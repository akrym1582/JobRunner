using System.Globalization;
using JobRunner.Models;

namespace JobRunner.Scheduling;

/// <summary>固定定義だけからネストの予定を選び、実行器と下書き試算の意味を揃えます。</summary>
public static class NestedScheduleSelector
{
    /// <summary>上位の採用ルール・業務日に対応する、最も早い有効な子の予定を選びます。</summary>
    /// <param name="unit">ネストネットです。</param>
    /// <param name="all">同じ固定版の定義です。</param>
    /// <param name="registration">ルートの仮登録または実登録です。</param>
    /// <param name="parent">直上のスケジュール採否です。</param>
    /// <param name="options">時間帯と時間制です。</param>
    /// <param name="token">要求取消または試算期限です。</param>
    /// <returns>保存・起動を行わない採否です。</returns>
    public static NestedScheduleSelection Select(UnitDefinition unit, IReadOnlyDictionary<string, UnitDefinition> all, Registration registration, NestedScheduleSelection parent, RunnerOptions options, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!parent.Enabled)
        {
            return new(parent.Point, false, "ANCESTOR_EXCLUDED");
        }

        if (unit.Get("de", "y") == "y" || !unit.Attributes.Keys.Any(k => k is "sd" or "st" or "ln" or "cy" or "sh" or "shd" or "cftd" or "ed"))
        {
            return new(parent.Point, true, "INHERITED");
        }

        // 元の属性・子孫は書き換えず、計算に必要なsdだけを対応ルールへ絞ります。
        var linked = new UnitDefinition { Path = unit.Path, Name = unit.Name, Type = unit.Type, Attributes = new(unit.Attributes, StringComparer.Ordinal) };
        linked.Attributes["sd"] = (unit.Attributes.GetValueOrDefault("sd") ?? ["1,en"])
            .Where(raw => ScheduleCalculator.RuleValue(unit, "ln", ScheduleCalculator.SplitRule(raw).Rule, ScheduleCalculator.SplitRule(raw).Rule.ToString(CultureInfo.InvariantCulture)) == parent.Point.Rule.ToString(CultureInfo.InvariantCulture)).ToList();
        if (linked.Attributes["sd"].Count == 0)
        {
            return new(parent.Point, false, "RULE_NOT_LINKED");
        }

        bool hasCandidate = false;
        SchedulePoint? selected = null;
        foreach (var point in ScheduleCalculator.CalculateRaw(linked, all, registration, parent.Point.BusinessDate, parent.Point.BusinessDate.AddDays(1), options, token))
        {
            hasCandidate = true;

            // 基準時刻前の不適合ルールで、同日時の有効な別ルールを消さないよう先に除きます。
            if (ScheduleCalculator.IsNestedTimeEligible(unit, all, point.Rule)
                && (selected is null || point.Due < selected.Due || (point.Due == selected.Due && point.Rule < selected.Rule)))
            {
                selected = point;
            }
        }

        return selected is not null ? new(selected, true, "SELECTED") : new(parent.Point, false, hasCandidate ? "BUSINESS_TIME_MISMATCH" : "NO_SAME_BUSINESS_DATE");
    }
}
