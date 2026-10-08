using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using IisCertManager.Contracts;
namespace IisCertManager.Client;
public static class PipeClient
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
    static void VerifyServer(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid))
            throw new IOException("无法验证后台服务身份。");
        using var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        var image = new StringBuilder(32768); uint length = (uint)image.Capacity;
        if (process.IsInvalid || !QueryFullProcessImageName(process, 0, image, ref length))
            throw new IOException("无法读取后台服务程序路径。");
        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "service", "IisCertManager.Service.exe"));
        if (!string.Equals(image.ToString(), expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("控制管道不属于同一安装目录的后台服务，拒绝发送配置。请使用安装后的客户端。");
    }
    public static async Task<Response> Send(Request request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var pipe = new NamedPipeClientStream(".", Wire.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(5000, timeout.Token);
        VerifyServer(pipe);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, Wire.Json).AsMemory(), timeout.Token);
        var line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("服务未返回响应。");
        return JsonSerializer.Deserialize<Response>(line, Wire.Json) ?? throw new IOException("服务响应无效。");
    }
}
