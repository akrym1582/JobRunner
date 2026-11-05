using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs.Models;
using JobRunner.Execution;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Storage;
using JobRunner.Workers;

using Operation = JobRunner.Models.Operation;

namespace JobRunner.Services;

/// <summary>稼働系の状態変更を直列化し、永続化後にだけ実行へ進めます。</summary>
/// <param name="storage">状態更新と復旧に使用する永続化アダプターです。</param>
/// <param name="definitions">不変定義の取得・検証・保存を担当します。</param>
/// <param name="executor">同じマシン上でだけジョブを起動する実行器です。</param>
/// <param name="options">先読み期間、同時実行数、時間制などの実効設定です。</param>
/// <param name="clock">予定と復旧基準の取得に使う注入可能な時計です。</param>
public sealed class SchedulerEngine(StorageRepository storage, DefinitionService definitions, ProcessJobExecutor executor, RunnerOptions options, TimeProvider clock)
{
    /// <summary>実行結果とAPI操作を同じTable更新経路へ直列化します。</summary>
    private readonly SemaphoreSlim mutations = new(1, 1);

    /// <summary>このプロセスが起動したジョブだけを追跡します。再起動時は復元しません。</summary>
    private readonly Dictionary<string, (Task<JobAttempt> Task, CancellationTokenSource Stop)> running = new(StringComparer.Ordinal);

    /// <summary>Tableで確定した採用情報のキャッシュです。</summary>
    private Activation? active;

    /// <summary>現在の採用に対応する不変定義のキャッシュです。</summary>
    private DefinitionBundle? bundle;

    /// <summary>すべての状態変更とOS起動の直前に確認する権限です。</summary>
    private LeaderCoordinator? leader;

    /// <summary>Gets. 現在採用している状態領域。初期化前はnullです。</summary>
    public string? Namespace => active?.Id;

    /// <summary>稼働系取得後、入力と保存済み状態を照合して復旧します。</summary>
    /// <param name="coordinator">取得済みの稼働系権限です。</param>
    /// <param name="cancellationToken">権限喪失または停止の通知です。</param>
    /// <returns>初期化と結果不明処理の完了を表します。</returns>
    public async Task InitializeAsync(LeaderCoordinator coordinator, CancellationToken cancellationToken)
    {
        leader = coordinator;
        var loaded = await definitions.LoadSourcesAsync(cancellationToken);
        await definitions.SaveAsync(loaded, cancellationToken);
        await ActivateAsync(loaded.Hash, cancellationToken);
        DateTimeOffset recoveryTime = clock.GetUtcNow();

        // 新版を採用しても旧版の未解決試行を消さず、同じ業務の並行起動を防ぎます。
        foreach (var row in await storage.ListAsync("occurrence:", cancellationToken))
        {
            var occurrence = StorageRepository.Read<Occurrence>(row);
            if (occurrence.State is "Starting" or "Running" && occurrence.AttemptId is not null)
            {
                occurrence.State = "Unknown";
                occurrence.Reason = "旧所有者のプロセス結果を確認できないため自動再実行しません。";
                var attemptRow = await storage.GetAsync("attempt:" + occurrence.AttemptId, cancellationToken) ?? throw new InvalidDataException("実行試行が欠損しています。");
                var attempt = StorageRepository.Read<JobAttempt>(attemptRow);
                attempt.State = "Unknown";
                await CommitAsync([Update(row, occurrence), Update(attemptRow, attempt)], cancellationToken);
            }
            else if (occurrence.Namespace != active!.Id && occurrence.AttemptId is null && occurrence.State == "Running")
            {
                occurrence.State = "Superseded";
                occurrence.Reason = "旧版のネットです。子の結果不明試行は別途保持します。";
                await CommitAsync([Update(row, occurrence)], cancellationToken);
            }
            else if (occurrence.Namespace == active!.Id && occurrence.Id == occurrence.RootId && !occurrence.Started && occurrence.State == "Waiting" && occurrence.Due < recoveryTime)
            {
                occurrence.State = "Skipped";
                occurrence.Reason = "復旧基準時刻より前の未開始予定です。";
                await CommitAsync([Update(row, occurrence)], cancellationToken);
            }
        }
    }

    /// <summary>操作、補充、完了結果、依存関係の順に一回の処理を進めます。</summary>
    /// <param name="cancellationToken">稼働系権限喪失の通知です。</param>
    /// <returns>現在の処理可能な状態変更を終えたときに完了します。</returns>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        leader!.Ensure();
        await CollectAsync(cancellationToken);
        foreach (var commandRow in await storage.ListAsync("command:", cancellationToken))
        {
            var operation = StorageRepository.Read<Operation>(commandRow);
            if (operation.Status == "Pending")
            {
                await ApplyAsync(commandRow, operation, cancellationToken);
            }
        }

        foreach (var registrationRow in await storage.ListAsync($"registration:{active!.Id}:", cancellationToken))
        {
            await ReplenishAsync(registrationRow, cancellationToken);
        }

        var rows = await storage.ListAsync("occurrence:", cancellationToken);
        var all = rows.ToDictionary(r => StorageRepository.Read<Occurrence>(r).Id, r => (Row: r, Value: StorageRepository.Read<Occurrence>(r)), StringComparer.Ordinal);
        var units = bundle!.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        foreach (var network in all.Values.Where(v => v.Value.Namespace == active!.Id && units[v.Value.UnitPath].Type == "n"))
        {
            foreach (var child in units[network.Value.UnitPath].Children)
            {
                if (!all.Values.Any(v => v.Value.RootId == network.Value.RootId && v.Value.UnitPath == child.Path))
                {
                    throw new InvalidDataException("世代の子ユニットが欠損しています。成功扱いや部分実行はできません。");
                }
            }
        }

        foreach (var item in all.Values.Where(v => v.Value.Namespace == active!.Id).OrderBy(v => v.Value.Due).ThenBy(v => v.Value.UnitPath, StringComparer.Ordinal))
        {
            var occurrence = item.Value;
            if (occurrence.State == "Waiting" && occurrence.Due <= clock.GetUtcNow())
            {
                var unit = units[occurrence.UnitPath];
                var registrationRow = await storage.GetAsync($"registration:{occurrence.Namespace}:{occurrence.RegistrationId}", cancellationToken) ?? throw new InvalidDataException("登録行が欠損しています。");
                if (StorageRepository.Read<Registration>(registrationRow).Disabled && !all[occurrence.RootId].Value.Started)
                {
                    continue;
                }

                if (occurrence.Id == occurrence.RootId)
                {
                    bool blocked = all.Values.Any(v => v.Value.RootPath == occurrence.RootPath && v.Value.RootId != occurrence.RootId && (v.Value.State == "Unknown" || (unit.Get("mp", "n") != "y" && v.Value.State is "Starting" or "Running")));
                    if (blocked)
                    {
                        continue;
                    }

                    occurrence.Started = true;
                    occurrence.State = "Running";
                    await CommitAsync([Update(item.Row, occurrence)], cancellationToken);
                }
                else
                {
                    var root = all[occurrence.RootId].Value;
                    if (!root.Started || root.State != "Running" || !DependenciesReady(occurrence, units, all.Values.Select(v => v.Value)))
                    {
                        continue;
                    }

                    if (unit.Type == "n")
                    {
                        occurrence.State = "Running";
                        occurrence.Started = true;
                        await CommitAsync([Update(item.Row, occurrence)], cancellationToken);
                    }
                    else if (running.Count < options.MaxConcurrency)
                    {
                        await StartAsync(item.Row, occurrence, unit, cancellationToken);
                    }
                }
            }
            else if (occurrence.State == "Running" && units[occurrence.UnitPath].Type == "n")
            {
                var children = all.Values.Where(v => v.Value.RootId == occurrence.RootId && ScheduleCalculator.Parent(v.Value.UnitPath) == occurrence.UnitPath).Select(v => v.Value).ToList();
                if (children.All(c => c.State is "Succeeded" or "Warning"))
                {
                    occurrence.State = children.Any(c => c.State == "Warning") ? "Warning" : "Succeeded";
                    await CommitAsync([Update(item.Row, occurrence)], cancellationToken);
                }
            }
        }
    }

    /// <summary>権限喪失時に全プロセスの停止を要求し、結果を自動確定しません。</summary>
    /// <returns>実行タスクが停止処理を終えたときに完了します。</returns>
    public async Task StopAsync()
    {
        foreach (var item in running.Values)
        {
            await item.Stop.CancelAsync();
        }

        foreach (var item in running.Values)
        {
            try
            {
                await item.Task;
            }
            catch (Exception)
            {
                // 永続状態のStarting/Runningを次の稼働系がUnknownへ移します。停止成功とは推測しません。
            }

            item.Stop.Dispose();
        }

        running.Clear();
    }

    /// <summary>親ネットと先行の成功を確認して後続を許可します。</summary>
    /// <param name="occurrence">開始を判定する世代です。</param>
    /// <param name="units">固定した定義です。</param>
    /// <param name="occurrences">同時に読み取った全世代です。</param>
    /// <returns>開始可能ならtrueです。結果不明や異常の先行があればfalseです。</returns>
    private static bool DependenciesReady(Occurrence occurrence, Dictionary<string, UnitDefinition> units, IEnumerable<Occurrence> occurrences)
    {
        string parentPath = ScheduleCalculator.Parent(occurrence.UnitPath);
        var siblings = occurrences.Where(o => o.RootId == occurrence.RootId).ToDictionary(o => o.UnitPath, StringComparer.Ordinal);
        if (!siblings.TryGetValue(parentPath, out var parent) || parent.State != "Running")
        {
            return false;
        }

        return DefinitionValidator.Connections(units[parentPath]).Where(e => e.To == occurrence.UnitPath).All(e => siblings[e.From].State is "Succeeded" or "Warning");
    }

    /// <summary>定義採用のジャーナルを再開し、登録作成後にReadyへ進めます。</summary>
    /// <param name="hash">採用する保存済み定義です。</param>
    /// <param name="cancellationToken">権限喪失の通知です。</param>
    /// <returns>Tableと管理Blobの整合が取れた時点で完了します。</returns>
    private async Task ActivateAsync(string hash, CancellationToken cancellationToken)
    {
        if (running.Count > 0)
        {
            throw new FormatException("実行中の定義切り替えは、プロセスが終了してから要求してください。");
        }

        var row = await storage.GetAsync("definition-active", cancellationToken);
        if (row is null && (await storage.Container.GetBlobClient($"management/{storage.Partition}/active.json").ExistsAsync(cancellationToken)).Value)
        {
            throw new InvalidDataException("有効版Table行が欠損しています。新規初期化せず復元が必要です。");
        }

        var existing = row is null ? null : StorageRepository.Read<Activation>(row);
        if (existing is not null && existing.FormatVersion != 1)
        {
            throw new InvalidDataException("有効版の保存形式を認識できません。");
        }

        bundle = await definitions.ReadAsync(hash, cancellationToken);
        string settingsHash = definitions.SettingsHash();
        bool same = existing?.DefinitionHash == hash && existing.SettingsHash == settingsHash;
        active = same ? existing : new Activation { Id = Guid.NewGuid().ToString("N"), DefinitionHash = hash, SettingsHash = settingsHash, Baseline = clock.GetUtcNow() };
        if (!same)
        {
            await CommitAsync([row is null ? Add("definition-active", active) : Update(row, active)], cancellationToken);
        }

        if (active!.Phase == "Preparing")
        {
            var all = bundle.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
            foreach (var unit in all.Values.Where(u => u.Type == "n" && (!all.TryGetValue(ScheduleCalculator.Parent(u.Path), out var parent) || parent.Type == "g")))
            {
                string id = StorageRepository.Hash(unit.Path);
                string key = $"registration:{active.Id}:{id}";
                var registrationRow = await storage.GetAsync(key, cancellationToken);
                if (registrationRow is null)
                {
                    var day = BusinessToday(active.Baseline);
                    var registration = new Registration { Id = id, Namespace = active.Id, RootPath = unit.Path, Anchor = day, Through = day.AddDays(-1) };
                    await CommitAsync([Add(key, registration)], cancellationToken);
                    registrationRow = await storage.GetAsync(key, cancellationToken);
                }

                await ReplenishAsync(registrationRow!, cancellationToken);
            }

            active.Phase = "Ready";
            row = await storage.GetAsync("definition-active", cancellationToken);
            await CommitAsync([Update(row!, active)], cancellationToken);
        }
        else if (active.Phase != "Ready")
        {
            throw new InvalidDataException("初期化状態を認識できません。");
        }

        var expectedUnits = bundle.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        foreach (var root in expectedUnits.Values.Where(u => u.Type == "n" && (!expectedUnits.TryGetValue(ScheduleCalculator.Parent(u.Path), out var parent) || parent.Type == "g")))
        {
            if (await storage.GetAsync($"registration:{active.Id}:{StorageRepository.Hash(root.Path)}", cancellationToken) is null)
            {
                throw new InvalidDataException("自動登録行が欠損しています。解除済み登録も削除せず保存する必要があります。");
            }
        }

        var mirror = storage.Container.GetBlobClient($"management/{storage.Partition}/active.json");
        var properties = await mirror.ExistsAsync(cancellationToken);
        var condition = new BlobRequestConditions { IfNoneMatch = ETag.All };
        if (properties.Value)
        {
            var current = await mirror.DownloadContentAsync(cancellationToken);
            try
            {
                var manifest = current.Value.Content.ToObjectFromJson<Activation>();
                if (manifest is null || manifest.FormatVersion != 1 || manifest.Phase != "Ready" || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    throw new InvalidDataException("管理Blobが破損しています。");
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("管理BlobのJSONが破損しています。", ex);
            }

            condition = new BlobRequestConditions { IfMatch = current.Value.Details.ETag };
        }

        leader!.Ensure();
        await mirror.UploadAsync(BinaryData.FromObjectAsJson(active), new BlobUploadOptions { Conditions = condition }, cancellationToken);
        await storage.PutImmutableAsync($"management/{storage.Partition}/{hash}/{settingsHash}/{active.Id}/activation.json", BinaryData.FromObjectAsJson(active), cancellationToken);
    }

    /// <summary>未作成の予定だけを決定的なキーで補充し、確定済み世代を再計算しません。</summary>
    /// <param name="row">補充カーソルを持つ登録行です。</param>
    /// <param name="cancellationToken">権限喪失の通知です。</param>
    /// <returns>予定作成とカーソル保存の完了を表します。</returns>
    private async Task ReplenishAsync(TableEntity row, CancellationToken cancellationToken)
    {
        var registration = StorageRepository.Read<Registration>(row);
        if (registration.Disabled || registration.Kind == "Immediate")
        {
            return;
        }

        DateOnly until = BusinessToday(clock.GetUtcNow()).AddDays(options.ConfirmedHorizonDays);
        if (registration.End is not null && until > registration.End)
        {
            until = registration.End.Value;
        }

        if (registration.Through >= until)
        {
            return;
        }

        var all = bundle!.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        foreach (var point in ScheduleCalculator.Calculate(all[registration.RootPath], all, registration, registration.Through, until, options))
        {
            await CreateOccurrenceAsync(registration, point, all[registration.RootPath], cancellationToken);
        }

        registration.Through = until;
        await CommitAsync([Update(row, registration)], cancellationToken);
    }

    /// <summary>一つのルート世代と子世代を再開可能に作成します。</summary>
    /// <param name="registration">生成元の実行登録です。</param>
    /// <param name="point">確定した予定です。</param>
    /// <param name="root">固定定義のルートです。</param>
    /// <param name="cancellationToken">停止通知です。</param>
    /// <returns>同じキーの世代がそろった時点で完了します。</returns>
    private async Task CreateOccurrenceAsync(Registration registration, SchedulePoint point, UnitDefinition root, CancellationToken cancellationToken)
    {
        string rootId = StorageRepository.Hash(JsonSerializer.Serialize(new { registration.Namespace, registration.Id, point.Rule, point.BusinessDate, point.Due }));
        foreach (var unit in new[] { root }.Flatten())
        {
            string id = unit.Path == root.Path ? rootId : StorageRepository.Hash(rootId + ":" + unit.Path);
            string key = "occurrence:" + id;
            if (await storage.GetAsync(key, cancellationToken) is not null)
            {
                continue;
            }

            var occurrence = new Occurrence
            {
                Id = id,
                RootId = rootId,
                Namespace = registration.Namespace,
                RegistrationId = registration.Id,
                UnitPath = unit.Path,
                RootPath = root.Path,
                BusinessDate = point.BusinessDate,
                Rule = point.Rule,
                Due = point.Due,
                OriginalDue = point.Due,
                State = unit.Get("ha", "n") == "y" ? "Held" : "Waiting",
            };
            if (unit.Path == root.Path && point.Due < active!.Baseline)
            {
                occurrence.State = "Skipped";
                occurrence.Reason = "初期化基準時刻より前の予定です。";
            }

            await CommitAsync([Add(key, occurrence)], cancellationToken);
        }
    }

    /// <summary>Startingと試行を一括保存してからOSプロセスを起動します。</summary>
    /// <param name="row">起動権を取る世代の現在行です。</param>
    /// <param name="occurrence">実行する世代です。</param>
    /// <param name="unit">固定定義のジョブです。</param>
    /// <param name="cancellationToken">権限喪失の通知です。</param>
    /// <returns>開始権の保存と実行タスクの作成を表します。</returns>
    private async Task StartAsync(TableEntity row, Occurrence occurrence, UnitDefinition unit, CancellationToken cancellationToken)
    {
        var attempt = new JobAttempt { Id = Guid.NewGuid().ToString("N"), OccurrenceId = occurrence.Id, Owner = leader!.Owner, Epoch = leader.Epoch, StartedAt = clock.GetUtcNow() };
        occurrence.AttemptId = attempt.Id;
        occurrence.State = "Starting";
        occurrence.Started = true;
        await CommitAsync([Update(row, occurrence), Add("attempt:" + attempt.Id, attempt)], cancellationToken);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var task = executor.ExecuteAsync(
            unit,
            attempt,
            leader.Ensure,
            async pid =>
        {
            var current = await storage.GetAsync(row.RowKey, cancellationToken);
            var attemptRow = await storage.GetAsync("attempt:" + attempt.Id, cancellationToken);
            occurrence.State = "Running";
            attempt.State = "Running";
            attempt.ProcessId = pid;
            await CommitAsync([Update(current!, occurrence), Update(attemptRow!, attempt)], cancellationToken);
        },
            stop.Token);
        running.Add(occurrence.Id, (task, stop));
    }

    /// <summary>完了したタスクを回収し、結果と世代を同時に確定します。</summary>
    /// <param name="cancellationToken">権限喪失の通知です。</param>
    /// <returns>回収できる結果の永続化を表します。</returns>
    private async Task CollectAsync(CancellationToken cancellationToken)
    {
        foreach (var (id, item) in running.ToArray())
        {
            if (!item.Task.IsCompleted)
            {
                continue;
            }

            var row = await storage.GetAsync("occurrence:" + id, cancellationToken);
            var occurrence = StorageRepository.Read<Occurrence>(row!);
            var attemptRow = await storage.GetAsync("attempt:" + occurrence.AttemptId, cancellationToken);
            JobAttempt attempt;
            try
            {
                attempt = await item.Task;
            }
            catch (Exception)
            {
                attempt = StorageRepository.Read<JobAttempt>(attemptRow!);
                attempt.State = "Unknown";
                occurrence.Reason = "プロセスまたは出力収集が中断されました。業務結果の明示確認が必要です。";
            }

            attempt.FinishedAt = attempt.State == "Unknown" ? null : clock.GetUtcNow();
            occurrence.State = attempt.State;
            await CommitAsync([Update(row!, occurrence), Update(attemptRow!, attempt)], cancellationToken);
            item.Stop.Dispose();
            running.Remove(id);
        }
    }

    /// <summary>保留等の要求を適用し、操作完了と対象状態を同一トランザクションに含めます。</summary>
    /// <param name="commandRow">未処理要求のETag付き行です。</param>
    /// <param name="operation">適用する要求です。</param>
    /// <param name="cancellationToken">権限喪失の通知です。</param>
    /// <returns>操作が完了または明示的に拒否されたことを表します。</returns>
    private async Task ApplyAsync(TableEntity commandRow, Operation operation, CancellationToken cancellationToken)
    {
        var changes = new List<TableTransactionAction>();
        try
        {
            if (operation.StateNamespace != active!.Id)
            {
                throw new FormatException("受付後に定義の採用IDが変わりました。新しい操作IDで対象を再確認してください。");
            }

            if (operation.Kind == "Activate")
            {
                await ActivateAsync(operation.Target, cancellationToken);
            }
            else if (operation.Kind == "Register")
            {
                var all = bundle!.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
                if (!all.TryGetValue(operation.Target, out var root) || root.Type != "n" || (all.TryGetValue(ScheduleCalculator.Parent(root.Path), out var parent) && parent.Type != "g"))
                {
                    throw new FormatException("登録対象はルートネットにしてください。");
                }

                if (operation.RegistrationKind is not ("Confirmed" or "Immediate"))
                {
                    throw new FormatException("計画登録の変更反映は未実装です。ConfirmedまたはImmediateを指定してください。");
                }

                if (operation.RegistrationKind == "Immediate" && operation.Due is null)
                {
                    throw new FormatException("即時登録には固定されたDueが必要です。");
                }

                string id = StorageRepository.Hash(operation.Id);
                string key = $"registration:{active!.Id}:{id}";
                var current = await storage.GetAsync(key, cancellationToken);
                var day = BusinessToday(clock.GetUtcNow());
                var registration = current is null ? new Registration { Id = id, Namespace = active.Id, RootPath = root.Path, Kind = operation.RegistrationKind, Anchor = day, Through = day.AddDays(-1), End = operation.End } : StorageRepository.Read<Registration>(current);
                if (current is null)
                {
                    await CommitAsync([Add(key, registration)], cancellationToken);
                }

                if (registration.Kind == "Immediate")
                {
                    if (operation.Due is null)
                    {
                        throw new FormatException("再送でも予定を固定するため即時登録にはDueが必要です。");
                    }

                    await CreateOccurrenceAsync(registration, new SchedulePoint(1, registration.Anchor, operation.Due.Value), root, cancellationToken);
                }

                operation.Result = id;
            }
            else if (operation.Kind == "Unregister")
            {
                var row = await storage.GetAsync($"registration:{active!.Id}:{operation.Target}", cancellationToken) ?? throw new FormatException("登録がありません。");
                var registration = StorageRepository.Read<Registration>(row);
                registration.Disabled = true;
                changes.Add(Update(row, registration));

                // 解除済み登録の予定は開始条件でも検査します。過去の履歴は削除しません。
            }
            else
            {
                var row = await storage.GetAsync("occurrence:" + operation.Target, cancellationToken) ?? throw new FormatException("世代がありません。");
                var occurrence = StorageRepository.Read<Occurrence>(row);
                if (operation.ExpectedState is not null && occurrence.State != operation.ExpectedState)
                {
                    throw new FormatException("期待状態が一致しません。");
                }

                switch (operation.Kind)
                {
                    case "Hold" when occurrence.State == "Waiting":
                        occurrence.State = "Held";
                        break;
                    case "Release" when occurrence.State == "Held":
                        occurrence.State = "Waiting";
                        break;
                    case "Move" when occurrence.State is "Waiting" or "Held" && operation.Due is not null:
                        occurrence.Due = operation.Due.Value;
                        break;
                    case "Restore" when occurrence.State is "Waiting" or "Held":
                        occurrence.Due = occurrence.OriginalDue;
                        break;
                    case "Cancel" when occurrence.State is "Waiting" or "Held":
                        occurrence.State = "Cancelled";
                        break;
                    case "Stop" when running.TryGetValue(occurrence.Id, out var task):
                        await task.Stop.CancelAsync();
                        break;
                    case "Rerun" when occurrence.State is "Failed" or "TimedOut":
                        occurrence.State = "Waiting";
                        occurrence.AttemptId = null;
                        break;
                    case "Resolve" when occurrence.State == "Unknown":
                        if (operation.ExpectedState != "Unknown" || string.IsNullOrWhiteSpace(operation.Reason) || operation.AttemptId != occurrence.AttemptId || operation.Resolution is not ("Succeeded" or "Failed"))
                        {
                            throw new FormatException("解決には期待状態、対象試行ID、理由、SucceededまたはFailedが必要です。");
                        }

                        occurrence.State = operation.Resolution;
                        occurrence.Reason = operation.Reason;
                        break;
                    default:
                        throw new FormatException("この操作は現在の状態に適用できません。");
                }

                changes.Add(Update(row, occurrence));
            }

            operation.Status = "Completed";
        }
        catch (FormatException ex)
        {
            changes.Clear();
            operation.Status = "Rejected";
            operation.Result = ex.Message;
        }

        changes.Add(Update(commandRow, operation));
        await CommitAsync(changes, cancellationToken);
    }

    /// <summary>リース確認とTable更新を一本の直列経路へ集約します。</summary>
    /// <param name="changes">条件付きの変更一覧です。</param>
    /// <param name="cancellationToken">権限喪失の通知です。</param>
    /// <returns>永続化後にだけ完了します。</returns>
    private async Task CommitAsync(IEnumerable<TableTransactionAction> changes, CancellationToken cancellationToken)
    {
        await mutations.WaitAsync(cancellationToken);
        try
        {
            leader!.Ensure();
            await storage.FencedAsync(leader.Owner, leader.Epoch, changes, cancellationToken);
        }
        finally
        {
            mutations.Release();
        }
    }

    /// <summary>新しい行を追加する操作を作ります。既存行は上書きしません。</summary>
    /// <typeparam name="T">保存するモデルです。</typeparam>
    /// <param name="key">行キーです。</param>
    /// <param name="value">追加内容です。</param>
    /// <returns>Tableの追加操作です。</returns>
    private TableTransactionAction Add<T>(string key, T value) => new(TableTransactionActionType.Add, storage.Entity(key, value));

    /// <summary>読み取ったETagに一致する場合だけ置換する操作を作ります。</summary>
    /// <typeparam name="T">保存するモデルです。</typeparam>
    /// <param name="row">現在のETag付き行です。</param>
    /// <param name="value">新しい内容です。</param>
    /// <returns>ワイルドカードを使用しない更新操作です。</returns>
    private TableTransactionAction Update<T>(TableEntity row, T value) => new(TableTransactionActionType.UpdateReplace, storage.Entity(row.RowKey, value), row.ETag);

    /// <summary>明示されたタイムゾーンの当日を登録の基準日に使います。</summary>
    /// <param name="utc">取得済みのUTC時刻です。</param>
    /// <returns>タイムゾーン変換後の日付です。</returns>
    private DateOnly BusinessToday(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone)).DateTime);
}
