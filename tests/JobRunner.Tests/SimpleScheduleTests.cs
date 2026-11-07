using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;
using Xunit;

namespace JobRunner.Tests;

/// <summary>V12 5.2.3–5.2.4の簡単設定が使う標準定義を検証します。</summary>
public sealed class SimpleScheduleTests
{
    /// <summary>年月・週番号省略時は月末・年末・同じ曜日でも近い曜日から継続します。</summary>
    /// <param name="anchor">登録基準日です。</param>
    /// <param name="first">最初の候補日です。</param>
    /// <param name="second">次の候補日です。</param>
    [Theory]
    [InlineData("2026-11-30", "2026-12-04", "2026-12-11")]
    [InlineData("2026-12-31", "2027-01-01", "2027-01-08")]
    [InlineData("2026-11-27", "2026-11-27", "2026-12-04")]
    public void WeeklyWeekdayContinuesAcrossRegistrationMonth(string anchor, string first, string second)
    {
        var points = Points("sd=fr;cy=(1,w);sh=no;", DateOnly.Parse(anchor), DateOnly.Parse(first), DateOnly.Parse(second).AddDays(1));
        Assert.Equal(new[] { first, second }, points.Select(p => p.BusinessDate.ToString("yyyy-MM-dd")));
    }

    /// <summary>明示した月・週番号は登録日からの次曜日へ変換しません。</summary>
    [Fact]
    public void ExplicitMonthAndOrdinalKeepTheirPhase()
    {
        var points = Points("sd=2026/11/fr:1;cy=(2,w);sh=no;", new(2026, 11, 30), new(2026, 12, 1), new(2026, 12, 20));
        Assert.Equal(new[] { new DateOnly(2026, 12, 4), new DateOnly(2026, 12, 18) }, points.Select(p => p.BusinessDate));
    }

    /// <summary>起点を除外し、カレンダーの連休を飛ばして前後の運用日を数えます。</summary>
    /// <param name="offset">起算指定です。</param>
    /// <param name="expected">期待する業務日です。</param>
    [Theory]
    [InlineData("af,2,10", "2026-11-30")]
    [InlineData("be,5,10", "2026-11-17")]
    public void MonthlyTwentyFifthCountsOperatingDays(string offset, string expected)
    {
        var points = Points($"sd=25;cy=(1,m);sh=no;cftd={offset};", new(2026, 11, 1), new(2026, 11, 1), new(2026, 12, 1), "cl=2026/11/23;cl=2026/11/26;");
        Assert.Equal(DateOnly.Parse(expected), Assert.Single(points).BusinessDate);
    }

    /// <summary>休業日起点の振替と起算の順序、猶予の終端を区別します。</summary>
    /// <param name="shift">休業日の振替方法です。</param>
    /// <param name="offset">起算指定です。</param>
    /// <param name="expected">候補日または候補なしです。</param>
    [Theory]
    [InlineData("no", "af,2,3", "2026-10-27")]
    [InlineData("af", "af,2,3", "2026-10-28")]
    [InlineData("no", "af,2,1", null)]
    [InlineData("no", "be,1,2", "2026-10-23")]
    public void HolidayOriginShiftAndGrace(string shift, string offset, string? expected)
    {
        var points = Points($"sd=25;cy=(1,m);sh={shift};shd=2;cftd={offset};", new(2026, 10, 1), new(2026, 10, 1), new(2026, 11, 1));
        if (expected is null)
        {
            Assert.Empty(points);
        }
        else
        {
            Assert.Equal(DateOnly.Parse(expected), Assert.Single(points).BusinessDate);
        }
    }

    /// <summary>起算は年境界を越え、31日が存在しない月を月末へ丸めません。</summary>
    [Fact]
    public void MonthlyMissingDaysAndOffsetsKeepCalendarMeaning()
    {
        var points = Points("sd=31;cy=(1,m);sh=no;cftd=af,2,10;", new(2026, 11, 1), new(2026, 11, 1), new(2027, 2, 1));
        Assert.Equal(new DateOnly(2027, 1, 4), Assert.Single(points).BusinessDate);
        points = Points("sd=29;cy=(1,m);sh=no;", new(2028, 2, 1), new(2028, 2, 1), new(2028, 3, 1));
        Assert.Equal(new DateOnly(2028, 2, 29), Assert.Single(points).BusinessDate);
    }

    /// <summary>毎運用日は業務月基準日ではなく登録日からの日周期に従います。</summary>
    [Fact]
    public void DailyOperatingDaysUseReferencedCalendarAndBusinessTime()
    {
        var units = Jp1DefinitionParser.Parse("unit=c;{ty=g;sdd=20;md=ne;stt=06:00;cl=sa;cl=su;cl=2026/12/1;}unit=n;{ty=n;jc=/c;sd=en;cy=(1,d);sh=ca;st=25:00;}", new DefinitionSource());
        var options = new RunnerOptions { TimeZone = "UTC", ScheduleTimeMode = 48 };
        DefinitionValidator.Validate(units, options);
        var all = units.Flatten().ToDictionary(u => u.Path);
        var points = ScheduleCalculator.Calculate(all["/n"], all, new Registration { Anchor = new(2026, 11, 30) }, new(2026, 11, 30), new(2026, 12, 3), options).ToArray();
        Assert.Equal(new[] { new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 2) }, points.Select(p => p.BusinessDate));
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 1, 0, 0, TimeSpan.Zero), points[0].Due);
        Assert.Throws<FormatException>(() => DefinitionValidator.Validate(units, new RunnerOptions { ScheduleTimeMode = 24 }));
    }

    /// <summary>第N・最終運用日は基準日20日の業務月、暦日25日は暦月に従います。</summary>
    /// <param name="selector">開始日の指定です。</param>
    /// <param name="expected">候補となる暦日です。</param>
    [Theory]
    [InlineData("*2", "2026-11-23")]
    [InlineData("*b", "2026-12-18")]
    [InlineData("25", "2026-11-25")]
    public void MonthlyWorkingSelectorsRespectBusinessMonth(string selector, string expected)
    {
        var points = Points($"sd=2026/11/{selector};sh=no;", new(2026, 10, 1), new(2026, 11, 1), new(2026, 12, 20), "sdd=20;md=th;");
        Assert.Equal(DateOnly.Parse(expected), Assert.Single(points).BusinessDate);
    }

    /// <summary>公式属性だけを解析・検証しUTCの業務日候補を返します。</summary>
    private static SchedulePoint[] Points(string schedule, DateOnly anchor, DateOnly from, DateOnly until, string calendar = "")
    {
        var units = Jp1DefinitionParser.Parse($"unit=g;{{ty=g;cl=sa;cl=su;{calendar}unit=n;{{ty=n;st=09:00;{schedule}}}}}", new DefinitionSource());
        var options = new RunnerOptions { TimeZone = "UTC" };
        DefinitionValidator.Validate(units, options);
        var all = units.Flatten().ToDictionary(u => u.Path);
        return ScheduleCalculator.Calculate(all["/g/n"], all, new Registration { Anchor = anchor }, from, until, options).ToArray();
    }
}
