using System.Globalization;
using System.Text.RegularExpressions;
using JobRunner.Models;

namespace JobRunner.Scheduling;

/// <summary>
/// 固定された登録基準日から予定を純粋計算します。保存やプロセス起動は行いません。
/// </summary>
public static class ScheduleCalculator
{
    /// <summary>DayOfWeekの数値順に対応するJP1の曜日表記です。</summary>
    private static readonly string[] Weekdays = ["su", "mo", "tu", "we", "th", "fr", "sa"];

    /// <summary>
    /// 指定範囲の業務日を評価し、定義上の時刻をUTCへ変換します。
    /// </summary>
    /// <param name="root">対象のルートネットです。</param>
    /// <param name="all">参照カレンダーを含む全ユニットです。</param>
    /// <param name="registration">基準日を固定した登録です。</param>
    /// <param name="from">生成対象業務日の下限です。</param>
    /// <param name="until">生成対象業務日の排他的上限です。</param>
    /// <param name="options">時間制とタイムゾーンです。</param>
    /// <param name="cancellationToken">呼出元による計算中断です。</param>
    /// <returns>ルール番号・業務日・実日時を分離した予定です。</returns>
    public static IEnumerable<SchedulePoint> Calculate(UnitDefinition root, IReadOnlyDictionary<string, UnitDefinition> all, Registration registration, DateOnly from, DateOnly until, RunnerOptions options, CancellationToken cancellationToken = default)
        => CalculateRaw(root, all, registration, from, until, options, cancellationToken).GroupBy(p => p.Due).Select(g => g.OrderBy(p => p.Rule).First());

    /// <summary>曜日と日付のカレンダー指定を上位から継承して評価します。</summary>
    /// <param name="date">評価する業務日です。</param>
    /// <param name="unit">カレンダーを適用するユニットです。</param>
    /// <param name="all">参照と親グループを解決する辞書です。</param>
    /// <returns>稼働日ならtrueです。明示日付は曜日指定より優先します。</returns>
    public static bool IsWorking(DateOnly date, UnitDefinition unit, IReadOnlyDictionary<string, UnitDefinition> all) => EvaluateCalendar(date, unit, all).Working;

    /// <summary>実行と画面で同じ評価を行い、継承元も返します。</summary>
    /// <param name="date">業務日です。</param>
    /// <param name="unit">参照を持つネットまたはカレンダーです。</param>
    /// <param name="all">定義の参照先です。</param>
    /// <returns>実効値と採用した指定の出所です。</returns>
    public static CalendarEvaluation EvaluateCalendar(DateOnly date, UnitDefinition unit, IReadOnlyDictionary<string, UnitDefinition> all)
    {
        string reference = unit.Get("jc");
        if (reference.Length > 0)
        {
            unit = all[reference];
        }

        while (true)
        {
            var open = unit.Attributes.GetValueOrDefault("op") ?? [];
            var closed = unit.Attributes.GetValueOrDefault("cl") ?? [];
            string day = date.ToString("yyyy/M/d", CultureInfo.InvariantCulture);
            if (open.Any(v => DateMatches(v, day)))
            {
                return new(true, unit.Path, "op日付");
            }

            if (closed.Any(v => DateMatches(v, day)))
            {
                return new(false, unit.Path, "cl日付");
            }

            string weekday = Weekdays[(int)date.DayOfWeek];
            if (open.Contains(weekday))
            {
                return new(true, unit.Path, "op曜日");
            }

            if (closed.Contains(weekday))
            {
                return new(false, unit.Path, "cl曜日");
            }

            if (!all.TryGetValue(Parent(unit.Path), out var parent))
            {
                return new(true, null, "既定値");
            }

            unit = parent;
        }
    }

    /// <summary>属性に付いたルール番号を切り分け、省略時は第1ルールへ対応付けます。</summary>
    /// <param name="value">番号と値を含む属性文字列です。</param>
    /// <returns>ルール番号と残りの値です。</returns>
    public static (int Rule, string Value) SplitRule(string value)
    {
        int comma = value.IndexOf(',');
        return comma > 0 && int.TryParse(value[..comma], out int rule) ? (rule, value[(comma + 1)..].Trim()) : (1, value);
    }

    /// <summary>47時までの日跨ぎ時刻を分へ変換し、日付情報を維持します。</summary>
    /// <param name="value">hh:mm形式の定義時刻です。</param>
    /// <returns>0から2879までの分数です。</returns>
    public static int ParseTime(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int hour) || !int.TryParse(parts[1], out int minute) || hour is < 0 or > 47 || minute is < 0 or > 59)
        {
            throw new FormatException($"不正な定義時刻: {value}");
        }

        return (hour * 60) + minute;
    }

    /// <summary>論理完全名の親を取得し、上位カレンダー探索に使用します。</summary>
    /// <param name="path">ユニット完全名です。</param>
    /// <returns>親の完全名。最上位ならルートです。</returns>
    public static string Parent(string path) => path[..path.LastIndexOf('/')] is { Length: > 0 } parent ? parent : "/";

    /// <summary>ルール番号に対応する属性を選び、別ルールの値を流用しません。</summary>
    /// <param name="unit">予定を持つユニットです。</param>
    /// <param name="key">スケジュール属性名です。</param>
    /// <param name="rule">対象ルール番号です。</param>
    /// <param name="fallback">省略時の仕様上の値です。</param>
    /// <returns>対応する属性値です。</returns>
    public static string RuleValue(UnitDefinition unit, string key, int rule, string fallback) =>
        unit.Attributes.GetValueOrDefault(key)?.Select(SplitRule).Where(v => v.Rule == rule).Select(v => v.Value).FirstOrDefault() ?? fallback;

    /// <summary>ネストの絶対時刻が基準時刻前の場合は、上位とは異なる業務日として除外します。</summary>
    /// <param name="unit">対象のネストです。</param>
    /// <param name="all">参照カレンダーを含む定義です。</param>
    /// <param name="rule">評価するルールです。</param>
    /// <returns>同じ業務日の候補として扱える場合trueです。</returns>
    public static bool IsNestedTimeEligible(UnitDefinition unit, IReadOnlyDictionary<string, UnitDefinition> all, int rule)
    {
        string time = RuleValue(unit, "st", rule, "+00:00");
        return time.StartsWith('+') || ParseTime(time) >= ParseTime(Inherited(unit, all, "stt", "00:00"));
    }

    /// <summary>重複の優先順位を適用する前のルール別候補を計算します。</summary>
    /// <param name="root">対象ネットです。</param>
    /// <param name="all">全定義です。</param>
    /// <param name="registration">登録基準です。</param>
    /// <param name="from">開始業務日です。</param>
    /// <param name="until">終了業務日です。</param>
    /// <param name="options">時間帯設定です。</param>
    /// <param name="cancellationToken">呼出元による計算中断です。</param>
    /// <returns>ルール別の候補です。</returns>
    internal static IEnumerable<SchedulePoint> CalculateRaw(UnitDefinition root, IReadOnlyDictionary<string, UnitDefinition> all, Registration registration, DateOnly from, DateOnly until, RunnerOptions options, CancellationToken cancellationToken)
    {
        if (until.DayNumber - from.DayNumber > 3700)
        {
            throw new FormatException("一度の計算範囲は3700日以下にしてください。");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 一度の列挙だけに閉じたキャッシュです。別の定義版・登録へ状態を持ち越しません。
        var workingDays = new Dictionary<DateOnly, bool>();
        var months = new Dictionary<DateOnly, (DateOnly Start, DateOnly End)>();
        var prefix = new Dictionary<int, int> { [from.DayNumber] = 0 };
        int first = from.DayNumber, last = first;
        bool Working(DateOnly day)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!workingDays.TryGetValue(day, out bool result))
            {
                result = IsWorking(day, root, all);
                workingDays.Add(day, result);
            }

            return result;
        }

        (DateOnly Start, DateOnly End) Period(DateOnly month)
        {
            if (!months.TryGetValue(month, out var result))
            {
                result = CalendarMonth(month, root, all);
                months.Add(month, result);
            }

            return result;
        }

        int CountWorking(DateOnly start, DateOnly end, bool working)
        {
            while (first > start.DayNumber)
            {
                prefix[first - 1] = prefix[first] - (Working(DateOnly.FromDayNumber(first - 1)) ? 1 : 0);
                first--;
            }

            while (last < end.DayNumber)
            {
                prefix[last + 1] = prefix[last] + (Working(DateOnly.FromDayNumber(last)) ? 1 : 0);
                last++;
            }

            int count = prefix[end.DayNumber] - prefix[start.DayNumber];
            return working ? count : end.DayNumber - start.DayNumber - count;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
        foreach (string value in root.Attributes.GetValueOrDefault("sd") ?? ["1,en"])
        {
            var (rule, dateSpec) = SplitRule(value);
            if (dateSpec == "ud")
            {
                continue;
            }

            string timeSpec = RuleValue(root, "st", rule, "+00:00");
            string cycle = RuleValue(root, "cy", rule, string.Empty);
            string shift = RuleValue(root, "sh", rule, "ca");
            int shiftDays = int.Parse(RuleValue(root, "shd", rule, "2"), CultureInfo.InvariantCulture);
            for (var day = from.AddDays(-93); day < until.AddDays(93); day = day.AddDays(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Matches(day, dateSpec, cycle, registration.Anchor, Working, Period, CountWorking))
                {
                    continue;
                }

                DateOnly? business = Shift(day, shift, shiftDays, Working);
                business = business is null ? null : Origin(business.Value, RuleValue(root, "cftd", rule, "no"), Working);
                if (business is null || business < from || business >= until || (registration.End is not null && business >= registration.End))
                {
                    continue;
                }

                if (root.Get("ed") is { Length: > 0 } end && business > DateOnly.ParseExact(end, "yyyy/M/d", CultureInfo.InvariantCulture))
                {
                    continue;
                }

                int minutes = ParseTime(timeSpec.TrimStart('+'));
                int baseline = ParseTime(Inherited(root, all, "stt", "00:00"));
                int actual = timeSpec.StartsWith('+') ? baseline + minutes : minutes;
                if (!timeSpec.StartsWith('+') && actual < baseline)
                {
                    actual += 1440;
                }

                var local = business.Value.ToDateTime(TimeOnly.MinValue).AddMinutes(actual);
                if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
                {
                    throw new FormatException("夏時間の欠落・重複時刻には明示的な運用方針が必要です。");
                }

                yield return new SchedulePoint(rule, business.Value, new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone)));
            }
        }
    }

    /// <summary>原文のカレンダー日付を桁揃えの違いに依存せず照合します。</summary>
    /// <param name="value">曜日または年月日の属性値です。</param>
    /// <param name="day">比較する年月日です。</param>
    /// <returns>年月日が一致する場合だけtrueです。</returns>
    private static bool DateMatches(string value, string day) => DateOnly.TryParseExact(value, "yyyy/M/d", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && parsed.ToString("yyyy/M/d", CultureInfo.InvariantCulture) == day;

    /// <summary>親に定義された日付切り替え時刻などを探索します。</summary>
    /// <param name="unit">探索開始ユニットです。</param>
    /// <param name="all">全ユニットの辞書です。</param>
    /// <param name="key">継承する属性名です。</param>
    /// <param name="fallback">上位にも定義がない場合の値です。</param>
    /// <returns>直近の定義値です。</returns>
    private static string Inherited(UnitDefinition unit, IReadOnlyDictionary<string, UnitDefinition> all, string key, string fallback)
    {
        if (unit.Get("jc") is { Length: > 0 } reference)
        {
            unit = all[reference];
        }

        while (!unit.Attributes.ContainsKey(key))
        {
            if (!all.TryGetValue(Parent(unit.Path), out var parent))
            {
                return fallback;
            }

            unit = parent;
        }

        return unit.Get(key);
    }

    /// <summary>休業日の振り替えを猶予日数以内で検索します。</summary>
    /// <param name="date">当初の業務日です。</param>
    /// <param name="shift">be、af、ca、noの指定です。</param>
    /// <param name="limit">振り替えの最大日数です。</param>
    /// <param name="working">対象カレンダーの稼働日判定です。</param>
    /// <returns>実行業務日。猶予内にない場合はnullです。</returns>
    private static DateOnly? Shift(DateOnly date, string shift, int limit, Func<DateOnly, bool> working)
    {
        if (shift == "no" || working(date))
        {
            return date;
        }

        if (shift == "ca")
        {
            return null;
        }

        for (int i = 1; i <= limit; i++)
        {
            var candidate = date.AddDays(shift == "be" ? -i : i);
            if (working(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>基準日と月区分から業務月の両端を求めます。</summary>
    /// <param name="month">定義で指定する月です。</param>
    /// <param name="unit">対象ネットです。</param>
    /// <param name="all">継承先を含む定義です。</param>
    /// <returns>開始日と翌月開始日です。</returns>
    private static (DateOnly Start, DateOnly End) CalendarMonth(DateOnly month, UnitDefinition unit, IReadOnlyDictionary<string, UnitDefinition> all)
    {
        if (unit.Get("jc") is { Length: > 0 } reference)
        {
            unit = all[reference];
        }

        if (Inherited(unit, all, "md", "th") == "ne")
        {
            month = month.AddMonths(-1);
        }

        string baseline = Inherited(unit, all, "sdd", "1");
        DateOnly BaseDay(DateOnly value)
        {
            if (int.TryParse(baseline, out int day))
            {
                return value.AddDays(day - 1);
            }

            var parts = baseline.Split(':');
            int weekday = Array.IndexOf(Weekdays, parts[0]);
            return value.AddDays(((weekday - (int)value.DayOfWeek + 7) % 7) + (7 * (int.Parse(parts[1], CultureInfo.InvariantCulture) - 1)));
        }

        return (BaseDay(month), BaseDay(month.AddMonths(1)));
    }

    /// <summary>サイクルと振替の後に起算日数を適用します。</summary>
    /// <param name="date">振替後の起点日です。</param>
    /// <param name="spec">cftdの値です。</param>
    /// <param name="working">対象カレンダーの稼働日判定です。</param>
    /// <returns>起算日または猶予超過のnullです。</returns>
    private static DateOnly? Origin(DateOnly date, string spec, Func<DateOnly, bool> working)
    {
        var parts = spec.Split(',');
        if (parts[0] == "no")
        {
            return date;
        }

        int count = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 1;
        int direction = parts[0] is "be" or "db" ? -1 : 1;
        if (parts[0] is "db" or "da")
        {
            return date.AddDays(count * direction);
        }

        int grace = parts.Length > 2 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 10;
        int limit = grace;
        for (int i = 1; i <= limit; i++)
        {
            var candidate = date.AddDays(i * direction);
            if (working(candidate) && --count == 0)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>開始日の指定と処理サイクルを組み合わせて候補日を判定します。</summary>
    /// <param name="day">判定する候補日です。</param>
    /// <param name="spec">sdのルール本体です。</param>
    /// <param name="cycle">cyのルール本体。空なら一度だけです。</param>
    /// <param name="anchor">登録時の基準日です。</param>
    /// <param name="working">対象カレンダーの稼働日判定です。</param>
    /// <param name="period">基準日と月区分による業務月です。</param>
    /// <param name="countDays">開始を含み終了を含まない稼働・休業日の個数です。</param>
    /// <returns>候補日が開始日とサイクルに一致すればtrueです。</returns>
    private static bool Matches(DateOnly day, string spec, string cycle, DateOnly anchor, Func<DateOnly, bool> working, Func<DateOnly, (DateOnly Start, DateOnly End)> period, Func<DateOnly, DateOnly, bool, int> countDays)
    {
        var parts = spec.Split('/');
        string selector = parts[^1];
        int year = parts.Length == 3 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : anchor.Year;
        int month = parts.Length >= 2 ? int.Parse(parts[^2], CultureInfo.InvariantCulture) : anchor.Month;
        var firstMonth = new DateOnly(year, month, 1);
        DateOnly weekdayAnchor = parts.Length >= 2 ? DateOnly.MinValue : anchor;
        DateOnly? Pick(DateOnly selectedMonth) => Select(selectedMonth, selector == "en" ? anchor.Day.ToString(CultureInfo.InvariantCulture) : selector, weekdayAnchor, working, period);

        // 月・年単位では開始月に指定日がなくても、位相を保って後続月を評価します。
        if (cycle.Length > 0)
        {
            var periodic = Regex.Match(cycle, @"^\((\d+),([ymwd])\)$");
            int interval = int.Parse(periodic.Groups[1].Value, CultureInfo.InvariantCulture);
            char unit = periodic.Groups[2].Value[0];
            if (unit is 'm' or 'y')
            {
                for (int neighboringMonth = -2; neighboringMonth <= 2; neighboringMonth++)
                {
                    var candidateMonth = new DateOnly(day.Year, day.Month, 1).AddMonths(neighboringMonth);
                    int offset = ((candidateMonth.Year - year) * 12) + candidateMonth.Month - month;
                    if (offset >= 0 && offset % (interval * (unit == 'y' ? 12 : 1)) == 0 && (selector != "en" || day >= anchor) && day == Pick(candidateMonth))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        // 年月・週番号を省略した曜日は登録日以降で最も近い曜日です。
        // 登録月内に残っていなくても翌月（年）の曜日から週周期を開始します。
        int nextWeekday = parts.Length == 1 ? Array.IndexOf(Weekdays, selector) : -1;
        var seed = selector == "en" ? anchor : nextWeekday >= 0
            ? anchor.AddDays((nextWeekday - (int)anchor.DayOfWeek + 7) % 7)
            : Pick(firstMonth);
        if (seed is null || day < seed)
        {
            return false;
        }

        if (cycle.Length == 0)
        {
            return day == seed;
        }

        var match = Regex.Match(cycle, @"^\((\d+),([ymwd])\)$");
        int count = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        char kind = match.Groups[2].Value[0];
        int elapsed = day.DayNumber - seed.Value.DayNumber;
        if (kind is 'd' or 'w')
        {
            if (selector.StartsWith('*') || selector.StartsWith('@'))
            {
                bool wantWorking = selector[0] == '*';
                if (working(day) != wantWorking)
                {
                    return false;
                }

                elapsed = countDays(seed.Value, day, wantWorking);
            }

            return elapsed % (count * (kind == 'w' ? 7 : 1)) == 0;
        }

        int months = ((day.Year - year) * 12) + day.Month - month;
        if (months < 0 || months % (count * (kind == 'y' ? 12 : 1)) != 0)
        {
            return false;
        }

        return day == Pick(new DateOnly(day.Year, day.Month, 1));
    }

    /// <summary>月内の暦日・末日・曜日・運用日番号を解決します。</summary>
    /// <param name="month">対象月の初日です。</param>
    /// <param name="selector">sdの年月を除いた指定です。</param>
    /// <param name="anchor">番号省略の曜日選択に使う登録日です。</param>
    /// <param name="working">稼働日を返すカレンダー関数です。</param>
    /// <param name="period">基準日と月区分による業務月です。</param>
    /// <returns>存在する候補日。該当しなければnullです。</returns>
    private static DateOnly? Select(DateOnly month, string selector, DateOnly anchor, Func<DateOnly, bool> working, Func<DateOnly, (DateOnly Start, DateOnly End)> period)
    {
        var (begin, end) = selector.StartsWith('+') || selector.StartsWith('*') || selector.StartsWith('@') ? period(month) : (month, month.AddMonths(1));
        var days = Enumerable.Range(0, end.DayNumber - begin.DayNumber).Select(begin.AddDays).ToList();
        selector = selector.TrimStart('+');
        if (selector.StartsWith('*') || selector.StartsWith('@'))
        {
            bool wantWorking = selector[0] == '*';
            days = days.Where(d => working(d) == wantWorking).ToList();
            selector = selector[1..];
        }

        var weekday = selector.Split(':');
        int index = Array.IndexOf(Weekdays, weekday[0]);
        if (index >= 0)
        {
            var matching = days.Where(d => (int)d.DayOfWeek == index).ToList();
            if (weekday.Length == 1)
            {
                return matching.Cast<DateOnly?>().FirstOrDefault(d => d >= anchor);
            }

            return weekday[1] == "b" ? matching.Cast<DateOnly?>().LastOrDefault() : matching.Cast<DateOnly?>().ElementAtOrDefault(int.Parse(weekday[1], CultureInfo.InvariantCulture) - 1);
        }

        if (selector.StartsWith('b'))
        {
            int back = selector.Length > 1 ? int.Parse(selector[2..], CultureInfo.InvariantCulture) : 0;
            return days.Cast<DateOnly?>().ElementAtOrDefault(days.Count - 1 - back);
        }

        return days.Cast<DateOnly?>().ElementAtOrDefault(int.Parse(selector, CultureInfo.InvariantCulture) - 1);
    }
}
