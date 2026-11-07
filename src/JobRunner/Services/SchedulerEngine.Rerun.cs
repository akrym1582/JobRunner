using System.Text.Json;
using Azure.Data.Tables;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Storage;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Services;

/// <summary>選択した異常箇所と後続だけを再開可能な変更計画へ変換します。</summary>
public sealed partial class SchedulerEngine
{
    /// <summary>TOP以外の再実行を保存済み世代のグラフへ適用します。</summary>
    /// <param name="command">操作行です。</param>
    /// <param name="operation">再実行方式です。</param>
    /// <param name="target">対象の世代です。</param>
    /// <param name="token">権限喪失の通知です。</param>
    /// <returns>この経路で適用した場合trueです。</returns>
    private async Task<bool> ApplySelectiveRerunAsync(TableEntity command, Operation operation, Occurrence target, CancellationToken token)
    {
        if (operation.Kind != "Rerun" || operation.RerunMethod is null or "TOP")
        {
            return false;
        }

        var units = await ReadUnitsAsync(target.DefinitionHash, token);
        var rows = (await storage.ListAsync("occurrence:", token)).Where(r => StorageRepository.Read<Occurrence>(r).RootId == target.RootId).ToList();
        var values = rows.ToDictionary(r => StorageRepository.Read<Occurrence>(r).UnitPath, r => (Row: r, Value: StorageRepository.Read<Occurrence>(r)), StringComparer.Ordinal);
        bool root = target.Id == target.RootId;
        string method = operation.RerunMethod;
        if ((root && method is not ("ABNORMAL_JOB" or "ABNORMAL_NEXT" or "ABNORMAL_JOBNET" or "WARNING"))
            || (!root && method is not ("FROM" or "ONLY" or "NEXT"))
            || values.Values.Any(v => v.Value.State is "Starting" or "Running" or "Unknown" or "Archiving" or "Archived")
            || target.State is not ("Succeeded" or "Warning" or "Failed" or "TimedOut" or "Cancelled" or "Interrupted"))
        {
            throw new FormatException("再実行方式と対象状態が一致しないか、未解決・実行中の世代があります。");
        }

        var rerunOptions = operation.RerunOptions ?? [];
        if (rerunOptions.Any(o => o is not ("HOLD" or "WARNING")) || (rerunOptions.Contains("HOLD") && method == "NEXT") || (root && rerunOptions.Contains("WARNING") && method != "ABNORMAL_NEXT"))
        {
            throw new FormatException("再実行オプションの組み合わせが不正です。");
        }

        var seeds = root
            ? values.Values.Where(v => !units[v.Value.UnitPath].IsNetwork() && (method == "WARNING" ? v.Value.State == "Warning" : v.Value.State is "Failed" or "TimedOut" or "Interrupted")).Select(v => v.Value.UnitPath).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>([target.UnitPath], StringComparer.Ordinal);
        if (method == "ABNORMAL_JOBNET")
        {
            seeds = values.Values.Where(v => v.Value.Id != v.Value.RootId && units[v.Value.UnitPath].IsNetwork() && v.Value.State is "Failed" or "Interrupted").Select(v => v.Value.UnitPath).ToHashSet(StringComparer.Ordinal);
        }

        if (seeds.Count == 0)
        {
            throw new FormatException("指定方式に該当する再実行対象がありません。");
        }

        var bypassed = values.Values.Where(v => v.Value.Id != v.Value.RootId && v.Value.State is "Failed" or "TimedOut" or "Interrupted" && seeds.Any(seed => seed == v.Value.UnitPath || seed.StartsWith(v.Value.UnitPath + "/", StringComparison.Ordinal))).Select(v => v.Value.UnitPath).ToHashSet(StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(seeds);
        bool only = method is "ONLY" or "WARNING";
        bool next = method is "NEXT" or "ABNORMAL_NEXT";
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out string? path))
        {
            if (!visited.Add(path))
            {
                continue;
            }

            if (!next || !seeds.Contains(path))
            {
                selected.Add(path);
                foreach (var descendant in values.Keys.Where(p => p.StartsWith(path + "/", StringComparison.Ordinal)))
                {
                    selected.Add(descendant);
                }
            }

            if (!only)
            {
                string parent = ScheduleCalculator.Parent(path);
                while (units.TryGetValue(parent, out var network) && network.IsNetwork())
                {
                    foreach (var edge in DefinitionValidator.Connections(network).Where(e => e.From == path))
                    {
                        queue.Enqueue(edge.To);
                    }

                    path = parent;
                    parent = ScheduleCalculator.Parent(parent);
                }
            }
        }

        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in selected)
        {
            var value = values[path].Value;
            if (value.ScheduleExcluded == true)
            {
                value.State = "NotScheduled";
                changed.Add(path);
                continue;
            }

            value.State = (units[path].Get("ha", "n") == "y" || (path == target.UnitPath && rerunOptions.Contains("HOLD"))) ? "Held" : "Waiting";
            value.IndividuallyModified = true;
            value.HoldOverride = path == target.UnitPath && rerunOptions.Contains("HOLD") ? true : null;
            value.ManualExitCode = null;
            value.InterruptRequested = null;
            value.StatusDetail = null;
            value.AttemptId = null;
            value.ConditionMatched = null;
            value.StandaloneRerun = method == "ONLY" && path == target.UnitPath ? true : null;
            value.FinishedAt = null;
            value.Due = clock.GetUtcNow();
            value.BypassedPredecessors = next ? bypassed.Select(p => values[p].Value.Id).ToList() : null;
            value.Reason = method + "方式で再実行します。過去の試行は維持します。";
            changed.Add(path);
        }

        foreach (string path in method == "ONLY" ? Array.Empty<string>() : selected.ToArray())
        {
            string parent = ScheduleCalculator.Parent(path);
            while (values.TryGetValue(parent, out var ancestor))
            {
                if (!selected.Contains(parent))
                {
                    ancestor.Value.State = ancestor.Value.Id == target.Id && rerunOptions.Contains("HOLD") ? "Held" : "Running";
                    ancestor.Value.InterruptRequested = null;
                    ancestor.Value.Started = true;
                    ancestor.Value.FinishedAt = null;
                    changed.Add(parent);
                }

                parent = ScheduleCalculator.Parent(parent);
            }
        }

        if (rerunOptions.Contains("WARNING"))
        {
            var predecessors = root ? bypassed : DefinitionValidator.Connections(units[ScheduleCalculator.Parent(target.UnitPath)]).Where(e => e.To == target.UnitPath).Select(e => e.From);
            foreach (string path in predecessors)
            {
                if (values[path].Value.State is "Failed" or "TimedOut" or "Interrupted")
                {
                    values[path].Value.State = "Warning";
                    changed.Add(path);
                }
            }
        }

        var plan = new OperationMutation
        {
            OperationId = operation.Id,
            Changes = changed.Select(p => new ArchivedRow(values[p].Row.RowKey, values[p].Row.ETag.ToString(), JsonSerializer.Serialize(values[p].Value))).ToList(),
        };
        await new OperationMutationService(storage, clock).ApplyAsync(command, operation, plan, CommitAsync, leader!.Ensure, StopOwnedAsync, token);
        return true;
    }
}
