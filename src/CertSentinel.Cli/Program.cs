using System.Text;
using CertSentinel.Cli;
using CertSentinel.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;

Console.OutputEncoding = Encoding.UTF8;

// 與服務共用設定:安裝目錄的 appsettings.json → %ProgramData%\CertSentinel\appsettings.json → 環境變數。
var configuration = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
    .AddJsonFile(SettingsPaths.ProgramDataSettingsFile, optional: true)
    .AddEnvironmentVariables()
    .Build();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

return await CliApp.RunAsync(args, Console.Out, Console.Error, new CliDependencies { Configuration = configuration }, cancellation.Token);
