using System.Globalization;
using System.Text.RegularExpressions;
using JobRunner.Models;

namespace JobRunner.Scheduling;

/// <summary>ルール別の定義監視を、世代に固定する監視設定へ変換します。</summary>
public static class DefinitionDelayMonitor
{
    /// <summary>採用しないルールも含め、有効化前に全設定を検証します。</summary>
    /// <param name="unit">ネット定義です。</param>
    public static void Validate(UnitDefinition unit)
    {
        foreach (string key in new[] { "sy", "ey" })
        {
            var rules = new HashSet<int>();
            foreach (string raw in unit.Attributes.GetValueOrDefault(key) ?? [])
            {
                var (rule, value) = ScheduleCalculator.SplitRule(raw);
                if (rule is < 1 or > 144 || !rules.Add(rule) || value.Length == 0)
                {
                    throw new FormatException($"{unit.Path}: {key}のルール番号が重複または範囲外です。");
                }

                _ = Parse(value);
            }
        }

        if (unit.Attributes.ContainsKey("fd"))
        {
            _ = Duration(unit.Get("fd"));
        }
    }

    /// <summary>別ルールの監視値を流用せず、採用ルールだけを固定します。</summary>
    /// <param name="unit">固定版のネットです。</param>
    /// <param name="rule">採用ルール番号です。</param>
    /// <returns>監視属性がない場合はnullです。</returns>
    public static DelayMonitorSettings? Read(UnitDefinition unit, int rule)
    {
        var start = Parse(ScheduleCalculator.RuleValue(unit, "sy", rule, string.Empty));
        var end = Parse(ScheduleCalculator.RuleValue(unit, "ey", rule, string.Empty));
        int? duration = unit.Attributes.ContainsKey("fd") ? Duration(unit.Get("fd")) : null;
        return start.Mode is null && end.Mode is null && duration is null ? null : new DelayMonitorSettings
        {
            Start = start.Mode,
            StartMinutes = start.Minutes,
            End = end.Mode,
            EndMinutes = end.Minutes,
            Duration = duration.HasValue ? true : null,
            DurationMinutes = duration,
        };
    }

    /// <summary>fdと相対分の整数範囲を確認します。</summary>
    /// <param name="value">10進数の分数です。</param>
    /// <returns>1〜2879分です。</returns>
    private static int Duration(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) && minutes is >= 1 and <= 2879
        ? minutes : throw new FormatException("遅延監視の相対分・所要時間は1〜2879です。");

    /// <summary>sy/eyの絶対時刻とM/U/Cの基準を分離します。</summary>
    /// <param name="value">ルール番号を除いた値です。</param>
    /// <returns>監視方式と分数です。</returns>
    private static (string? Mode, int? Minutes) Parse(string value)
    {
        if (value.Length == 0)
        {
            return (null, null);
        }

        if (value[0] is 'M' or 'U' or 'C')
        {
            return (value[0] switch { 'M' => "ROOT", 'U' => "TOP", _ => "OWN" }, Duration(value[1..]));
        }

        if (!Regex.IsMatch(value, @"^[0-9]{1,2}:[0-9]{1,2}$"))
        {
            throw new FormatException("遅延監視はhh:mmまたはM/U/Cに続く分数です。");
        }

        return ("ABSOLUTE", ScheduleCalculator.ParseTime(value));
    }
}
