using System.Text.Json;
using Azure.Data.Tables;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Storage;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Services;

/// <summary>追加世代を含む予定変更を、復旧可能な一つの操作として保存します。</summary>
public sealed partial class SchedulerEngine
{
    /// <summary>変更前ETagと変更後値を一緒に保存します。</summary>
    /// <param name="row">変更前の行です。</param>
    /// <param name="value">変更後の値です。</param>
    /// <returns>再開時に照合する変更です。</returns>
    private static ArchivedRow Change(TableEntity row, Occurrence value) => new(row.RowKey, row.ETag.ToString(), JsonSerializer.Serialize(value));

    /// <summary>変更途中の状態を実行判定へ渡さず、取消と復元も同じ経路で適用します。</summary>
    /// <param name="command">操作行です。</param>
    /// <param name="operation">変更要求です。</param>
    /// <param name="targetRow">対象行です。</param>
    /// <param name="target">対象世代です。</param>
    /// <param name="token">停止通知です。</param>
    /// <returns>予定操作を処理した場合trueです。</returns>
    private async Task<bool> ApplyScheduleChangeAsync(TableEntity command, Operation operation, TableEntity targetRow, Occurrence target, CancellationToken token)
    {
        if (operation.Kind is not ("Move" or "RunNow" or "Restore" or "Cancel"))
        {
            return false;
        }

        var units = await ReadUnitsAsync(target.DefinitionHash, token);
        var allRows = await storage.ListAsync("occurrence:", token);
        var values = allRows.Select(r => (Row: r, Value: StorageRepository.Read<Occurrence>(r)))
            .Where(v => v.Value.RootId == target.RootId && (v.Value.Id == target.Id || v.Value.UnitPath.StartsWith(target.UnitPath + "/", StringComparison.Ordinal))).ToList();
        if (values.Any(v => v.Value.StatusDetail == "REGISTRATION_CANCELLED" || v.Value.Started || v.Value.State is not ("Waiting" or "Held" or "NotScheduled" or "Cancelled")))
        {
            throw new FormatException("開始済みの世代は予定変更できません。");
        }

        if (operation.Kind != "Restore" && values.Any(v => v.Value.State == "Cancelled"))
        {
            throw new FormatException("取消済みの予定は変更解除を先に行ってください。");
        }

        string lower = operation.ChangeLower ?? "SHIFT";
        string ahead = operation.PushedAhead ?? "MOVE";
        if (lower is not ("SHIFT" or "NOTSHIFT") || ahead is not ("ADD" or "MOVE"))
        {
            throw new FormatException("予定変更方式が不正です。");
        }

        var plan = new OperationMutation { FormatVersion = 2, OperationId = operation.Id };
        if (operation.Kind == "Restore")
        {
            var changeIds = values.Select(v => v.Value.ScheduleChangeId).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
            var related = allRows.Select(r => (Row: r, Value: StorageRepository.Read<Occurrence>(r))).ToList();

            // 追加した世代をさらにADDした場合も、関係する全世代をたどってから原子的に拒否・適用します。
            if (target.Id == target.RootId)
            {
                bool expanded;
                do
                {
                    expanded = false;
                    foreach (var value in related.Select(v => v.Value).Where(v => v.AddedByChangeId is not null && changeIds.Contains(v.AddedByChangeId) && v.ScheduleChangeId is not null))
                    {
                        expanded |= changeIds.Add(value.ScheduleChangeId);
                    }
                }
                while (expanded);
            }

            var additions = related.Where(v => target.Id == target.RootId && v.Value.AddedByChangeId is not null && changeIds.Contains(v.Value.AddedByChangeId)).ToList();
            if (additions.Any(v => v.Value.Started || v.Value.State is not ("Waiting" or "Held" or "NotScheduled" or "Cancelled")))
            {
                throw new FormatException("追加世代が開始済みなので変更解除できません。");
            }

            foreach (var item in additions)
            {
                item.Value.State = "Cancelled";
                item.Value.StatusDetail = "BYPASS";
                item.Value.FinishedAt = clock.GetUtcNow();
                plan.Changes.Add(Change(item.Row, item.Value));
            }

            foreach (var item in values)
            {
                if (item.Value.State == "Cancelled" && item.Value.BeforeChangeState is null)
                {
                    throw new FormatException("復元元のない取消世代です。");
                }

                item.Value.Due = item.Value.BeforeChangeDue ?? item.Value.OriginalDue;
                item.Value.State = item.Value.BeforeChangeState ?? item.Value.State;
                item.Value.HoldOverride = item.Value.ScheduleChangeId is null ? item.Value.HoldOverride : item.Value.BeforeChangeHoldOverride;
                item.Value.BeforeChangeHoldOverride = null;
                item.Value.BeforeChangeDue = null;
                item.Value.BeforeChangeState = null;
                item.Value.ScheduleChangeId = null;
                item.Value.StatusDetail = null;
                item.Value.FinishedAt = null;
            }
        }
        else
        {
            DateTimeOffset due = operation.Kind == "RunNow" ? clock.GetUtcNow() : operation.Due ?? target.Due;
            if (operation.Kind == "Move")
            {
                if (operation.Due.HasValue == operation.RelativeMinutes.HasValue)
                {
                    throw new FormatException("絶対日時または相対分の一方を指定してください。");
                }

                if (operation.RelativeMinutes is int minutes)
                {
                    if (minutes == 0 || Math.Abs((long)minutes) > (99 * 1440) + 2879)
                    {
                        throw new FormatException("相対時間が範囲外です。");
                    }

                    var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
                    var local = TimeZoneInfo.ConvertTime(target.Due, zone).DateTime.AddMinutes(minutes);
                    if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
                    {
                        throw new FormatException("相対変更後の日時が存在しないか曖昧です。");
                    }

                    due = new DateTimeOffset(local, zone.GetUtcOffset(local));
                }
            }

            string changeId = target.ScheduleChangeId ?? operation.Id;
            if (operation.Kind is "Move" or "RunNow" && target.Id == target.RootId && ahead == "ADD" && due < target.Due)
            {
                string rootId = StorageRepository.Hash("schedule-add:" + operation.Id + ":" + target.Id);
                var sequenceRow = await storage.GetAsync("execution-sequence", token);
                long sequence = (sequenceRow is null ? 0 : StorageRepository.Read<long>(sequenceRow)) + 1;
                foreach (var item in values)
                {
                    var copy = JsonSerializer.Deserialize<Occurrence>(JsonSerializer.Serialize(item.Value))!;
                    copy.Id = copy.Id == target.Id ? rootId : StorageRepository.Hash(rootId + ":" + copy.UnitPath);
                    copy.RootId = rootId;
                    copy.OriginalDue = copy.Due;
                    copy.Jp1ExecutionId = copy.Id == rootId ? FormatExecutionId(sequence) : null;
                    copy.Materialized = copy.Id == rootId ? true : null;
                    copy.AddedByChangeId = changeId;
                    copy.ScheduleChangeId = null;
                    copy.BeforeChangeDue = null;
                    copy.BeforeChangeState = null;
                    copy.BeforeChangeHoldOverride = null;
                    plan.Changes.Add(new ArchivedRow("occurrence:" + copy.Id, string.Empty, JsonSerializer.Serialize(copy)));
                }

                plan.Changes.Add(new ArchivedRow("execution-sequence", sequenceRow?.ETag.ToString() ?? string.Empty, JsonSerializer.Serialize(sequence)));
            }

            var delta = due - target.Due;
            var shifts = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
            foreach (var item in values.OrderBy(v => v.Value.UnitPath.Count(c => c == '/')))
            {
                if (item.Value.ScheduleChangeId is null)
                {
                    item.Value.BeforeChangeHoldOverride = item.Value.HoldOverride;
                }

                item.Value.BeforeChangeDue ??= item.Value.Due;
                item.Value.BeforeChangeState ??= item.Value.State;
                item.Value.ScheduleChangeId ??= changeId;
                if (operation.Kind == "Cancel")
                {
                    item.Value.State = "Cancelled";
                    item.Value.StatusDetail = "BYPASS";
                    item.Value.FinishedAt = clock.GetUtcNow();
                    continue;
                }

                var unit = units[item.Value.UnitPath];
                bool independent = item.Value.Id != target.Id && unit.IsNetwork() && unit.Get("de", "y") == "n";
                var shift = item.Value.Id == target.Id || lower == "SHIFT" ? delta
                    : independent ? TimeSpan.Zero : shifts[ScheduleCalculator.Parent(unit.Path)];
                shifts[unit.Path] = shift;
                try
                {
                    item.Value.Due += shift;
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    throw new FormatException("変更後日時が範囲外です。", ex);
                }
            }
        }

        plan.Changes.AddRange(values.Select(v => Change(v.Row, v.Value)));
        await new OperationMutationService(storage, clock).ApplyAsync(command, operation, plan, CommitAsync, leader!.Ensure, StopOwnedAsync, token);
        return true;
    }

    /// <summary>登録解除済みの期間を、予定生成前にも検査します。</summary>
    /// <param name="registration">登録です。</param>
    /// <param name="businessDate">実行日です。</param>
    /// <param name="due">予定日時です。</param>
    /// <returns>解除範囲に含まれる場合trueです。</returns>
    private bool IsExcluded(Registration registration, DateOnly businessDate, DateTimeOffset due)
    {
        var calendarDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(due, TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone)).DateTime);
        return registration.Exclusions?.Any(e => (e.DateType == "EXECUTION_DATE" ? businessDate : calendarDate) >= e.Begin && (e.DateType == "EXECUTION_DATE" ? businessDate : calendarDate) <= e.End) == true;
    }

    /// <summary>解除範囲と既存未開始世代の取消を一つの変更計画にします。</summary>
    /// <param name="command">操作行です。</param>
    /// <param name="operation">対象ルートと任意の期間です。</param>
    /// <param name="token">停止通知です。</param>
    /// <returns>変更計画の適用完了です。</returns>
    private async Task UnregisterRootAsync(TableEntity command, Operation operation, CancellationToken token)
    {
        if ((operation.Start.HasValue || operation.End.HasValue || operation.DateType is not null)
            && (!operation.Start.HasValue || !operation.End.HasValue || operation.Start > operation.End || operation.DateType is not ("CALENDAR_DATE" or "EXECUTION_DATE")))
        {
            throw new FormatException("解除期間はdateType、開始日、終了日を組み合わせて指定してください。");
        }

        var registrations = new Dictionary<string, Registration>(StringComparer.Ordinal);
        var plan = new OperationMutation { OperationId = operation.Id };
        foreach (var row in await storage.ListAsync($"registration:{active!.Id}:", token))
        {
            var registration = StorageRepository.Read<Registration>(row);
            if (registration.RootPath != operation.Target)
            {
                continue;
            }

            if (operation.Start.HasValue)
            {
                registration.Exclusions ??= [];
                registration.Exclusions.Add(new RegistrationExclusion(operation.DateType!, operation.Start.Value, operation.End!.Value));
            }
            else
            {
                registration.Disabled = true;
            }

            registration.ExhaustedOn = null;
            registrations.Add(registration.Id, registration);
            plan.Changes.Add(new ArchivedRow(row.RowKey, row.ETag.ToString(), JsonSerializer.Serialize(registration)));
        }

        if (registrations.Count == 0)
        {
            throw new FormatException("解除対象の登録がありません。");
        }

        var values = (await storage.ListAsync("occurrence:", token)).Select(r => (Row: r, Value: StorageRepository.Read<Occurrence>(r))).ToList();
        var cancelled = values.Where(v => v.Value.Namespace == active!.Id && v.Value.Id == v.Value.RootId && !v.Value.Started && registrations.TryGetValue(v.Value.RegistrationId, out var registration)
            && (registration.Disabled || IsExcluded(registration, v.Value.BusinessDate, v.Value.Due))).Select(v => v.Value.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in values.Where(v => cancelled.Contains(v.Value.RootId) && v.Value.State is "Waiting" or "Held" or "NotScheduled" or "Cancelled"))
        {
            item.Value.State = "Cancelled";
            item.Value.StatusDetail = "REGISTRATION_CANCELLED";
            item.Value.FinishedAt = clock.GetUtcNow();
            plan.Changes.Add(Change(item.Row, item.Value));
        }

        await new OperationMutationService(storage, clock).ApplyAsync(command, operation, plan, CommitAsync, leader!.Ensure, StopOwnedAsync, token);
    }
}
