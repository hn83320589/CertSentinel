using System.CommandLine;
using CertSentinel.Cli.Output;
using CertSentinel.Core.Checks;
using CertSentinel.Core.Notifications;
using CertSentinel.Core.Probing;
using CertSentinel.Core.Targets;
using CertSentinel.Infrastructure.Configuration;
using CertSentinel.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CertSentinel.Cli;

/// <summary>可替換的外部相依,讓整個 CLI 流程能在測試中以暫存資料庫與假探測器執行。</summary>
public sealed class CliDependencies
{
    public required IConfiguration Configuration { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public Func<ICertificateProbe> ProbeFactory { get; init; } =
        () => new TlsCertificateProbe(new DnsEndpointResolver(), new TlsProbeOptions());
}

public static class CliApp
{
    public const int Success = 0;
    public const int InputError = 1;
    public const int RuntimeError = 2;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CliDependencies dependencies, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        var dbOption = new Option<string?>("--db")
        {
            Description = "資料庫檔路徑(預設讀取 appsettings.json 的 CertSentinel:DatabasePath)",
            Recursive = true,
        };
        var jsonOption = new Option<bool>("--json") { Description = "以 JSON 輸出" };
        var nameOption = new Option<string?>("--name") { Description = "顯示名稱(預設為 主機:埠號)" };
        var daysOption = new Option<int>("--days") { Description = "顯示近 N 天的紀錄", DefaultValueFactory = _ => 7 };
        Argument<string> TargetArgument() => new("target")
        {
            Description = "example.com、example.com:8443 或 https://example.com:8443/path",
        };

        var root = new RootCommand("CertSentinel — SSL/TLS 憑證到期監控工具") { dbOption };

        var addTarget = TargetArgument();
        var add = new Command("add", "新增監控目標") { addTarget, nameOption };
        add.SetAction((parse, ct) => RunWithTarget(parse, ct, async (ctx, target) =>
        {
            var result = await ctx.Targets.AddAsync(target, parse.GetValue(nameOption), dependencies.Time.GetUtcNow(), ct);
            if (result.Outcome == AddTargetOutcome.Duplicate)
            {
                return Fail($"目標 {target} 已存在(名稱:{result.Target.Name}),同一主機與埠號不可重複新增。");
            }

            var label = result.Target.Name == target.ToString() ? target.ToString() : $"{result.Target.Name}({target})";
            await output.WriteLineAsync($"已新增 {label}。");
            return Success;
        }, addTarget));

        var list = new Command("list", "列出監控清單") { jsonOption };
        list.SetAction((parse, ct) => Run(parse, ct, async ctx =>
        {
            var targets = await ctx.Targets.ListAsync(ct);
            await output.WriteAsync(parse.GetValue(jsonOption) ? Views.Json(Views.TargetListJson(targets)) + Environment.NewLine : ctx.Views.TargetList(targets));
            return Success;
        }));

        Command Toggle(string name, string description, Func<Context, TargetAddress, CancellationToken, Task<bool>> apply, string done)
        {
            var argument = TargetArgument();
            var command = new Command(name, description) { argument };
            command.SetAction((parse, ct) => RunWithTarget(parse, ct, async (ctx, target) =>
            {
                if (!await apply(ctx, target, ct))
                {
                    return NotFound(target);
                }

                await output.WriteLineAsync($"已{done} {target}。");
                return Success;
            }, argument));
            return command;
        }

        var remove = Toggle("remove", "移除監控目標(連同歷史紀錄)", (ctx, t, ct) => ctx.Targets.RemoveAsync(t, ct), "移除");
        var enable = Toggle("enable", "啟用監控目標", (ctx, t, ct) => ctx.Targets.SetEnabledAsync(t, true, ct), "啟用");
        var disable = Toggle("disable", "停用監控目標(保留歷史)", (ctx, t, ct) => ctx.Targets.SetEnabledAsync(t, false, ct), "停用");

        var status = new Command("status", "顯示所有目標目前狀態") { jsonOption };
        status.SetAction((parse, ct) => Run(parse, ct, async ctx =>
        {
            var rows = await ctx.Checks.GetStatusAsync(ct);
            await output.WriteAsync(parse.GetValue(jsonOption) ? Views.Json(Views.StatusJson(rows)) + Environment.NewLine : ctx.Views.Status(rows));
            return Success;
        }));

        var checkTarget = TargetArgument();
        var check = new Command("check", "立即檢查單一目標(寫入歷史,不觸發通知)") { checkTarget, jsonOption };
        check.SetAction((parse, ct) => RunWithTarget(parse, ct, async (ctx, address) =>
        {
            if (await ctx.Targets.FindAsync(address, ct) is not { } target)
            {
                return NotFound(address);
            }

            var record = await ctx.CreateCoordinator().CheckOnceAsync(target, ct);
            await output.WriteAsync(parse.GetValue(jsonOption) ? Views.Json(Views.CheckJson(target, record)) + Environment.NewLine : ctx.Views.CheckDetail(target, record));
            return Success;
        }, checkTarget));

        var historyTarget = TargetArgument();
        var history = new Command("history", "顯示目標近 N 天的檢查紀錄") { historyTarget, daysOption, jsonOption };
        history.SetAction((parse, ct) => RunWithTarget(parse, ct, async (ctx, address) =>
        {
            var days = parse.GetValue(daysOption);
            if (days <= 0)
            {
                return Fail($"--days 必須是正整數(目前為 {days})。");
            }

            if (await ctx.Targets.FindAsync(address, ct) is not { } target)
            {
                return NotFound(address);
            }

            // 取完整歷史再篩選,讓視窗內最早一筆也能和更早的紀錄比對指紋。
            var since = dependencies.Time.GetUtcNow().AddDays(-days);
            var annotated = CertificateRenewal.Annotate(await ctx.Checks.GetHistoryAsync(target.Id, DateTimeOffset.MinValue, ct))
                .Where(h => h.Record.CheckedUtc >= since)
                .ToList();
            await output.WriteAsync(parse.GetValue(jsonOption) ? Views.Json(Views.HistoryJson(annotated)) + Environment.NewLine : ctx.Views.History(annotated));
            return Success;
        }, historyTarget));

        root.Subcommands.Add(add);
        root.Subcommands.Add(list);
        root.Subcommands.Add(remove);
        root.Subcommands.Add(enable);
        root.Subcommands.Add(disable);
        root.Subcommands.Add(status);
        root.Subcommands.Add(check);
        root.Subcommands.Add(history);

        var parseResult = root.Parse(args);
        return await parseResult.InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, cancellationToken);

        int Fail(string message)
        {
            error.WriteLine($"錯誤:{message}");
            return InputError;
        }

        int NotFound(TargetAddress target) =>
            Fail($"找不到目標 {target}。可用 certsentinel list 查看清單,或先以 certsentinel add 新增。");

        Task<int> RunWithTarget(ParseResult parse, CancellationToken ct, Func<Context, TargetAddress, Task<int>> body, Argument<string> argument)
        {
            if (!TargetParser.TryParse(parse.GetValue(argument), out var target, out var parseError))
            {
                return Task.FromResult(Fail($"目標格式不合法:{parseError}"));
            }

            return Run(parse, ct, ctx => body(ctx, target));
        }

        async Task<int> Run(ParseResult parse, CancellationToken ct, Func<Context, Task<int>> body)
        {
            var settings = CertSentinelSettings.Bind(dependencies.Configuration);
            if (parse.GetValue(dbOption) is { Length: > 0 } db)
            {
                settings.DatabasePath = db;
            }

            if (settings.Validate(requireSmtp: false) is { Count: > 0 } errors)
            {
                return Fail("設定檔有誤:" + string.Join(" ", errors));
            }

            try
            {
                var database = new SqliteDatabase(settings.DatabasePath);
                await database.InitializeAsync(ct);
                return await body(new Context(database, settings, dependencies));
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                await error.WriteLineAsync($"錯誤:無法存取資料庫 {settings.DatabasePath}:{ex.Message}");
                return RuntimeError;
            }
        }
    }

    private sealed class Context(SqliteDatabase database, CertSentinelSettings settings, CliDependencies dependencies)
    {
        public SqliteTargetRepository Targets { get; } = new(database);

        public SqliteCheckStore Checks { get; } = new(database);

        public Views Views { get; } = new(dependencies.Time.LocalTimeZone);

        /// <summary>CLI 只使用 CheckOnceAsync(不寄信),因此寄信器為不會被呼叫的空實作。</summary>
        public CheckCoordinator CreateCoordinator() => new(
            Targets, Checks, dependencies.ProbeFactory(), NoEmail.Instance, dependencies.Time,
            settings.ToCheckOptions(), NullLogger<CheckCoordinator>.Instance);
    }

    private sealed class NoEmail : IEmailSender
    {
        public static NoEmail Instance { get; } = new();

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("CLI 不寄送通知。");
    }
}
