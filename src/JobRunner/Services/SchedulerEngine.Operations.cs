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
    /// <summary>未開始世代の枠を保ち、開始済み・取消済みの履歴は枠に数えません。</summary>
    /// <param name="row">更新前の登録行です。</param>
    /// <param name="registration">未来世代数を持つ登録です。</param>
    /// <param name="token">権限喪失の通知です。</param>
    /// <returns>必要な予定の生成を表します。</returns>
    private async Task ReplenishCountAsync(TableEntity row, Registration registration, CancellationToken token)
    {
        var units = bundle!.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        var root = units[registration.RootPath];
        var roots = (await storage.ListAsync("occurrence:", token)).Select(StorageRepository.Read<Occurrence>)
            .Where(o => o.Namespace == registration.Namespace && o.RegistrationId == registration.Id && o.Id == o.RootId).ToList();
        foreach (var incomplete in roots.Where(o => o.Materialized == false))
        {
            await CreateOccurrenceAsync(registration, new SchedulePoint(incomplete.Rule, incomplete.BusinessDate, incomplete.OriginalDue), root, token);
        }

        int needed = registration.FutureGenerations!.Value - roots.Count(o => !o.Started && o.State is "Waiting" or "Held");
        DateOnly today = BusinessToday(clock.GetUtcNow());
        if (needed <= 0 || registration.ExhaustedOn == today)
        {
            return;
        }

        var generationRegistration = JsonSerializer.Deserialize<Registration>(JsonSerializer.Serialize(registration))!;
        generationRegistration.Holding = roots.Count == 0 ? registration.Holding : null;
        var last = roots.OrderBy(o => o.OriginalDue).ThenBy(o => o.Rule).LastOrDefault();
        DateTimeOffset baseline = registration.CreatedAt ?? active!.Baseline;

        // 48時間制や基準時刻加算では、次の実日時が前の業務日に属することがあります。
        DateOnly from = BusinessToday(last?.OriginalDue ?? baseline);
        DateOnly limit = from.AddYears(Math.Min(100, 9998 - from.Year));
        while (from < limit && needed > 0)
        {
            DateOnly until = from.AddYears(1);
            if (until > limit)
            {
                until = limit;
            }

            foreach (var point in ScheduleCalculator.Calculate(root, units, registration, from.AddDays(-3), until, options).Distinct().OrderBy(p => p.Due).ThenBy(p => p.Rule))
            {
                DateOnly actualDay = BusinessToday(point.Due);
                if (actualDay < from || actualDay >= until || point.Due < baseline || (last is not null && (point.Due < last.OriginalDue || (point.Due == last.OriginalDue && point.Rule <= last.Rule))))
                {
                    continue;
                }

                await CreateOccurrenceAsync(generationRegistration, point, root, token);
                generationRegistration.Holding = null;
                needed--;
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
                await new OperationMutationService(storage, clock).ResumeAsync(row, CommitAsync, StopOwnedAsync, token);
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
        var units = bundle!.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        bool network = units.TryGetValue(target.UnitPath, out var unit) && unit.Type == "n";
        if (!(network && operation.Kind is "Move" or "Restore" or "Cancel" or "Stop" or "Rerun") && operation.Kind != "RunNow")
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
        bool pending = values.All(v => v.Value.State is "Waiting" or "Held");
        if (operation.Kind is "Move" or "Restore" or "RunNow" or "Cancel")
        {
            if (!pending || (operation.Kind == "Move" && operation.Due is null))
            {
                throw new FormatException("未開始の対象一式だけを変更できます。MoveにはDueが必要です。");
            }

            var delta = (operation.Kind == "RunNow" ? clock.GetUtcNow() : operation.Due ?? target.Due) - target.Due;
            foreach (var item in values)
            {
                if (operation.Kind == "Cancel")
                {
                    item.Value.State = "Cancelled";
                    item.Value.FinishedAt = clock.GetUtcNow();
                }
                else
                {
                    try
                    {
                        item.Value.Due = operation.Kind == "Restore" ? item.Value.OriginalDue : item.Value.Due + delta;
                    }
                    catch (ArgumentOutOfRangeException ex)
                    {
                        throw new FormatException("子孫の変更後日時が有効範囲を超えます。", ex);
                    }
                }
            }
        }
        else if (operation.Kind == "Stop")
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
                else if (item.Value.State == "Running" && units[item.Value.UnitPath].Type == "n")
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
            if (operation.RerunMethod != "TOP" || target.State is not ("Succeeded" or "Warning" or "Failed" or "TimedOut" or "Cancelled" or "Interrupted")
                || values.Any(v => v.Value.State is "Running" or "Starting" or "Unknown" or "Archiving" or "Archived"))
            {
                throw new FormatException("ネット再実行はTOPを指定し、結果不明・実行中の子を先に解決してください。");
            }

            foreach (var item in values)
            {
                item.Value.State = units[item.Value.UnitPath].Get("ha", "n") == "y" ? "Held" : "Waiting";
                item.Value.AttemptId = null;
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
