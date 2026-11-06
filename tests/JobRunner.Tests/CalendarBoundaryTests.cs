using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;
using Xunit;

namespace JobRunner.Tests;

/// <summary>非0基準時刻・参照・振替と生成復旧を独立した期待日時で検証します。</summary>
public sealed class CalendarBoundaryTests
{
    /// <summary>参照カレンダーを優先し、基準時刻の前後を正しい暦日に写します。</summary>
    /// <param name="mode">時間制です。</param>
    /// <param name="time">定義時刻です。</param>
    /// <param name="expected">期待するUTC日時です。</param>
    [Theory]
    [InlineData(24, "05:59", "2026-11-05T20:59:00Z")]
    [InlineData(24, "06:00", "2026-11-04T21:00:00Z")]
    [InlineData(24, "06:01", "2026-11-04T21:01:00Z")]
    [InlineData(24, "+00:00", "2026-11-04T21:00:00Z")]
    [InlineData(24, "+23:59", "2026-11-05T20:59:00Z")]
    [InlineData(48, "24:00", "2026-11-05T15:00:00Z")]
    [InlineData(48, "47:59", "2026-11-06T14:59:00Z")]
    [InlineData(48, "+47:59", "2026-11-06T20:59:00Z")]
    public void ReferencedBaselinePreservesBusinessDate(int mode, string time, string expected)
    {
        var options = new RunnerOptions { ScheduleTimeMode = mode };
        var units = Jp1DefinitionParser.Parse($"unit=cal;{{ty=g;stt=06:00;unit=sub;{{ty=g;}}}}unit=g;{{ty=g;stt=09:00;unit=n;{{ty=n;jc=/cal/sub;sd=2026/11/5;st={time};sh=no;}}}}", new DefinitionSource());
        DefinitionValidator.Validate(units, options);
        var all = units.Flatten().ToDictionary(u => u.Path);
        var day = new DateOnly(2026, 11, 5);
        var point = Assert.Single(ScheduleCalculator.Calculate(all["/g/n"], all, new Registration { Anchor = day }, day, day.AddDays(1), options));
        Assert.Equal(day, point.BusinessDate);
        Assert.Equal(DateTimeOffset.Parse(expected), point.Due);
    }

    /// <summary>日付例外・年跨ぎ・猶予の包含境界と起算順序を検証します。</summary>
    /// <param name="schedule">ネットの予定属性です。</param>
    /// <param name="expected">振替・起算後の業務日、または候補なしです。</param>
    [Theory]
    [InlineData("sd=2026/12/31;sh=af;shd=1;", "2027-01-01")]
    [InlineData("sd=2027/1/2;sh=be;shd=1;", "2027-01-01")]
    [InlineData("sd=2027/1/2;sh=af;shd=1;", null)]
    [InlineData("sd=2027/1/2;sh=af;shd=2;", "2027-01-04")]
    [InlineData("sd=2026/12/31;sh=af;shd=1;cftd=af,1,3;", "2027-01-04")]
    [InlineData("sd=2026/12/31;sh=af;shd=1;cftd=af,1,2;", null)]
    public void HolidayShiftAndOriginRespectYearBoundary(string schedule, string? expected)
    {
        var units = Jp1DefinitionParser.Parse("unit=cal;{ty=g;cl=sa;cl=su;cl=2026/12/31;op=2027/1/1;}unit=n;{ty=n;jc=/cal;st=10:00;" + schedule + "}", new DefinitionSource());
        DefinitionValidator.Validate(units, new RunnerOptions());
        var all = units.Flatten().ToDictionary(u => u.Path);
        var points = ScheduleCalculator.Calculate(all["/n"], all, new Registration { Anchor = new(2026, 12, 1) }, new(2026, 12, 31), new(2027, 1, 5), new RunnerOptions()).ToList();
        if (expected is null)
        {
            Assert.Empty(points);
        }
        else
        {
            Assert.Equal(DateOnly.Parse(expected), Assert.Single(points).BusinessDate);
        }
    }

    /// <summary>業務月と暦月を混ぜず、参照先の基準日・月区分を継承します。</summary>
    /// <param name="classification">当月/翌月の区分です。</param>
    /// <param name="expected">業務月最終日の暦日です。</param>
    [Theory]
    [InlineData("th", "2024-03-19")]
    [InlineData("ne", "2024-02-19")]
    public void ReferencedBusinessMonthEndsBeforeNextBaseDay(string classification, string expected)
    {
        var units = Jp1DefinitionParser.Parse($"unit=cal;{{ty=g;sdd=20;md={classification};unit=sub;{{ty=g;}}}}unit=n;{{ty=n;jc=/cal/sub;sd=2024/2/+b;st=10:00;sh=no;}}", new DefinitionSource());
        DefinitionValidator.Validate(units, new RunnerOptions());
        var all = units.Flatten().ToDictionary(u => u.Path);
        var point = Assert.Single(ScheduleCalculator.Calculate(all["/n"], all, new Registration { Anchor = new(2024, 1, 1) }, new(2024, 1, 1), new(2024, 4, 1), new RunnerOptions()));
        Assert.Equal(DateOnly.Parse(expected), point.BusinessDate);
    }

    /// <summary>推測による欠落月の受理や、24時間制の拡張をしません。</summary>
    /// <param name="definition">未対応条件を含む定義です。</param>
    [Theory]
    [InlineData("unit=g;{ty=g;sdd=29;}")]
    [InlineData("unit=g;{ty=g;sdd=30;}")]
    [InlineData("unit=g;{ty=g;sdd=su:5;}")]
    [InlineData("unit=n;{ty=n;st=24:00;}")]
    public void KeepsUnverifiedBoundariesRejected(string definition)
    {
        var units = Jp1DefinitionParser.Parse(definition, new DefinitionSource());
        Assert.Throws<FormatException>(() => DefinitionValidator.Validate(units, new RunnerOptions()));
    }

    /// <summary>夏時間の欠落と二重化を、勝手な補正で実行日時へ変換しません。</summary>
    /// <param name="date">切替日です。</param>
    /// <param name="time">曖昧または存在しない時刻です。</param>
    [Theory]
    [InlineData("2026/3/8", "02:30")]
    [InlineData("2026/11/1", "01:30")]
    public void RejectsInvalidOrAmbiguousServiceTime(string date, string time)
    {
        var units = Jp1DefinitionParser.Parse($"unit=n;{{ty=n;sd={date};st={time};sh=no;}}", new DefinitionSource());
        var options = new RunnerOptions { TimeZone = "America/New_York" };
        DefinitionValidator.Validate(units, options);
        Assert.Throws<FormatException>(() => ScheduleCalculator.Calculate(units[0], units.ToDictionary(u => u.Path), new Registration { Anchor = new(2026, 1, 1) }, new(2026, 1, 1), new(2027, 1, 1), options).ToList());
    }

    /// <summary>同時刻ルールを一世代にし、長期停止後も過去の実行を補いません。</summary>
    /// <param name="future">件数方式または日数方式です。</param>
    /// <returns>監視ルール・世代ID・試行数の一貫性を確認します。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task OverlappingRulesRemainUniqueAcrossLongOutage(int? future)
    {
        await using var s = new EngineScenario();
        s.Scope.Options.ScheduleTimeMode = 48;
        s.Scope.Options.ConfirmedFutureGenerations = future;
        await s.StartAsync("unit=g;{ty=g;stt=06:00;unit=n;{ty=n;sd=1,2026/11/5;sd=2,2026/11/5;cy=1,(1,d);cy=2,(1,d);st=1,29:59;st=2,05:59;sh=1,no;sh=2,no;sy=1,C5;sy=2,C99;unit=j;{ty=j;te=\"true\";}}}");
        var roots = (await s.OccurrencesAsync()).Where(o => o.Id == o.RootId).ToList();
        Assert.Equal(roots.Count, roots.Select(o => o.Due).Distinct().Count());
        Assert.All(roots, o =>
        {
            Assert.Equal(1, o.Rule);
            Assert.Equal(5, o.DelayMonitor!.StartMinutes);
        });
        s.Clock.Now = s.Clock.Now.AddDays(10);
        await s.RestartAsync();
        await s.Engine.TickAsync(CancellationToken.None);
        Assert.Empty(await s.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
        var next = (await s.OccurrencesAsync()).Where(o => o.Id == o.RootId && o.State == "Waiting").OrderBy(o => o.Due).First();
        s.Clock.Now = next.Due;
        await s.AdvanceUntilAsync(o => o.Single(v => v.Id == next.Id).State == "Succeeded");
        Assert.Single(await s.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
    }
}
