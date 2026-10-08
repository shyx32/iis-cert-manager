using System.Security.Cryptography;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using IisCertManager.Contracts;
namespace IisCertManager.Service;
public sealed class CertificateManager(StateStore store, IisManager iis)
{
    public void Validate(Profile p)
    {
        iis.Validate(p);
        var settings = store.Data.Settings;
        var mode = Policy.Resolve(p.Mode, StateStore.DnsConfigured(settings), p.Domains);
        if (mode == ChallengeMode.Dns01)
            foreach (var domain in p.Domains) Policy.RelativeRecord("_acme-challenge." + domain.Replace("*.", ""), settings.AliyunZone);
        if (!settings.AcceptTerms || string.IsNullOrWhiteSpace(settings.Email))
            throw new InvalidOperationException("请先保存联系邮箱并同意 CA 服务条款。");
    }
    public async Task Issue(Profile p, CancellationToken serviceStop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serviceStop);
        timeout.CancelAfter(TimeSpan.FromMinutes(18));
        var ct = timeout.Token;
        var settings = store.Data.Settings;
        p.LastAttempt = DateTimeOffset.UtcNow;
        HttpChallenge? http = null;
        using var dns = new AliyunDns(store, settings);
        try
        {
            Validate(p);
            var mode = Policy.Resolve(p.Mode, StateStore.DnsConfigured(settings), p.Domains);
            p.Status = $"正在签发：{mode}"; store.Save();
            store.Log($"{p.SiteName}/{p.Host}：开始 {(settings.Staging ? "测试" : "正式")} 签发，{mode}。");
            await dns.Cleanup(ct);
            var keyPath = Path.Combine(store.Root, settings.Staging ? "account-staging.dpapi" : "account-production.dpapi");
            var key = File.Exists(keyPath) ? KeyFactory.FromPem(StateStore.Unprotect(File.ReadAllBytes(keyPath))) : KeyFactory.NewKey(KeyAlgorithm.ES256);
            if (!File.Exists(keyPath)) File.WriteAllBytes(keyPath, StateStore.Protect(key.ToPem()));
            var acme = new AcmeContext(settings.Staging ? WellKnownServers.LetsEncryptStagingV2 : WellKnownServers.LetsEncryptV2, key);
            await acme.NewAccount(settings.Email, true).WaitAsync(ct);
            var order = await acme.NewOrder(p.Domains).WaitAsync(ct);
            var authorizations = (await order.Authorizations().WaitAsync(ct)).ToList();
            var challenges = new List<(IAuthorizationContext Auth, IChallengeContext Challenge)>();
            var leases = new List<DnsLease>();
            foreach (var auth in authorizations)
            {
                var resource = await auth.Resource().WaitAsync(ct);
                if (resource.Status == AuthorizationStatus.Valid) continue;
                var host = resource.Identifier.Value;
                if (mode == ChallengeMode.Http01)
                {
                    http ??= new HttpChallenge();
                    var challenge = await auth.Http().WaitAsync(ct);
                    http.Add(host, challenge.Token, challenge.KeyAuthz);
                    challenges.Add((auth, challenge));
                }
                else
                {
                    var challenge = await auth.Dns().WaitAsync(ct);
                    leases.Add(await dns.Create("_acme-challenge." + host.Replace("*.", ""), acme.AccountKey.DnsTxt(challenge.Token), ct));
                    challenges.Add((auth, challenge));
                }
            }
            foreach (var lease in leases)
            {
                store.Log($"等待 TXT 传播：{lease.Name}");
                await dns.WaitPropagation(lease, ct);
            }
            foreach (var pair in challenges) await pair.Challenge.Validate().WaitAsync(ct);
            foreach (var pair in challenges)
            {
                var valid = false;
                for (var attempt = 0; attempt < 90; attempt++)
                {
                    var auth = await pair.Auth.Resource().WaitAsync(ct);
                    if (auth.Status == AuthorizationStatus.Valid) { valid = true; break; }
                    if (auth.Status is AuthorizationStatus.Invalid or AuthorizationStatus.Deactivated or AuthorizationStatus.Expired or AuthorizationStatus.Revoked)
                        throw new InvalidOperationException($"CA 域名验证失败：{auth.Identifier.Value}（{auth.Status}），请检查公网 DNS 和端口可达性。");
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
                if (!valid) throw new TimeoutException("CA 域名验证超时。");
            }
            // 等待订单从 pending 变为 ready 后再 finalize。
            var ready = false;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                var orderState = await order.Resource().WaitAsync(ct);
                if (orderState.Status == OrderStatus.Ready) { ready = true; break; }
                if (orderState.Status == OrderStatus.Invalid) throw new InvalidOperationException("ACME 订单无效。");
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            if (!ready) throw new TimeoutException("ACME 订单未就绪。");
            var certKey = KeyFactory.NewKey(KeyAlgorithm.RS256);
            var chain = await order.Generate(new CsrInfo { CommonName = p.Domains[0] }, certKey, retryCount: 30).WaitAsync(ct);
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var pfx = chain.ToPfx(certKey).Build("IIS Cert Manager - " + p.Host, password);
            if (settings.Staging)
            {
                p.Status = "测试签发成功（未安装，不受浏览器信任）";
                store.Log($"{p.Host}：测试签发完成，未修改 IIS。");
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                iis.Install(p, pfx, password);
                p.Status = "已签发并绑定 HTTPS";
                store.Log($"{p.Host}：部署完成，指纹 {p.Thumbprint}，到期 {p.Expires:O}。");
            }
            p.Failures = 0; p.NextAttempt = DateTimeOffset.UtcNow.AddHours(12); store.Save();
        }
        catch (Exception error)
        {
            p.Failures++;
            p.NextAttempt = DateTimeOffset.UtcNow.AddMinutes(Math.Min(1440, 15 * Math.Pow(2, Math.Min(p.Failures - 1, 7))));
            var message = error is OperationCanceledException ? "操作超时或服务正在停止" : error.Message;
            p.Status = "失败：" + message; store.Save();
            store.Log($"{p.Host}：{p.Status}；下次尝试 {p.NextAttempt:O}。");
            throw new InvalidOperationException(message, error);
        }
        finally
        {
            if (http != null) await http.DisposeAsync();
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await dns.Cleanup(cleanupTimeout.Token); }
            catch (Exception) { store.Log("DNS 清理超时，已保存待清理记录；下一次运行会重试。"); }
        }
    }
}
