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
public sealed partial class SchedulerEngine(StorageRepository storage, DefinitionService definitions, ProcessJobExecutor executor, RunnerOptions options, TimeProvider clock)
{
    /// <summary>実行結果とAPI操作を同じTable更新経路へ直列化します。</summary>
    private readonly SemaphoreSlim mutations = new(1, 1);

    /// <summary>このプロセスが起動したジョブだけを追跡します。再起動時は復元しません。</summary>
    private readonly Dictionary<string, (Task<JobAttempt> Task, CancellationTokenSource Stop)> running = new(StringComparer.Ordinal);

    /// <summary>世代が固定した不変定義を版ごとに保持します。</summary>
    private readonly Dictionary<string, Dictionary<string, UnitDefinition>> versionUnits = new(StringComparer.Ordinal);

    /// <summary>Tableで確定した採用情報のキャッシュです。</summary>
    private Activation? active;

    /// <summary>現在の採用に対応する不変定義のキャッシュです。</summary>
    private DefinitionBundle? bundle;

    /// <summary>すべての状態変更とOS起動の直前に確認する権限です。</summary>
    private LeaderCoordinator? leader;

    /// <summary>保持期限の全件走査を毎回のポーリングで行わないための次回確認時刻です。</summary>
    private DateTimeOffset nextRetentionCheck;

    /// <summary>復旧時点より前に未開始のルートは補充後も起動しません。</summary>
    private DateTimeOffset recoveryBaseline;

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
        recoveryBaseline = clock.GetUtcNow();
        await ActivateAsync(loaded.Hash, true, cancellationToken);
        await ResumeMutationsAsync(cancellationToken);
        await RepairIncompleteGenerationsAsync(cancellationToken);
        DateTimeOffset recoveryTime = recoveryBaseline;

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
            else if (occurrence.Namespace == active!.Id && occurrence.Id == occurrence.RootId && !occurrence.Started && occurrence.State == "Waiting" && occurrence.Due < recoveryTime
                && StorageRepository.Read<Registration>((await storage.GetAsync($"registration:{occurrence.Namespace}:{occurrence.RegistrationId}", cancellationToken))!).PassedDaemonStarts != "IMMEDIATE")
            {
                occurrence.State = "Skipped";
                occurrence.FinishedAt = recoveryTime;
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
        await ResumeMutationsAsync(cancellationToken);
        await CollectAsync(cancellationToken);
        if (clock.GetUtcNow() >= nextRetentionCheck && (nextRetentionCheck == default || options.HistoryArchiveAfterDays is not null || options.LogRetentionDays is not null || options.OperationArchiveAfterDays is not null))
        {
            await new HistoryRetentionService(storage, options, clock).SweepAsync(CommitAsync, leader.Ensure, cancellationToken);
            nextRetentionCheck = clock.GetUtcNow().AddMinutes(1);
        }

        foreach (var commandRow in await storage.ListAsync("command:", cancellationToken))
        {
            var operation = StorageRepository.Read<Operation>(commandRow);
            if (operation.Status == "Pending")
            {
                await ApplyAsync(commandRow, operation, cancellationToken);
            }
        }

        var registrationRows = await storage.ListAsync($"registration:{active!.Id}:", cancellationToken);
        foreach (var registrationRow in registrationRows)
        {
            await ReplenishAsync(registrationRow, cancellationToken);
        }

        // このTick中は操作適用が終わっているため、解除状態を一度の列挙から参照できます。
        // ジョブごとに同じ登録を読み直すStorage要求をなくします。
        var registrations = registrationRows.Select(StorageRepository.Read<Registration>).ToDictionary(r => r.Id, StringComparer.Ordinal);
        var rows = await storage.ListAsync("occurrence:", cancellationToken);
        var archivedRoots = rows.Select(StorageRepository.Read<Occurrence>).Where(o => o.State is "Archiving" or "Archived").Select(o => o.RootId).ToHashSet(StringComparer.Ordinal);
        rows = rows.Where(r => !archivedRoots.Contains(StorageRepository.Read<Occurrence>(r).RootId)).ToList();
        var all = rows.ToDictionary(r => StorageRepository.Read<Occurrence>(r).Id, r => (Row: r, Value: StorageRepository.Read<Occurrence>(r)), StringComparer.Ordinal);
        var rootUnits = new Dictionary<string, Dictionary<string, UnitDefinition>>(StringComparer.Ordinal);
        foreach (var root in all.Values.Where(v => v.Value.Namespace == active!.Id && v.Value.Id == v.Value.RootId))
        {
            rootUnits[root.Value.Id] = await ReadUnitsAsync(root.Value.DefinitionHash, cancellationToken);
        }

        var memberPaths = all.Values.GroupBy(v => v.Value.RootId).ToDictionary(g => g.Key, g => g.Select(v => v.Value.UnitPath).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var network in all.Values.Where(v => v.Value.Namespace == active!.Id && rootUnits[v.Value.RootId][v.Value.UnitPath].Type == "n"))
        {
            foreach (var child in rootUnits[network.Value.RootId][network.Value.UnitPath].Children)
            {
                if (!memberPaths[network.Value.RootId].Contains(child.Path))
                {
                    throw new InvalidDataException("世代の子ユニットが欠損しています。成功扱いや部分実行はできません。");
                }
            }
        }

        foreach (var item in all.Values.Where(v => v.Value.Namespace == active!.Id).OrderBy(v => v.Value.Due).ThenBy(v => v.Value.UnitPath, StringComparer.Ordinal))
        {
            var occurrence = item.Value;
            var units = rootUnits[occurrence.RootId];
            if (occurrence.State is "Waiting" or "Held" && occurrence.Id != occurrence.RootId && occurrence.StandaloneRerun != true && ShouldExclude(occurrence, units, all.Values.Select(v => v.Value)))
            {
                occurrence.State = "NotScheduled";
                occurrence.FinishedAt = clock.GetUtcNow();
                occurrence.Reason = "条件不成立またはリカバリー対象外です。";
                await CommitAsync([Update(item.Row, occurrence)], cancellationToken);
                continue;
            }

            if (occurrence.State == "Waiting" && occurrence.Due <= clock.GetUtcNow())
            {
                var unit = units[occurrence.UnitPath];
                if (unit.Type != "n" && running.Count >= options.MaxConcurrency)
                {
                    continue;
                }

                if (!registrations.TryGetValue(occurrence.RegistrationId, out var registration))
                {
                    throw new InvalidDataException("登録行が欠損しています。");
                }

                if (registration.Disabled && !all[occurrence.RootId].Value.Started)
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
                    bool standalone = occurrence.StandaloneRerun == true || all.Values.Any(v => v.Value.RootId == occurrence.RootId && v.Value.StandaloneRerun == true && v.Value.State == "Running" && occurrence.UnitPath.StartsWith(v.Value.UnitPath + "/", StringComparison.Ordinal));
                    if (!root.Started || (!standalone && root.State != "Running") || (occurrence.StandaloneRerun != true && !DependenciesReady(occurrence, units, all.Values.Select(v => v.Value))))
                    {
                        continue;
                    }

                    if (unit.Type == "n")
                    {
                        occurrence.State = "Running";
                        occurrence.Started = true;
                        await CommitAsync([Update(item.Row, occurrence)], cancellationToken);
                    }
                    else if (unit.Type == "jdj")
                    {
                        var edge = DefinitionValidator.Edges(units[ScheduleCalculator.Parent(unit.Path)]).Single(e => e.To == unit.Path);
                        var predecessor = all.Values.Single(v => v.Value.RootId == occurrence.RootId && v.Value.UnitPath == edge.From).Value;
                        var attemptRow = await storage.GetAsync("attempt:" + predecessor.AttemptId, cancellationToken) ?? throw new InvalidDataException("判定元の試行が欠損しています。");
                        int code = StorageRepository.Read<JobAttempt>(attemptRow).ExitCode ?? throw new InvalidDataException("判定元の終了コードがありません。");
                        occurrence.ConditionMatched = EvaluateCondition(unit, unchecked((uint)code));
                        occurrence.State = "Succeeded";
                        occurrence.Started = true;
                        occurrence.FinishedAt = clock.GetUtcNow();
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
                if (children.All(c => c.State is "Succeeded" or "Warning" or "NotScheduled"))
                {
                    occurrence.State = children.Any(c => c.State == "Warning") ? "Warning" : "Succeeded";
                    occurrence.FinishedAt = clock.GetUtcNow();
                    await CommitAsync([Update(item.Row, occurrence)], cancellationToken);
                }
                else if (children.Any(c => c.State is "Failed" or "TimedOut" or "Cancelled" or "Interrupted")
                    && !children.Any(c => c.State is "Running" or "Starting" or "Unknown" or "Held")
                    && !children.Any(c => c.State == "Waiting" && DependenciesReady(c, units, all.Values.Select(v => v.Value))))
                {
                    // 独立した後続が開始可能なら先に実行します。異常先行で待つ後続だけになった時点で結果を確定します。
                    occurrence.State = "Failed";
                    occurrence.FinishedAt = clock.GetUtcNow();
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

    /// <summary>公式の実行ID書式を保った独自採番を行い、上限到達時は再利用せず停止します。</summary>
    /// <param name="sequence">SchedulerId全体で単調増加する番号です。</param>
    /// <returns>任意の数字接頭辞、英大文字、4桁数字からなる不透明なIDです。</returns>
    private static string FormatExecutionId(long sequence)
    {
        long prefix = sequence / 260000;
        if (prefix > 9999)
        {
            throw new InvalidOperationException("互換APIの実行ID領域を使い切りました。IDは再利用できません。");
        }

        char letter = (char)('A' + ((sequence / 10000) % 26));
        string start = prefix == 0 ? string.Empty : prefix.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
        return $"@{start}{letter}{sequence % 10000:D4}";
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

        return DefinitionValidator.Edges(units[parentPath]).Where(e => e.To == occurrence.UnitPath).All(e =>
        {
            var predecessor = siblings[e.From];
            if (units[occurrence.UnitPath].Type is "rj" or "rp")
            {
                return predecessor.State is "Failed" or "TimedOut";
            }

            if (e.Conditional)
            {
                return predecessor.State == "Succeeded" && predecessor.ConditionMatched == true;
            }

            if (predecessor.State is not ("Succeeded" or "Warning" or "NotScheduled"))
            {
                return predecessor.State is "Failed" or "TimedOut" or "Interrupted" && occurrence.BypassedPredecessors?.Contains(predecessor.Id) == true;
            }

            // 判定成立時は従属ユニットを完了させてから、判定ジョブの通常後続へ進めます。
            return predecessor.ConditionMatched != true || DefinitionValidator.Edges(units[parentPath]).Where(c => c.From == e.From && c.Conditional).All(c => siblings[c.To].State is "Succeeded" or "Warning" or "NotScheduled");
        });
    }

    /// <summary>定義採用のジャーナルを再開し、登録作成後にReadyへ進めます。</summary>
    /// <param name="hash">採用する保存済み定義です。</param>
    /// <param name="recovering">復旧時は旧領域を引き継ぐためtrueです。</param>
    /// <param name="cancellationToken">権限喪失の通知です。</param>
    /// <returns>Tableと管理Blobの整合が取れた時点で完了します。</returns>
    private async Task ActivateAsync(string hash, bool recovering, CancellationToken cancellationToken)
    {
        if (running.Count > 0 || (!recovering && (await storage.ListAsync("occurrence:", cancellationToken)).Select(StorageRepository.Read<Occurrence>).Any(o => o.Namespace == active?.Id && (o.State is "Starting" or "Running" || (o.Started && o.State is "Waiting" or "Held")))))
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
                    var registration = new Registration { Id = id, Namespace = active.Id, RootPath = unit.Path, Anchor = day, Through = day.AddDays(-1), FutureGenerations = options.ConfirmedFutureGenerations, CreatedAt = active.Baseline };
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

    /// <summary>補充範囲に依存せず、途中まで保存された現行領域の世代を先に完成させます。</summary>
    /// <param name="cancellationToken">権限喪失の通知です。</param>
    /// <returns>既存の決定的な世代IDを維持した補完の完了です。</returns>
    private async Task RepairIncompleteGenerationsAsync(CancellationToken cancellationToken)
    {
        foreach (var row in await storage.ListAsync("occurrence:", cancellationToken))
        {
            var occurrence = StorageRepository.Read<Occurrence>(row);
            if (occurrence.Namespace != active!.Id || occurrence.Id != occurrence.RootId || occurrence.Materialized != false)
            {
                continue;
            }

            var registrationRow = await storage.GetAsync($"registration:{active.Id}:{occurrence.RegistrationId}", cancellationToken)
                ?? throw new InvalidDataException("未完成世代の登録が欠損しています。");
            var registration = StorageRepository.Read<Registration>(registrationRow);
            var units = await ReadUnitsAsync(occurrence.DefinitionHash, cancellationToken);
            if (registration.RootPath != occurrence.UnitPath || !units.TryGetValue(occurrence.UnitPath, out var root) || root.Type != "n")
            {
                throw new InvalidDataException("未完成世代の登録と定義が一致しません。");
            }

            // 無効化された登録も含め、既に保存された子を維持し欠けた子だけを補います。
            await CreateOccurrenceAsync(registration, new SchedulePoint(occurrence.Rule, occurrence.BusinessDate, occurrence.OriginalDue), root, cancellationToken);
        }
    }

    /// <summary>未作成の予定だけを決定的なキーで補充し、確定済み世代を再計算しません。</summary>
    /// <param name="row">補充カーソルを持つ登録行です。</param>
    /// <param name="cancellationToken">権限喪失の通知です。</param>
    /// <returns>予定作成とカーソル保存の完了を表します。</returns>
    private async Task ReplenishAsync(TableEntity row, CancellationToken cancellationToken)
    {
        var registration = StorageRepository.Read<Registration>(row);
        if (!registration.Disabled && registration.IntervalMinutes is not null)
        {
            await ReplenishIntervalAsync(row, registration, cancellationToken);
            return;
        }

        if (registration.Disabled || registration.Kind == "Immediate" || registration.FixedDue is not null)
        {
            return;
        }

        if (registration.Start is not null)
        {
            await ReplenishPeriodAsync(row, registration, cancellationToken);
            return;
        }

        if (registration.FutureGenerations is not null)
        {
            await ReplenishCountAsync(row, registration, cancellationToken);
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

        var all = await ReadUnitsAsync(registration.DefinitionHash, cancellationToken);
        var from = registration.Through;
        var recoveryDay = BusinessToday(recoveryBaseline).AddDays(-3);
        if (from < recoveryDay)
        {
            from = recoveryDay;
        }

        foreach (var point in ScheduleCalculator.Calculate(all[registration.RootPath], all, registration, from, until, options))
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
        var existingRoot = await storage.GetAsync("occurrence:" + rootId, cancellationToken);
        if (existingRoot is not null && StorageRepository.Read<Occurrence>(existingRoot).State is "Archiving" or "Archived")
        {
            return;
        }

        string fixedHash = existingRoot is null ? registration.DefinitionHash ?? active!.DefinitionHash : StorageRepository.Read<Occurrence>(existingRoot).DefinitionHash ?? active!.DefinitionHash;
        root = (await ReadUnitsAsync(fixedHash, cancellationToken))[root.Path];
        var schedule = new Dictionary<string, (SchedulePoint Point, bool Enabled)>(StringComparer.Ordinal) { [root.Path] = (point, true) };
        var fixedUnits = await ReadUnitsAsync(fixedHash, cancellationToken);
        foreach (var unit in new[] { root }.Flatten())
        {
            var selected = unit.Path == root.Path ? (Point: point, Enabled: true) : schedule[ScheduleCalculator.Parent(unit.Path)];
            if (unit.Path != root.Path && unit.Type == "n" && unit.Get("de", "y") != "y" && selected.Enabled && unit.Attributes.Keys.Any(k => k is "sd" or "st" or "ln" or "cy" or "sh" or "shd" or "cftd" or "ed"))
            {
                // 上位ルールにリンクする候補から、当該業務日の最も早い予定を一つだけ採用します。
                var linked = JsonSerializer.Deserialize<UnitDefinition>(JsonSerializer.Serialize(unit))!;
                linked.Attributes["sd"] = (unit.Attributes.GetValueOrDefault("sd") ?? ["1,en"])
                    .Where(raw => ScheduleCalculator.RuleValue(unit, "ln", ScheduleCalculator.SplitRule(raw).Rule, ScheduleCalculator.SplitRule(raw).Rule.ToString(System.Globalization.CultureInfo.InvariantCulture)) == selected.Point.Rule.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
                var candidate = ScheduleCalculator.Calculate(linked, fixedUnits, registration, selected.Point.BusinessDate, selected.Point.BusinessDate.AddDays(1), options).OrderBy(p => p.Due).ThenBy(p => p.Rule).FirstOrDefault();
                selected = candidate is null ? (selected.Point, false) : (candidate, true);
            }

            schedule[unit.Path] = selected;
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
                DefinitionHash = fixedHash,
                RegistrationId = registration.Id,
                UnitPath = unit.Path,
                RootPath = root.Path,
                BusinessDate = selected.Point.BusinessDate,
                Rule = selected.Point.Rule,
                Due = selected.Point.Due,
                OriginalDue = selected.Point.Due,
                ScheduleExcluded = selected.Enabled ? null : true,
                Materialized = unit.Path == root.Path ? false : null,
                State = !selected.Enabled ? "NotScheduled" : (unit.Path == root.Path && registration.Holding is bool holding ? holding : unit.Get("ha", "n") == "y") ? "Held" : "Waiting",
            };
            if (unit.Path == root.Path && registration.PassedDaemonStarts != "IMMEDIATE" && point.Due < (recoveryBaseline > active!.Baseline ? recoveryBaseline : active.Baseline))
            {
                occurrence.State = "Skipped";
                occurrence.FinishedAt = recoveryBaseline > active.Baseline ? recoveryBaseline : active.Baseline;
                occurrence.Reason = "初期化基準時刻より前の予定です。";
            }

            if (id == rootId)
            {
                var sequenceRow = await storage.GetAsync("execution-sequence", cancellationToken);
                long sequence = (sequenceRow is null ? 0 : StorageRepository.Read<long>(sequenceRow)) + 1;
                occurrence.Jp1ExecutionId = FormatExecutionId(sequence);
                await CommitAsync([Add(key, occurrence), sequenceRow is null ? Add("execution-sequence", sequence) : Update(sequenceRow, sequence)], cancellationToken);
            }
            else
            {
                await CommitAsync([Add(key, occurrence)], cancellationToken);
            }
        }

        var completedRow = (await storage.GetAsync("occurrence:" + rootId, cancellationToken))!;
        var completed = StorageRepository.Read<Occurrence>(completedRow);
        if (completed.Materialized == false)
        {
            completed.Materialized = true;
            await CommitAsync([Update(completedRow, completed)], cancellationToken);
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
            occurrence.FinishedAt = attempt.FinishedAt;
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
                await ActivateAsync(operation.Target, false, cancellationToken);
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
                    throw new FormatException("計画登録は対象外です。ConfirmedまたはImmediateを指定してください。");
                }

                if (operation.RegistrationKind == "Immediate" && operation.Due is null)
                {
                    throw new FormatException("即時登録には固定されたDueが必要です。");
                }

                if (operation.FutureGenerations is < 1 or > 99 || (operation.FutureGenerations is not null && operation.RegistrationKind != "Confirmed"))
                {
                    throw new FormatException("未来世代数は確定登録で1〜99を指定してください。");
                }

                if ((operation.Start is not null && (operation.End is null || operation.Start >= operation.End))
                    || (operation.FixedDue is not null && (operation.Start is not null || operation.End is not null || operation.FutureGenerations is not null))
                    || (operation.RegistrationKind != "Confirmed" && (operation.Start is not null || operation.FixedDue is not null))
                    || operation.PassedDaemonStarts is not (null or "NEXTTIME" or "IMMEDIATE"))
                {
                    throw new FormatException("確定登録の期間・日時または超過時動作の組み合わせが不正です。");
                }

                if (operation.IntervalMinutes.HasValue != operation.IntervalUntil.HasValue || (operation.IntervalMinutes is int interval && (interval is < 1 or > 1440 || operation.FixedDue is null || operation.IntervalUntil <= operation.FixedDue || (operation.IntervalUntil - operation.FixedDue)!.Value.TotalMinutes / interval > 100000)))
                {
                    throw new FormatException("間隔は1〜1440分、FixedDueとそれより後のIntervalUntilが必要です。最大10万予定です。");
                }

                // 旧形式と自動登録は64桁のハッシュです。接頭辞で新規API登録のキー領域を分離します。
                string id = "request-" + StorageRepository.Hash(operation.Id);
                string key = $"registration:{active!.Id}:{id}";
                var current = await storage.GetAsync(key, cancellationToken);
                string legacyId = StorageRepository.Hash(operation.Id);
                var legacyRow = await storage.GetAsync($"registration:{active.Id}:{legacyId}", cancellationToken);
                if (current is null && legacyRow is not null && !bundle.Units.Flatten().Any(u => StorageRepository.Hash(u.Path) == legacyId))
                {
                    current = legacyRow;
                    id = legacyId;
                    key = current.RowKey;
                }

                var day = BusinessToday(clock.GetUtcNow());
                var registration = current is null ? new Registration { Id = id, Namespace = active.Id, RootPath = root.Path, Kind = operation.RegistrationKind, Anchor = day, Through = operation.Start ?? day.AddDays(-1), Start = operation.Start, FixedDue = operation.FixedDue, IntervalMinutes = operation.IntervalMinutes, IntervalUntil = operation.IntervalUntil, PassedDaemonStarts = operation.PassedDaemonStarts, End = operation.End, Holding = operation.Holding, FutureGenerations = operation.FutureGenerations, CreatedAt = operation.CreatedAt ?? clock.GetUtcNow() } : StorageRepository.Read<Registration>(current);
                if (current is null)
                {
                    await CommitAsync([Add(key, registration)], cancellationToken);
                }

                if (registration.FixedDue is DateTimeOffset fixedDue && registration.IntervalMinutes is null)
                {
                    await CreateOccurrenceAsync(registration, new SchedulePoint(1, BusinessToday(fixedDue), fixedDue), root, cancellationToken);
                }
                else if (registration.Kind == "Immediate")
                {
                    if (operation.Due is null)
                    {
                        throw new FormatException("再送でも予定を固定するため即時登録にはDueが必要です。");
                    }

                    await CreateOccurrenceAsync(registration, new SchedulePoint(1, registration.Anchor, operation.Due.Value), root, cancellationToken);
                }
                else
                {
                    await ReplenishAsync((await storage.GetAsync(key, cancellationToken))!, cancellationToken);
                }

                operation.Result = id;
            }
            else if (operation.Kind == "UpdateRegistrationDefinition")
            {
                var row = await storage.GetAsync($"registration:{active!.Id}:{operation.Target}", cancellationToken) ?? throw new FormatException("登録がありません。");
                var registration = StorageRepository.Read<Registration>(row);
                if (string.IsNullOrWhiteSpace(operation.DefinitionHash) || operation.ExpectedDefinitionHash != (registration.DefinitionHash ?? active.DefinitionHash))
                {
                    throw new FormatException("更新元の期待定義版が一致しません。");
                }

                Dictionary<string, UnitDefinition> updatedUnits;
                try
                {
                    var snapshot = await definitions.ReadAsync(operation.DefinitionHash, cancellationToken);
                    DefinitionValidator.Validate(snapshot.Units, options);
                    updatedUnits = await ReadUnitsAsync(operation.DefinitionHash, cancellationToken);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    throw new FormatException("指定した補充用定義が保存されていません。", ex);
                }

                if (!updatedUnits.TryGetValue(registration.RootPath, out var updatedRoot) || updatedRoot.Type != "n"
                    || (updatedUnits.TryGetValue(ScheduleCalculator.Parent(updatedRoot.Path), out var updatedParent) && updatedParent.Type != "g"))
                {
                    throw new FormatException("補充用定義に登録対象のルートがありません。");
                }

                registration.DefinitionHash = operation.DefinitionHash;
                registration.ExhaustedOn = null;
                changes.Add(Update(row, registration));
            }
            else if (operation.Kind == "UnregisterRoot")
            {
                var rows = await storage.ListAsync($"registration:{active.Id}:", cancellationToken);
                var plan = new OperationMutation { OperationId = operation.Id };
                foreach (var row in rows)
                {
                    var registration = StorageRepository.Read<Registration>(row);
                    if (registration.RootPath == operation.Target)
                    {
                        registration.Disabled = true;
                        plan.Changes.Add(new ArchivedRow(row.RowKey, row.ETag.ToString(), JsonSerializer.Serialize(registration)));
                    }
                }

                if (plan.Changes.Count == 0)
                {
                    throw new FormatException("解除対象の登録がありません。");
                }

                await new OperationMutationService(storage, clock).ApplyAsync(commandRow, operation, plan, CommitAsync, leader!.Ensure, StopOwnedAsync, cancellationToken);
                return;
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
                if (occurrence.Namespace != active.Id && operation.Kind != "Resolve")
                {
                    throw new FormatException("旧採用領域の世代は結果不明の解決だけを受け付けます。");
                }

                if (operation.ExpectedState is not null && occurrence.State != operation.ExpectedState)
                {
                    throw new FormatException("期待状態が一致しません。");
                }

                if (await ApplySelectiveRerunAsync(commandRow, operation, occurrence, cancellationToken) || await ApplyTreeOperationAsync(commandRow, operation, row, occurrence, cancellationToken))
                {
                    return;
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
                        occurrence.FinishedAt = clock.GetUtcNow();
                        break;
                    case "Stop" when running.TryGetValue(occurrence.Id, out var task):
                        await task.Stop.CancelAsync();
                        break;
                    case "Rerun" when occurrence.State is "Failed" or "TimedOut" && operation.RerunMethod is null:
                        if ((await ReadUnitsAsync(occurrence.DefinitionHash, cancellationToken))[occurrence.UnitPath].Type == "n")
                        {
                            throw new FormatException("再実行する末端ジョブを指定してください。ネット一括再実行の方式は未対応です。");
                        }

                        occurrence.State = "Waiting";
                        occurrence.AttemptId = null;
                        occurrence.BypassedPredecessors = null;
                        occurrence.StandaloneRerun = null;
                        occurrence.ConditionMatched = null;
                        occurrence.FinishedAt = null;
                        foreach (var ancestorRow in await storage.ListAsync("occurrence:", cancellationToken))
                        {
                            var ancestor = StorageRepository.Read<Occurrence>(ancestorRow);
                            if (ancestor.RootId == occurrence.RootId && ancestor.State is "Failed" or "Interrupted" && occurrence.UnitPath.StartsWith(ancestor.UnitPath + "/", StringComparison.Ordinal))
                            {
                                ancestor.State = "Running";
                                ancestor.FinishedAt = null;
                                changes.Add(Update(ancestorRow, ancestor));
                            }
                        }

                        break;
                    case "Resolve" when occurrence.State == "Unknown":
                        if (operation.ExpectedState != "Unknown" || string.IsNullOrWhiteSpace(operation.Reason) || operation.AttemptId != occurrence.AttemptId || operation.Resolution is not ("Succeeded" or "Failed"))
                        {
                            throw new FormatException("解決には期待状態、対象試行ID、理由、SucceededまたはFailedが必要です。");
                        }

                        var resolvedAttemptRow = await storage.GetAsync("attempt:" + occurrence.AttemptId, cancellationToken) ?? throw new InvalidDataException("解決対象の試行が欠損しています。");
                        var resolvedAttempt = StorageRepository.Read<JobAttempt>(resolvedAttemptRow);
                        if (resolvedAttempt.OccurrenceId != occurrence.Id || resolvedAttempt.State != "Unknown")
                        {
                            throw new InvalidDataException("解決対象の試行状態が一致しません。");
                        }

                        resolvedAttempt.State = operation.Resolution;
                        resolvedAttempt.FinishedAt = clock.GetUtcNow();
                        resolvedAttempt.ResolutionReason = operation.Reason;
                        changes.Add(Update(resolvedAttemptRow, resolvedAttempt));
                        occurrence.State = operation.Resolution;
                        occurrence.FinishedAt = resolvedAttempt.FinishedAt;
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

        operation.CompletedAt = clock.GetUtcNow();
        changes.Add(Update(commandRow, operation));
        await CommitAsync(changes, cancellationToken);
    }

    /// <summary>旧データは採用版へ、新しい世代は保存した版へ固定して読み取ります。</summary>
    /// <param name="hash">参照する版。nullは旧保存形式です。</param>
    /// <param name="token">停止通知です。</param>
    /// <returns>該当版の完全名とユニットの対応です。</returns>
    private async Task<Dictionary<string, UnitDefinition>> ReadUnitsAsync(string? hash, CancellationToken token)
    {
        hash ??= active!.DefinitionHash;
        if (!versionUnits.TryGetValue(hash, out var units))
        {
            var snapshot = await definitions.ReadAsync(hash, token);
            units = snapshot.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
            versionUnits.Add(hash, units);
        }

        return units;
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
