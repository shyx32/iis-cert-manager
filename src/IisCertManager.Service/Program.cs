using IisCertManager.Service;
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(x => x.ServiceName = "IisCertManager");
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<IisManager>();
builder.Services.AddSingleton<CertificateManager>();
builder.Services.AddHostedService<ControlServer>();
builder.Services.AddHostedService<RenewalWorker>();
await builder.Build().RunAsync();
