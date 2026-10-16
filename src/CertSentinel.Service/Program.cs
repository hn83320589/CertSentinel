using CertSentinel.Core.Checks;
using CertSentinel.Core.Notifications;
using CertSentinel.Core.Probing;
using CertSentinel.Infrastructure.Configuration;
using CertSentinel.Infrastructure.Email;
using CertSentinel.Infrastructure.Storage;
using CertSentinel.Service;

const string ServiceName = "CertSentinel";

var builder = Host.CreateApplicationBuilder(args);

// 設定來源:安裝目錄的 appsettings.json → %ProgramData%\CertSentinel\appsettings.json(覆寫,升級不會被蓋掉)→ 環境變數。
builder.Configuration.AddJsonFile(SettingsPaths.ProgramDataSettingsFile, optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));

// 日誌寫入 Windows 事件檢視器(應用程式記錄,來源 CertSentinel;事件來源由 install-service.ps1 註冊)。
// EventLogSettings 不會從設定檔繫結,必須在程式碼中指定;否則服務模式下來源會變成未註冊的程式名稱而寫不進去。
if (OperatingSystem.IsWindows())
{
#pragma warning disable CA1416 // 已由 OperatingSystem.IsWindows() 保護,分析器無法追蹤到 lambda 內。
    builder.Services.Configure<Microsoft.Extensions.Logging.EventLog.EventLogSettings>(options =>
    {
        options.SourceName = ServiceName;
        options.LogName = "Application";
    });
#pragma warning restore CA1416
}

var settings = CertSentinelSettings.Bind(builder.Configuration);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new SqliteDatabase(settings.DatabasePath));
builder.Services.AddSingleton<ITargetRepository, SqliteTargetRepository>();
builder.Services.AddSingleton<ICheckStore, SqliteCheckStore>();
builder.Services.AddSingleton<ICertificateProbe>(new TlsCertificateProbe(new DnsEndpointResolver(), new TlsProbeOptions()));
builder.Services.AddSingleton<IEmailSender>(new SmtpEmailSender(settings.Smtp));
builder.Services.AddSingleton(_ => settings.ToCheckOptions());
builder.Services.AddSingleton<CheckCoordinator>();
builder.Services.AddSingleton<IRoundRunner>(sp => new CoordinatorRoundRunner(
    sp.GetRequiredService<SqliteDatabase>(),
    sp.GetRequiredService<CheckCoordinator>(),
    TimeSpan.FromDays(settings.RetentionDays)));
builder.Services.AddHostedService(sp => new CheckWorker(
    sp.GetRequiredService<IRoundRunner>(),
    sp.GetRequiredService<TimeProvider>(),
    TimeSpan.FromMinutes(settings.CheckIntervalMinutes),
    sp.GetRequiredService<ILogger<CheckWorker>>()));

using var host = builder.Build();

var errors = settings.Validate();
if (errors.Count > 0)
{
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CertSentinel.Startup");
#pragma warning disable CA1848 // 啟動失敗只會記錄一次,不需要 LoggerMessage。
    logger.LogCritical("設定檔有誤,服務無法啟動:{Errors}", string.Join(Environment.NewLine, errors));
#pragma warning restore CA1848
    return 1;
}

await host.RunAsync();
return 0;
