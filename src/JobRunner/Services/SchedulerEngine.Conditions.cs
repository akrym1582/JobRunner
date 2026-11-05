using System.Globalization;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;

namespace JobRunner.Services;

/// <summary>保存済み結果に基づき、条件分岐と異常時後続を判定します。</summary>
public sealed partial class SchedulerEngine
{
    /// <summary>明示的な不成立だけを非実行へ進め、結果不明を成功として扱いません。</summary>
    /// <param name="occurrence">判定する世代です。</param>
    /// <param name="units">固定定義です。</param>
    /// <param name="all">同じ読み取り時点の世代です。</param>
    /// <returns>実行しない条件が確定している場合trueです。</returns>
    private static bool ShouldExclude(Occurrence occurrence, Dictionary<string, UnitDefinition> units, IEnumerable<Occurrence> all)
    {
        var siblings = all.Where(o => o.RootId == occurrence.RootId).ToDictionary(o => o.UnitPath, StringComparer.Ordinal);
        string parent = ScheduleCalculator.Parent(occurrence.UnitPath);
        if (siblings[parent].State == "NotScheduled")
        {
            return true;
        }

        var incoming = DefinitionValidator.Edges(units[parent]).Where(e => e.To == occurrence.UnitPath).ToList();
        if (incoming.Any(e => e.Conditional && siblings[e.From].State == "Succeeded" && siblings[e.From].ConditionMatched == false))
        {
            return true;
        }

        return units[occurrence.UnitPath].Type is "rj" or "rp" && incoming.Count > 0 && incoming.All(e => siblings[e.From].State is "Succeeded" or "Warning" or "NotScheduled");
    }

    /// <summary>符号なし終了コードをJP1の比較条件で判定します。</summary>
    /// <param name="unit">判定ジョブの定義です。</param>
    /// <param name="code">永続化された終了コードです。</param>
    /// <returns>判定条件の成立を表します。</returns>
    private static bool EvaluateCondition(UnitDefinition unit, uint code)
    {
        uint threshold = uint.Parse(unit.Get("ejc", "0"), CultureInfo.InvariantCulture);
        uint lower = uint.Parse(unit.Get("ejl", "0"), CultureInfo.InvariantCulture);
        uint upper = uint.Parse(unit.Get("ejh", "0"), CultureInfo.InvariantCulture);
        bool within = (unit.Get("ejm", "ge") == "ge" ? code >= lower : code > lower) && (unit.Get("eju", "le") == "le" ? code <= upper : code < upper);
        return unit.Get("ej", "gt") switch
        {
            "gt" => code > threshold,
            "ge" => code >= threshold,
            "lt" => code < threshold,
            "le" => code <= threshold,
            "eq" => code == threshold,
            "ne" => code != threshold,
            "ri" => within,
            "ro" => !within,
            _ => throw new InvalidDataException("保存済み判定条件が未対応です。"),
        };
    }
}
