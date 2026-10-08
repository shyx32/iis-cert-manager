using System.Collections.Concurrent;
using System.Net;
using System.Text;
namespace IisCertManager.Service;
public sealed class HttpChallenge : IAsyncDisposable
{
    readonly HttpListener listener = new();
    readonly ConcurrentDictionary<string,string> tokens = new(StringComparer.Ordinal);
    readonly CancellationTokenSource stop = new();
    readonly Task loop;
    public HttpChallenge()
    {
        // HTTP.sys 对这一特定路径提供服务，与 IIS 的其他路径共存。
        listener.Prefixes.Add("http://+:80/.well-known/acme-challenge/");
        try { listener.Start(); }
        catch { listener.Close(); stop.Dispose(); throw; }
        loop = Serve();
    }
    public void Add(string host, string token, string content) => tokens[host.ToLowerInvariant() + "/" + token] = content;
    async Task Serve()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or HttpListenerException or ObjectDisposedException) { break; }
            try
            {
                var url = context.Request.Url;
                const string prefix = "/.well-known/acme-challenge/";
                if (url == null || !url.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
                { context.Response.StatusCode = 404; continue; }
                var key = url.Host.ToLowerInvariant() + "/" + url.AbsolutePath[prefix.Length..];
                if (context.Request.HttpMethod == "GET" && tokens.TryGetValue(key, out var value))
                {
                    var bytes = Encoding.ASCII.GetBytes(value);
                    context.Response.ContentType = "text/plain";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, stop.Token);
                }
                else context.Response.StatusCode = 404;
            }
            catch (Exception e) when (e is IOException or HttpListenerException or OperationCanceledException) { }
            finally { try { context.Response.Close(); } catch (HttpListenerException) { } }
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Close(); await loop; stop.Dispose();
    }
}
