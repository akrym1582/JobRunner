using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;

namespace JobRunner.Controllers.Jp1;

/// <summary>ローカル実行で観測した意味だけをJP1状態へ変換します。</summary>
public static class Jp1Status
{
    /// <summary>結果不明を異常終了へ丸めず、待機の理由も区別します。</summary>
    /// <param name="value">対象世代です。</param>
    /// <param name="units">世代に固定された定義です。</param>
    /// <param name="all">同じルート世代の状態です。</param>
    /// <param name="now">表示時刻です。</param>
    /// <returns>対応するStatus定数です。</returns>
    public static string Map(Occurrence value, Dictionary<string, UnitDefinition> units, IEnumerable<Occurrence> all, DateTimeOffset now)
    {
        var siblings = all.Where(o => o.RootId == value.RootId).ToDictionary(o => o.UnitPath, StringComparer.Ordinal);
        if (value.State == "Waiting")
        {
            string parent = ScheduleCalculator.Parent(value.UnitPath);
            if (siblings.TryGetValue(parent, out var parentValue))
            {
                if (parentValue.State is "Failed" or "Cancelled" or "Interrupted")
                {
                    return "UNEXEC";
                }

                if (parentValue.State != "Running" || DefinitionValidator.Edges(units[parent]).Where(e => e.To == value.UnitPath).Any(e =>
                    !siblings.TryGetValue(e.From, out var from) || (units[value.UnitPath].IsRecovery()
                        ? units[e.From].IsRecovery() ? from.State != "Succeeded" : from.State is not ("Failed" or "TimedOut" or "Interrupted")
                        : from.State is not ("Succeeded" or "Warning" or "NotScheduled") || (e.Conditional && from.ConditionMatched != true))))
                {
                    return "TERMWAIT";
                }
            }

            return value.Due > now ? "TIMEWAIT" : "EXECWAIT";
        }

        if (value.State == "Running" && units[value.UnitPath].IsNetwork())
        {
            var children = siblings.Values.Where(o => o.UnitPath.StartsWith(value.UnitPath + "/", StringComparison.Ordinal) && !units[o.UnitPath].IsRecovery()).ToList();
            return children.Any(o => o.State is "Failed" or "TimedOut" or "Unknown" or "Interrupted") ? "ABCONT"
                : children.Any(o => o.State == "Warning") ? "WACONT" : "RUNNING";
        }

        return value.State switch
        {
            "Held" => "HOLDING",
            "Starting" => "EXECWAIT",
            "Running" => "RUNNING",
            "Succeeded" => value.ConditionMatched == false ? "NORMALFALSE" : "NORMAL",
            "Warning" => "WARNING",
            "Failed" => value.StatusDetail == "FAIL" ? "FAIL" : "ABNORMAL",
            "TimedOut" => "KILL",
            "Unknown" => "UNKNOWN",
            "Interrupted" => "INTERRUPT",
            "Cancelled" or "Superseded" => "BYPASS",
            "NotScheduled" => value.ScheduleExcluded == true || value.StatusDetail == "BYPASS" ? "BYPASS" : "UNEXEC",
            "Skipped" => "UNEXEC",
            _ => throw new InvalidDataException("表示できない実行状態です。"),
        };
    }
}
