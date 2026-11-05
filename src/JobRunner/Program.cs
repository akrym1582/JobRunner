using System.Text.Json;
using Azure;
using JobRunner.Execution;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using JobRunner.Workers;

using Operation = JobRunner.Models.Operation;

namespace JobRunner;

/// <summary>認証や画面を持たない管理APIと、稼働系のみの実行サービスを起動します。</summary>
public static class Program
{
    /// <summary>構成を読み取り、HTTP受付とスケジューラーを同じホストで実行します。</summary>
    /// <param name="args">ASP.NET Coreのコマンドライン構成です。</param>
    /// <returns>ホスト停止時に完了します。</returns>
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var options = builder.Configuration.GetSection("JobRunner").Get<RunnerOptions>() ?? new RunnerOptions();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<StorageRepository>();
        builder.Services.AddSingleton<DefinitionService>();
        builder.Services.AddSingleton<ProcessJobExecutor>();
        builder.Services.AddSingleton<SchedulerEngine>();
        builder.Services.AddSingleton<SchedulerWorker>();
        builder.Services.AddHostedService(p => p.GetRequiredService<SchedulerWorker>());
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidDataException or KeyNotFoundException)
            {
                await Results.Problem(ex.Message, statusCode: 400).ExecuteAsync(context);
            }
            catch (RequestFailedException)
            {
                await Results.Problem("Storageへの要求が失敗しました。操作IDで結果を再照会してください。", statusCode: 503).ExecuteAsync(context);
            }
        });
        Controllers.Jp1.Jp1Endpoints.Map(app);
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", async (StorageRepository storage, CancellationToken token) =>
        {
            await storage.Container.GetPropertiesAsync(cancellationToken: token);
            await storage.GetAsync("leader", token);
            return Results.Ok(new { status = "ready" });
        });
        app.MapGet("/api/jobrunner/status", (SchedulerWorker worker, SchedulerEngine engine, StorageRepository storage) => new { worker.IsLeader, worker.Epoch, worker.Phase, engine.Namespace, storage.RequestCount });
        app.MapGet("/api/jobrunner/configuration", () => new { options.SchedulerId, options.Jp1ManagerName, options.DefinitionSources, options.ScheduleTimeMode, options.TimeZone, options.ConfirmedHorizonDays, options.ConfirmedFutureGenerations, options.LocalAgents, options.LocalUsers, options.HistoryArchiveAfterDays, options.LogRetentionDays, options.OperationArchiveAfterDays });
        app.MapGet("/api/jobrunner/compatibility", () => new
        {
            reference = "JP1/AJS3 V12 3021-3-D28-20",
            comparedWithJp1 = false,
            supportedJp1Actions = new[] { "registerImmediateExec", "registerFixedExec (futureGeneration/NEXTTIME)", "changeHold" },
            unsupported = new[] { "その他のJP1 HTTP操作・完全なエラーID互換", "別形式Schedule/Calendar", "イベント", "待ち合わせ", "条件接続", "相対日", "24時間制の24時以降", "ネストの独立スケジュール", "OSユーザー切り替え" },
        });
        app.MapPost("/api/jobrunner/definitions/validate", (List<SourceEvidence> sources, DefinitionService definitions) => definitions.Validate(sources));
        app.MapPost("/api/jobrunner/definitions/import", async (List<SourceEvidence> sources, DefinitionService definitions, CancellationToken token) =>
        {
            var bundle = definitions.Validate(sources);
            await definitions.SaveAsync(bundle, token);
            return Results.Ok(new { version = bundle.Hash });
        });
        app.MapPost("/api/jobrunner/operations", EnqueueAsync);
        app.MapPost("/api/jobrunner/definitions/{version}/activate", (string version, Operation operation, StorageRepository storage, CancellationToken token) =>
        {
            operation.Kind = "Activate";
            operation.Target = version;
            return EnqueueAsync(operation, storage, token);
        });
        app.MapPost("/api/jobrunner/executions/{id}/resolve", (string id, Operation operation, StorageRepository storage, CancellationToken token) =>
        {
            operation.Kind = "Resolve";
            operation.Target = id;
            return EnqueueAsync(operation, storage, token);
        });
        app.MapGet("/api/jobrunner/operations/{id}", async (string id, StorageRepository storage, CancellationToken token) =>
        {
            var row = await storage.GetAsync("command:" + StorageRepository.Hash(id), token);
            return row is null ? Results.NotFound() : Results.Ok(await HistoryRetentionService.ReadOperationAsync(row, storage, token));
        });
        app.MapGet("/api/jobrunner/executions", async (StorageRepository storage, CancellationToken token) =>
            (await storage.ListAsync("occurrence:", token)).Select(StorageRepository.Read<Occurrence>));
        app.MapGet("/api/jobrunner/executions/{id}/archive", async (string id, StorageRepository storage, CancellationToken token) =>
        {
            var row = await storage.GetAsync("occurrence:" + id, token);
            var occurrence = row is null ? null : StorageRepository.Read<Occurrence>(row);
            if (occurrence?.ArchivePath is null)
            {
                return Results.NotFound();
            }

            var data = await storage.Container.GetBlobClient(occurrence.ArchivePath).DownloadContentAsync(token);
            return Results.Ok(data.Value.Content.ToObjectFromJson<HistoryArchive>());
        });
        app.MapGet("/api/jobrunner/registrations", async (StorageRepository storage, CancellationToken token) =>
            (await storage.ListAsync("registration:", token)).Select(StorageRepository.Read<Registration>));
        app.MapGet("/api/jobrunner/executions/{id}/attempts", async (string id, StorageRepository storage, CancellationToken token) =>
            (await storage.ListAsync("attempt:", token)).Select(StorageRepository.Read<JobAttempt>).Where(a => a.OccurrenceId == id));
        app.MapGet("/api/jobrunner/executions/{id}/logs", async (string id, StorageRepository storage, CancellationToken token) =>
        {
            var paths = new List<string>();
            await foreach (var blob in storage.Container.GetBlobsAsync(prefix: $"logs/{id}/", cancellationToken: token))
            {
                paths.Add(blob.Name);
            }

            return Results.Ok(paths);
        });
        app.MapGet("/api/jobrunner/logs", async (string path, StorageRepository storage, CancellationToken token) =>
        {
            if (!path.StartsWith("logs/", StringComparison.Ordinal))
            {
                return Results.BadRequest();
            }

            var data = await storage.Container.GetBlobClient(path).DownloadContentAsync(token);
            return Results.Text(data.Value.Content.ToString());
        });
        app.MapPost("/api/jobrunner/schedules/preview", async (PreviewRequest request, DefinitionService definitions, CancellationToken token) =>
        {
            var bundle = await definitions.ReadAsync(request.Version, token);
            var all = bundle.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
            var registration = new Registration { RootPath = request.UnitPath, Anchor = request.Anchor };
            return Scheduling.ScheduleCalculator.Calculate(all[request.UnitPath], all, registration, request.From, request.Until, options).ToList();
        });
        await app.RunAsync();
    }

    /// <summary>どのインスタンスでも操作要求を保存し、同一IDの異なる内容を拒否します。</summary>
    /// <param name="operation">クライアントが指定する操作です。</param>
    /// <param name="storage">同じ制御Tableを共有するアダプターです。</param>
    /// <param name="token">HTTP要求中断の通知です。</param>
    /// <returns>既存結果または202の操作照会先です。</returns>
    private static async Task<IResult> EnqueueAsync(Operation operation, StorageRepository storage, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(operation.Id) || operation.Id.Length > 128)
        {
            return Results.BadRequest(new { error = "操作IDは1〜128文字で指定してください。" });
        }

        operation.Status = "Pending";
        operation.Result = null;
        operation.CreatedAt = null;
        operation.CompletedAt = null;
        operation.ArchivePath = null;
        operation.MutationPath = null;
        operation.RequestHash = string.Empty;
        operation.StateNamespace = string.Empty;
        operation.RequestHash = StorageRepository.Hash(JsonSerializer.Serialize(operation));
        operation.CreatedAt = DateTimeOffset.UtcNow;
        string key = "command:" + StorageRepository.Hash(operation.Id);
        var active = await storage.GetAsync("definition-active", token);
        if (active is null)
        {
            return Results.Problem("初期化が完了していません。", statusCode: 503);
        }

        operation.StateNamespace = StorageRepository.Read<Activation>(active).Id;
        try
        {
            await storage.Table.AddEntityAsync(storage.Entity(key, operation), token);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            var current = StorageRepository.Read<Operation>((await storage.GetAsync(key, token))!);
            return current.RequestHash == operation.RequestHash ? Results.Ok(await HistoryRetentionService.ReadOperationAsync((await storage.GetAsync(key, token))!, storage, token)) : Results.Conflict(new { error = "同じ操作IDに異なる内容が指定されています。" });
        }

        return Results.Accepted($"/api/jobrunner/operations/{Uri.EscapeDataString(operation.Id)}", operation);
    }
}
