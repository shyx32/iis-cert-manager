using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;

namespace IisCertManager.Setup;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--silent" }))
        {
            try { InstallAsync().GetAwaiter().GetResult(); return 0; }
            catch (Exception ex) { Trace.WriteLine(ex); return 1; }
        }
        if (args.Length != 0) return 2;
        var app = new Application();
        var window = new Window
        {
            Title = "IIS 证书管家 — 安装", Width = 480, Height = 290,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        var text = new TextBlock
        {
            Text = "安装 IIS 证书管家\n\n自动安装本机管理界面、Windows 后台服务和桌面快捷方式。\n安装位置：Program Files\\IisCertManager\n请先启用 IIS 及 IIS 管理脚本和工具。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20)
        };
        var button = new Button { Content = "安装 / 更新", Height = 36, MinWidth = 130, HorizontalAlignment = HorizontalAlignment.Right };
        bool busy = false, installed = false;
        window.Closing += (_, e) => { if (busy) e.Cancel = true; };
        button.Click += async (_, _) =>
        {
            if (installed)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(ClientPath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(ClientPath)! });
                    window.Close();
                }
                catch (Exception ex) { MessageBox.Show(window, ex.Message, "无法打开程序"); }
                return;
            }
            busy = true; button.IsEnabled = false;
            text.Text = "正在安装，请稍候…\n正在部署程序、启动后台服务并创建桌面快捷方式。";
            try
            {
                await InstallAsync();
                installed = true;
                text.Text = "安装完成。\n\n后台服务已启动，关闭界面后仍会自动续期。\n可点击下方按钮或桌面快捷方式打开程序。";
                button.Content = "打开证书管家";
            }
            catch (Exception ex)
            {
                text.Text = "安装未完成。请检查提示后重试。";
                MessageBox.Show(window, ex.Message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { busy = false; button.IsEnabled = true; }
        };
        panel.Children.Add(text); panel.Children.Add(button); window.Content = panel;
        return app.Run(window);
    }

    private static string ClientPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "IisCertManager", "client", "IisCertManager.Client.exe");

    private static async Task InstallAsync()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("请允许 Windows 管理员权限提示后重试。");
        if (!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "Microsoft.Web.Administration.dll")))
            throw new InvalidOperationException("请先在 Windows 功能或服务器管理器中启用 IIS，以及 IIS 管理脚本和工具。");
        // Program Files inherits an ACL that prevents ordinary users from replacing elevated scripts.
        var stage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ".IisCertManagerSetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var log = Path.Combine(stage, "install.log");
        var success = false;
        try
        {
            using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                ?? throw new InvalidOperationException("安装包缺少程序文件，请重新下载完整安装程序。");
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read)) archive.ExtractToDirectory(stage);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var value in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(stage, "scripts", "Install.ps1"), "-PackageRoot", stage })
                start.ArgumentList.Add(value);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动安装程序。");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            await File.WriteAllTextAsync(log, await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false)).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException($"安装未完成（错误码 {process.ExitCode}）。请关闭已打开的证书管家后重试。详细信息：{log}");
            success = true;
        }
        catch (Exception ex) { throw new InvalidOperationException(ex.Message + "\n安装文件目录：" + stage, ex); }
        finally
        {
            if (success) { try { Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }
}
