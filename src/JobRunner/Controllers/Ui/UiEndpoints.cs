using System.Text.Json;
using JobRunner.Controllers.Jp1;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Services;
using JobRunner.Storage;
using JobRunner.Workers;

namespace JobRunner.Controllers.Ui;

/// <summary>監視画面に必要な読み取りモデルを提供します。</summary>
public static partial class UiEndpoints
{
    /// <summary>既存の操作APIとは独立した読み取りAPIを登録します。</summary>
    /// <param name="app">アプリケーションです。</param>
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/jobrunner/ui");
        MapEditor(group);
        group.MapGet("/monthly", ReadMonthlyAsync);
        group.MapGet("/attempts", ReadAttemptsAsync);
        group.MapGet("/logs", ReadLogsAsync);
        group.MapGet("/jobnets", async (StorageRepository storage, DefinitionService definitions, RunnerOptions options, SchedulerWorker worker, CancellationToken token) =>
        {
            var active = await ActiveAsync(storage, token);
            var units = (await definitions.ReadAsync(active.DefinitionHash, token)).Units.Flatten().Where(u => u.IsNetwork()).Select(u => new Network(u.Path, u.Name, ScheduleCalculator.Parent(u.Path))).ToList();
            return new Catalog(active.Id, active.DefinitionHash, options.TimeZone, worker.Phase, units);
        });
        group.MapGet("/executions", async (string rootPath, DateTimeOffset? from, DateTimeOffset? until, int? page, StorageRepository storage, CancellationToken token) =>
        {
            if (page < 0 || from > until)
            {
                throw new ArgumentException("検索期間またはページが不正です。");
            }

            var active = await ActiveAsync(storage, token);
            var roots = (await storage.ListAsync("occurrence:", token)).Select(StorageRepository.Read<Occurrence>)
                .Where(o => o.Namespace == active.Id && o.Id == o.RootId && o.RootPath == rootPath && (!from.HasValue || o.Due >= from) && (!until.HasValue || o.Due <= until))
                .OrderByDescending(o => o.Due).ThenBy(o => o.Id, StringComparer.Ordinal).ToList();
            var items = new List<Occurrence>();
            foreach (var root in roots.Skip((page ?? 0) * 20).Take(20))
            {
                if (root.ArchivePath is null)
                {
                    items.Add(root);
                }
                else
                {
                    var snapshot = await ReadRootAsync(root, storage, token);
                    items.Add(snapshot.Occurrences.Single(o => o.Id == root.Id));
                }
            }

            return new ExecutionPage(items, roots.Count);
        });
        group.MapGet("/graph", async (string unitPath, string? rootId, StorageRepository storage, DefinitionService definitions, CancellationToken token) =>
        {
            var active = await ActiveAsync(storage, token);
            var occurrences = new List<Occurrence>();
            var attempts = new List<JobAttempt>();
            string hash = active.DefinitionHash;
            Occurrence? root = null;
            bool archived = false;
            if (rootId is not null)
            {
                var row = await storage.GetAsync("occurrence:" + rootId, token) ?? throw new KeyNotFoundException("実行回がありません。");
                root = StorageRepository.Read<Occurrence>(row);
                if (root.Namespace != active.Id || root.Id != root.RootId)
                {
                    throw new ArgumentException("現在の採用領域のルート実行回を指定してください。");
                }

                archived = root.ArchivePath is not null;
                var snapshot = await ReadRootAsync(root, storage, token);
                occurrences = snapshot.Occurrences;
                attempts = snapshot.Attempts;
                root = occurrences.Single(o => o.Id == rootId);
                hash = root.DefinitionHash ?? active.DefinitionHash;
                if (unitPath != root.RootPath && !unitPath.StartsWith(root.RootPath + "/", StringComparison.Ordinal))
                {
                    throw new ArgumentException("実行回の配下のネットを指定してください。");
                }
            }

            var units = (await definitions.ReadAsync(hash, token)).Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
            if (!units.TryGetValue(unitPath, out var network) || !network.IsNetwork())
            {
                throw new KeyNotFoundException("ジョブネットがありません。");
            }

            var positions = DefinitionLayout.Read(network);
            var byPath = occurrences.ToDictionary(o => o.UnitPath, StringComparer.Ordinal);
            var byAttempt = attempts.ToDictionary(a => a.Id, StringComparer.Ordinal);
            var status = Jp1Status.CreateMapper(units, occurrences, DateTimeOffset.UtcNow);
            var nodes = network.Children.Select(unit =>
            {
                var occurrence = byPath.GetValueOrDefault(unit.Path);
                var attempt = occurrence?.AttemptId is string attemptId ? byAttempt.GetValueOrDefault(attemptId) : null;
                return new Node(unit.Path, unit.Name, unit.Type, unit.IsNetwork(), unit.IsRecovery(), occurrence is null ? "DEFINITION" : status(occurrence), occurrence, occurrence?.ManualExitCode ?? attempt?.ExitCode, positions[unit.Path].X, positions[unit.Path].Y);
            }).ToList();
            var edges = DefinitionValidator.Edges(network).Select((e, i) => new Edge(i.ToString(System.Globalization.CultureInfo.InvariantCulture), e.From, e.To, e.Conditional ? "condition" : units[e.To].IsRecovery() ? "recovery" : "normal")).ToList();
            bool mutable = root is not null && !archived && occurrences.All(o => !o.Started && o.StatusDetail != "REGISTRATION_CANCELLED" && o.State is "Waiting" or "Held" or "NotScheduled" or "Cancelled");
            return new Graph(unitPath, hash, nodes, edges, root, mutable && occurrences.All(o => o.State != "Cancelled"), mutable && occurrences.Any(o => o.ScheduleChangeId is not null));
        });
    }

    private static async Task<Activation> ActiveAsync(StorageRepository storage, CancellationToken token)
    {
        var row = await storage.GetAsync("definition-active", token);
        if (row is null || StorageRepository.Read<Activation>(row).Phase != "Ready")
        {
            throw new InvalidDataException("初期化が完了していません。定義とStorageを確認してください。");
        }

        // 分割更新中の途中状態を一つの完成した図として表示しません。
        if ((await storage.ListAsync("command:", token)).Select(StorageRepository.Read<Operation>).Any(o => o.Status == "Pending" && o.MutationPath is not null))
        {
            throw new InvalidDataException("予定変更の適用中です。再取得してください。");
        }

        return StorageRepository.Read<Activation>(row);
    }

    private static async Task<(List<Occurrence> Occurrences, List<JobAttempt> Attempts)> ReadRootAsync(Occurrence root, StorageRepository storage, CancellationToken token)
    {
        if (root.ArchivePath is not null)
        {
            return await ReadArchiveAsync(root, storage, token);
        }

        var occurrences = (await storage.ListAsync("occurrence:", token)).Select(StorageRepository.Read<Occurrence>).Where(o => o.RootId == root.Id).ToList();
        if (occurrences.All(o => o.AttemptId is null))
        {
            return (occurrences, []);
        }

        var ids = occurrences.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var attempts = (await storage.ListAsync("attempt:", token)).Select(StorageRepository.Read<JobAttempt>).Where(a => ids.Contains(a.OccurrenceId)).ToList();
        return (occurrences, attempts);
    }

    private static async Task<(List<Occurrence> Occurrences, List<JobAttempt> Attempts)> ReadArchiveAsync(Occurrence root, StorageRepository storage, CancellationToken token)
    {
        var content = await storage.Container.GetBlobClient(root.ArchivePath).DownloadContentAsync(token);
        var archive = content.Value.Content.ToObjectFromJson<HistoryArchive>() ?? throw new InvalidDataException("履歴がありません。");
        if (archive.RootId != root.Id || archive.FormatVersion != 1)
        {
            throw new InvalidDataException("履歴が一致しません。");
        }

        return (archive.Rows.Where(r => r.Key.StartsWith("occurrence:", StringComparison.Ordinal)).Select(r => JsonSerializer.Deserialize<Occurrence>(r.Json)!).ToList(), archive.Rows.Where(r => r.Key.StartsWith("attempt:", StringComparison.Ordinal)).Select(r => JsonSerializer.Deserialize<JobAttempt>(r.Json)!).ToList());
    }
}
