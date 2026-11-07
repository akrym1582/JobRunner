using System.Text.Json;
using Azure.Data.Tables;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Storage;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Services;

/// <summary>確定世代の件数補充と再開可能なネット単位の運用操作を担当します。</summary>
public sealed partial class SchedulerEngine
{
    /// <summary>固定した開始時刻を基点に、処理時間に依存せず一定間隔の予定を補充します。</summary>
    /// <param name="row">生成カーソルの登録行です。</param>
    /// <param name="registration">間隔と終了日時を持つ登録です。</param>
    /// <param name="token">権限喪失の通知です。</param>
    /// <returns>先読み範囲の生成完了です。</returns>
    private async Task ReplenishIntervalAsync(TableEntity row, Registration registration, CancellationToken token)
    {
        var units = await ReadUnitsAsync(registration.DefinitionHash, token);
        var next = registration.NextIntervalDue ?? registration.FixedDue!.Value;
        int interval = registration.IntervalMinutes!.Value;
        if (registration.PassedDaemonStarts != "IMMEDIATE" && next < recoveryBaseline)
        {
            next = next.AddMinutes(Math.Ceiling((recoveryBaseline - next).TotalMinutes / interval) * interval);
        }

        var until = clock.GetUtcNow().AddDays(options.ConfirmedHorizonDays);
        if (until <= registration.FixedDue)
        {
            until = registration.FixedDue.Value.AddTicks(1);
        }

        if (until > registration.IntervalUntil)
        {
            until = registration.IntervalUntil.Value;
        }

        var generation = JsonSerializer.Deserialize<Registration>(JsonSerializer.Serialize(registration))!;
        while (next < until)
        {
            generation.Holding = next == registration.FixedDue ? registration.Holding : null;
            await CreateOccurrenceAsync(generation, new SchedulePoint(1, BusinessToday(next), next), units[registration.RootPath], token);
            next = next.AddMinutes(interval);
            registration.NextIntervalDue = next;
            await CommitAsync([Update(row, registration)], token);
            row = (await storage.GetAsync(row.RowKey, token))!;
        }
    }

    /// <summary>明示された確定期間を分割して保存し、その後に未来世代枠を補充します。</summary>
    /// <param name="row">期間カーソルを持つ登録行です。</param>
    /// <param name="registration">開始日と排他的終了日を持つ登録です。</param>
    /// <param name="token">権限喪失の通知です。</param>
    /// <returns>期間内の確定と件数補充の完了です。</returns>
    private async Task ReplenishPeriodAsync(TableEntity row, Registration registration, CancellationToken token)
    {
        var units = await ReadUnitsAsync(registration.DefinitionHash, token);
        var root = units[registration.RootPath];
        var existing = (await storage.ListAsync("occurrence:", token)).Select(StorageRepository.Read<Occurrence>)
            .Where(o => o.Namespace == registration.Namespace && o.RegistrationId == registration.Id && o.Id == o.RootId).OrderBy(o => o.OriginalDue).ThenBy(o => o.Rule).ToList();
        var generation = JsonSerializer.Deserialize<Registration>(JsonSerializer.Serialize(registration))!;
        var first = existing.FirstOrDefault(o => o.State != "Superseded");

        while (registration.Through < registration.End!.Value)
        {
            DateOnly until = registration.Through.AddDays(Math.Min(366, registration.End.Value.DayNumber - registration.Through.DayNumber));
            foreach (var point in ScheduleCalculator.Calculate(root, units, registration, registration.Through, until, options).Distinct().OrderBy(p => p.Due).ThenBy(p => p.Rule))
            {
                // 中断後の再送でも最初の世代の保留を保持し、後続へ拡散させません。
                generation.Holding = first is null || (first.OriginalDue == point.Due && first.Rule == point.Rule) ? registration.Holding : null;
                await CreateOccurrenceAsync(generation, point, root, token);
                if (await storage.GetAsync("occurrence:" + GenerationId(generation, point), token) is not null)
                {
                    first ??= new Occurrence { OriginalDue = point.Due, Rule = point.Rule };
                }
            }

            registration.Through = until;
            await CommitAsync([Update(row, registration)], token);
            row = (await storage.GetAsync(row.RowKey, token))!;
        }

        if (registration.FutureGenerations is not null)
        {
            await ReplenishCountAsync(row, registration, token);
        }
    }

    /// <summary>未開始世代の枠を保ち、開始済み・取消済みの履歴は枠に数えません。</summary>
    /// <param name="row">更新前の登録行です。</param>
    /// <param name="registration">未来世代数を持つ登録です。</param>
    /// <param name="token">権限喪失の通知です。</param>
    /// <returns>必要な予定の生成を表します。</returns>
    private async Task ReplenishCountAsync(TableEntity row, Registration registration, CancellationToken token)
    {
        var units = await ReadUnitsAsync(registration.DefinitionHash, token);
        var root = units[registration.RootPath];
        var roots = (await storage.ListAsync("occurrence:", token)).Select(StorageRepository.Read<Occurrence>)
            .Where(o => o.Namespace == registration.Namespace && o.RegistrationId == registration.Id && o.Id == o.RootId).ToList();
        foreach (var incomplete in roots.Where(o => o.Materialized == false))
        {
            var repairRegistration = JsonSerializer.Deserialize<Registration>(JsonSerializer.Serialize(registration))!;
            repairRegistration.GenerationRevision = incomplete.GenerationRevision;
            repairRegistration.DefinitionHash = incomplete.DefinitionHash;
            await CreateOccurrenceAsync(repairRegistration, new SchedulePoint(incomplete.Rule, incomplete.BusinessDate, incomplete.OriginalDue), (await ReadUnitsAsync(incomplete.DefinitionHash, token))[incomplete.UnitPath], token);
        }

        int needed = registration.FutureGenerations!.Value - roots.Count(o => !o.Started && o.State is "Waiting" or "Held");
        DateOnly today = BusinessToday(clock.GetUtcNow());
        if (needed <= 0 || registration.ExhaustedOn == today)
        {
            return;
        }

        var generationRegistration = JsonSerializer.Deserialize<Registration>(JsonSerializer.Serialize(registration))!;
        generationRegistration.Holding = roots.All(o => o.State == "Superseded") ? registration.Holding : null;
        if (registration.Start is not null)
        {
            generationRegistration.End = null;
        }

        var last = roots.Where(o => o.State != "Superseded" && o.GenerationRevision == registration.GenerationRevision).OrderBy(o => o.OriginalDue).ThenBy(o => o.Rule).LastOrDefault();
        DateTimeOffset baseline = registration.CreatedAt ?? active!.Baseline;
        if (baseline < recoveryBaseline)
        {
            baseline = recoveryBaseline;
        }

        // 再開境界より前を件数へ補充し続けず、長期停止後も次の未来予定から探索します。
        foreach (var cutoff in (await MembershipControlsAsync(registration.GroupCatalogHash, registration.RootPath, token)).Select(c => c.SkipBefore).Append(runtime.SkipBefore).Append(registration.GenerationNotBefore))
        {
            if (cutoff > baseline)
            {
                baseline = cutoff.Value;
            }
        }

        // 48時間制や基準時刻加算では、次の実日時が前の業務日に属することがあります。
        DateOnly from = BusinessToday(last is not null && last.OriginalDue > baseline ? last.OriginalDue : baseline);
        DateOnly limit = from.AddYears(Math.Min(100, 9998 - from.Year));
        while (from < limit && needed > 0)
        {
            DateOnly until = from.AddYears(1);
            if (until > limit)
            {
                until = limit;
            }

            foreach (var point in ScheduleCalculator.Calculate(root, units, generationRegistration, from.AddDays(-3), until, options).Distinct().OrderBy(p => p.Due).ThenBy(p => p.Rule))
            {
                DateOnly actualDay = BusinessToday(point.Due);
                if (IsExcluded(registration, point.BusinessDate, point.Due) || actualDay < from || actualDay >= until || point.Due < baseline || (last is not null && (point.Due < last.OriginalDue || (point.Due == last.OriginalDue && point.Rule <= last.Rule))))
                {
                    continue;
                }

                await CreateOccurrenceAsync(generationRegistration, point, root, token);
                var created = await storage.GetAsync("occurrence:" + GenerationId(generationRegistration, point), token);
                if (created is not null && StorageRepository.Read<Occurrence>(created).State is "Waiting" or "Held")
                {
                    generationRegistration.Holding = null;
                    needed--;
                }

                if (needed == 0)
                {
                    break;
                }
            }

            from = until;
        }

        if (needed > 0)
        {
            registration.ExhaustedOn = today;
            await CommitAsync([Update(row, registration)], token);
        }
    }

    /// <summary>起動判定や過去予定の除外より先に、中断したネット操作を完了させます。</summary>
    /// <param name="token">権限喪失の通知です。</param>
    /// <returns>保存済み変更計画の再開完了を表します。</returns>
    private async Task ResumeMutationsAsync(CancellationToken token)
    {
        foreach (var row in await storage.ListAsync("command:", token))
        {
            var operation = StorageRepository.Read<Operation>(row);
            if (operation.Status == "Pending" && operation.MutationPath is not null)
            {
                await new OperationMutationService(storage, clock).ResumeAsync(row, CommitAsync, StopOwnedAsync, token, operation.Kind == "Activate" ? FinalizePublicationAsync : null);
                await RestoreRuntimeAsync(token);
            }
        }
    }

    /// <summary>同じ稼働系が所有するタスクだけを停止し、PIDの再利用を避けます。</summary>
    /// <param name="id">停止する世代IDです。</param>
    /// <returns>停止要求の通知を表し、業務結果の確定を意味しません。</returns>
    private async Task StopOwnedAsync(string id)
    {
        leader!.Ensure();
        if (running.TryGetValue(id, out var task))
        {
            await task.Stop.CancelAsync();
        }
    }

    /// <summary>ネットの子孫操作を先に計画し、途中停止でも同じ差分を二度適用しません。</summary>
    /// <param name="commandRow">操作行です。</param>
    /// <param name="operation">操作内容です。</param>
    /// <param name="targetRow">対象行です。</param>
    /// <param name="target">対象の保存状態です。</param>
    /// <param name="token">権限喪失の通知です。</param>
    /// <returns>この経路で操作を完了した場合trueです。</returns>
    private async Task<bool> ApplyTreeOperationAsync(TableEntity commandRow, Operation operation, TableEntity targetRow, Occurrence target, CancellationToken token)
    {
        var units = await ReadUnitsAsync(target.DefinitionHash, token);
        bool network = units.TryGetValue(target.UnitPath, out var unit) && unit.IsNetwork();
        if (!network || operation.Kind is not ("Stop" or "Rerun"))
        {
            return false;
        }

        var rows = network
            ? (await storage.ListAsync("occurrence:", token)).Where(r =>
            {
                var value = StorageRepository.Read<Occurrence>(r);
                return value.RootId == target.RootId && (value.Id == target.Id || value.UnitPath.StartsWith(target.UnitPath + "/", StringComparison.Ordinal));
            }).ToList()
            : [targetRow];
        var values = rows.Select(r => (Row: r, Value: StorageRepository.Read<Occurrence>(r))).ToList();
        var plan = new OperationMutation { OperationId = operation.Id };
        if (operation.Kind == "Stop")
        {
            if (target.State != "Running")
            {
                throw new FormatException("強制終了の対象ネットは実行中である必要があります。");
            }

            plan.StopIds = values.Where(v => running.ContainsKey(v.Value.Id)).Select(v => v.Value.Id).ToList();
            foreach (var item in values)
            {
                if (item.Value.State is "Waiting" or "Held")
                {
                    item.Value.State = "Cancelled";
                    item.Value.FinishedAt = clock.GetUtcNow();
                }
                else if (item.Value.State == "Running" && units[item.Value.UnitPath].IsNetwork())
                {
                    item.Value.State = "Interrupted";
                    item.Value.FinishedAt = clock.GetUtcNow();
                }
            }

            // 実行試行の結果はCollectまたは復旧がUnknownとして記録します。変更計画で結果を上書きしません。
            values = values.Where(v => v.Value.State is not ("Running" or "Starting" or "Unknown")).ToList();
        }
        else
        {
            if ((operation.RerunOptions?.Any(o => o != "HOLD") ?? false) || operation.RerunMethod != "TOP" || target.State is not ("Succeeded" or "Warning" or "Failed" or "TimedOut" or "Cancelled" or "Interrupted")
                || values.Any(v => v.Value.State is "Running" or "Starting" or "Unknown" or "Archiving" or "Archived"))
            {
                throw new FormatException("ネット再実行はTOPを指定し、結果不明・実行中の子を先に解決してください。");
            }

            foreach (var excluded in values.Where(v => v.Value.ScheduleExcluded == true))
            {
                excluded.Value.State = "NotScheduled";
            }

            foreach (var item in values.Where(v => v.Value.ScheduleExcluded != true))
            {
                item.Value.State = (units[item.Value.UnitPath].Get("ha", "n") == "y" || (item.Value.Id == target.Id && operation.RerunOptions?.Contains("HOLD") == true)) ? "Held" : "Waiting";
                item.Value.HoldOverride = item.Value.Id == target.Id && operation.RerunOptions?.Contains("HOLD") == true ? true : null;
                item.Value.IndividuallyModified = true;
                item.Value.ManualExitCode = null;
                item.Value.InterruptRequested = null;
                item.Value.StatusDetail = null;
                item.Value.AttemptId = null;
                item.Value.BypassedPredecessors = null;
                item.Value.StandaloneRerun = null;
                item.Value.ConditionMatched = null;
                item.Value.FinishedAt = null;

                // 手動で再開するルートを未来枠へ数えず、復旧で過去予定として除外しません。
                if (item.Value.Id == item.Value.RootId)
                {
                    item.Value.Started = true;
                }

                item.Value.Due = clock.GetUtcNow();
                item.Value.Reason = "TOP方式で再実行します。過去の試行は維持します。";
            }

            foreach (var row in await storage.ListAsync("occurrence:", token))
            {
                var ancestor = StorageRepository.Read<Occurrence>(row);
                if (ancestor.RootId == target.RootId && ancestor.State is "Failed" or "Interrupted" && target.UnitPath.StartsWith(ancestor.UnitPath + "/", StringComparison.Ordinal))
                {
                    ancestor.InterruptRequested = null;
                    ancestor.State = "Running";
                    ancestor.FinishedAt = null;
                    values.Add((row, ancestor));
                }
            }
        }

        plan.Changes = values.Select(v => new ArchivedRow(v.Row.RowKey, v.Row.ETag.ToString(), JsonSerializer.Serialize(v.Value))).ToList();
        await new OperationMutationService(storage, clock).ApplyAsync(commandRow, operation, plan, CommitAsync, leader!.Ensure, StopOwnedAsync, token);
        return true;
    }
}
