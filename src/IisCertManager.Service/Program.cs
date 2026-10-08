using IisCertManager.Service;
// 防止服务与管理员手动启动的第二个进程同时操作 IIS、账户私钥和清理日志。
using var singleInstance = new Mutex(false, @"Global\IisCertManager.Service", out var createdNew);
if (!createdNew) throw new InvalidOperationException("IIS 证书服务已运行，不能重复启动。");
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(x => x.ServiceName = "IisCertManager");
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<IisManager>();
builder.Services.AddSingleton<CertificateManager>();
builder.Services.AddHostedService<ControlServer>();
builder.Services.AddHostedService<RenewalWorker>();
await builder.Build().RunAsync();
