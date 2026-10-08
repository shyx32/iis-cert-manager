using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IisCertManager.Contracts;
namespace IisCertManager.Service;
public sealed class PersistentState
{
    public Settings Settings { get; set; } = new();
    public List<Profile> Profiles { get; set; } = [];
}
public sealed class StateStore
{
    public string Root { get; }
    readonly ILogger<StateStore> logger;
    public PersistentState Data { get; }
    public SemaphoreSlim Gate { get; } = new(1, 1);
    readonly object logLock = new();
    public StateStore(ILogger<StateStore> logger, string? dataRoot = null)
    {
        this.logger = logger;
        Root = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IisCertManager");
        var directory = new DirectoryInfo(Root);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (directory.Exists)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("配置目录不能是链接或目录联接。");
            var owner = directory.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
            if (!administrators.Equals(owner) && !system.Equals(owner))
                throw new InvalidDataException("配置目录所有者不可信，请由管理员检查并重新创建该目录。");
        }
        var acl = new DirectorySecurity();
        acl.SetOwner(administrators);
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (!directory.Exists) directory.Create(acl); // 用受限 ACL 原子创建，避免普通用户预置状态目录。
        else directory.SetAccessControl(acl);
        var path = Path.Combine(Root, "state.dpapi");
        if (!File.Exists(path))
        {
            Data = File.Exists(path + ".bak") ? Load(path + ".bak") : new();
            if (File.Exists(path + ".bak")) { Save(); Log("主状态文件缺失，已恢复加密备份。"); }
        }
        else
        {
            try { Data = Load(path); }
            catch (Exception e) when (e is CryptographicException or JsonException or InvalidDataException)
            {
                // 不静默重置账户/规则；仅恢复可验证的同机加密备份。
                Data = Load(path + ".bak");
                File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
                Save();
                Log("主状态文件损坏，已恢复加密备份；请复核最近一次配置修改。");
            }
        }
    }
    static PersistentState Load(string path) => JsonSerializer.Deserialize<PersistentState>(Unprotect(File.ReadAllBytes(path)), Wire.Json)
        ?? throw new InvalidDataException("状态文件无效，请恢复备份。");
    public static byte[] Protect(string value) => ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.LocalMachine);
    public static string Unprotect(byte[] value) => Encoding.UTF8.GetString(ProtectedData.Unprotect(value, null, DataProtectionScope.LocalMachine));
    public void Save()
    {
        var path = Path.Combine(Root, "state.dpapi");
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, Protect(JsonSerializer.Serialize(Data, Wire.Json)));
        if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
    }
    public Settings PublicSettings() => Data.Settings with
    {
        AccessKeyId = "", AccessKeySecret = "", ClearDnsCredentials = false,
        HasDnsCredentials = DnsConfigured(Data.Settings)
    };
    public static bool DnsConfigured(Settings s) => !string.IsNullOrWhiteSpace(s.AliyunZone) &&
        !string.IsNullOrWhiteSpace(s.AccessKeyId) && !string.IsNullOrWhiteSpace(s.AccessKeySecret);
    public void UpdateSettings(Settings input)
    {
        if (!System.Net.Mail.MailAddress.TryCreate(input.Email, out var email) || email.Address != input.Email.Trim())
            throw new ArgumentException("请输入有效 ACME 联系邮箱。");
        if (input.RenewBeforeDays is < 7 or > 60) throw new ArgumentException("续期提前天数范围为 7–60。");
        if (!input.AcceptTerms) throw new ArgumentException("请阅读并同意 Let's Encrypt 服务条款。");
        var s = input with { Email = input.Email.Trim(), HasDnsCredentials = false, ClearDnsCredentials = false };
        if (!string.IsNullOrWhiteSpace(s.AliyunZone)) s.AliyunZone = Policy.Domain(s.AliyunZone);
        if (input.ClearDnsCredentials) { s.AccessKeyId = ""; s.AccessKeySecret = ""; s.AliyunZone = ""; }
        else
        {
            if (string.IsNullOrWhiteSpace(s.AccessKeyId)) s.AccessKeyId = Data.Settings.AccessKeyId;
            if (string.IsNullOrWhiteSpace(s.AccessKeySecret)) s.AccessKeySecret = Data.Settings.AccessKeySecret;
            if ((s.AccessKeyId.Length > 0 || s.AccessKeySecret.Length > 0 || s.AliyunZone.Length > 0) && !DnsConfigured(s))
                throw new ArgumentException("阿里云主域、AccessKey ID 和 Secret 必须完整填写；已有密钥留空表示保留。");
        }
        var old = Data.Settings;
        var journal = Path.Combine(Root, "dns-cleanup.json");
        if ((s.AliyunZone != old.AliyunZone || !DnsConfigured(s)) &&
            File.Exists(journal) && (JsonSerializer.Deserialize<List<DnsLease>>(File.ReadAllText(journal), Wire.Json)?.Count ?? 0) > 0)
            throw new InvalidOperationException("仍有待清理的 DNS TXT 记录。请恢复网络/权限并等待后台清理后再清除 DNS 配置或修改主域；可以更换同一主域的密钥以恢复清理。");
        Data.Settings = s;
        try { Save(); } catch { Data.Settings = old; throw; }
        Log("全局配置已保存；" + (s.Staging ? "测试 CA，不部署测试证书。" : "正式 CA。"));
    }
    public void Log(string message)
    {
        lock (logLock)
        {
            try
            {
                var path = Path.Combine(Root, "service.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".1", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {message}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { logger.LogWarning(e, "无法写入文件日志：{Message}", message); }
        }
    }
    public string[] Logs()
    {
        lock (logLock)
        {
            var path = Path.Combine(Root, "service.log");
            return File.Exists(path) ? File.ReadLines(path).TakeLast(120).ToArray() : [];
        }
    }
}
