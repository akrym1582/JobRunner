using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;
using Xunit;

namespace JobRunner.Tests;

/// <summary>業務日とUTCを分離し、月末や48時間制の境界で予定が欠落しないことを検証します。</summary>
public sealed class ScheduleTests
{
    /// <summary>計画16.6の例と、47時59分までの日跨ぎを確認します。</summary>
    /// <param name="time">定義上の開始時刻です。</param>
    /// <param name="expected">独立した期待UTC時刻です。</param>
    [Theory]
    [InlineData("23:59", "2026-11-05T14:59:00Z")]
    [InlineData("24:00", "2026-11-05T15:00:00Z")]
    [InlineData("25:00", "2026-11-05T16:00:00Z")]
    [InlineData("47:59", "2026-11-06T14:59:00Z")]
    public void PreservesBusinessDateBeyondMidnight(string time, string expected)
    {
        var units = Jp1DefinitionParser.Parse($"unit=net;{{ty=n;sd=2026/11/5;st={time};sh=no;}}", new DefinitionSource());
        var anchor = new DateOnly(2026, 11, 5);
        var result = ScheduleCalculator.Calculate(units[0], units.ToDictionary(u => u.Path), new Registration { Anchor = anchor }, anchor, anchor.AddDays(1), new RunnerOptions { ScheduleTimeMode = 48 }).ToList();
        var point = Assert.Single(result);
        Assert.Equal(anchor, point.BusinessDate);
        Assert.Equal(DateTimeOffset.Parse(expected), point.Due);
    }

    /// <summary>48時間制の前日業務日でも実日時が未来なら候補から落とさないことを確認します。</summary>
    [Fact]
    public void PreviousBusinessDayCanStillBeInFuture()
    {
        var units = Jp1DefinitionParser.Parse("unit=net;{ty=n;sd=2026/11/5;st=25:00;sh=no;}", new DefinitionSource());
        var result = ScheduleCalculator.Calculate(units[0], units.ToDictionary(u => u.Path), new Registration { Anchor = new(2026, 11, 6) }, new(2026, 11, 5), new(2026, 11, 7), new RunnerOptions { ScheduleTimeMode = 48 });
        var point = Assert.Single(result);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 16, 0, 0, TimeSpan.Zero), point.Due);
    }

    /// <summary>うるう年の月末を固定日数の加算で計算しないことを確認します。</summary>
    [Fact]
    public void MonthlyLastDayIncludesLeapDay()
    {
        var units = Jp1DefinitionParser.Parse("unit=net;{ty=n;sd=2024/1/b;st=12:00;cy=(1,m);sh=no;}", new DefinitionSource());
        var result = ScheduleCalculator.Calculate(units[0], units.ToDictionary(u => u.Path), new Registration { Anchor = new(2024, 1, 1) }, new(2024, 1, 1), new(2025, 1, 1), new RunnerOptions()).ToList();
        Assert.Equal(12, result.Count);
        Assert.Equal(new DateOnly(2024, 2, 29), result[1].BusinessDate);
        Assert.Equal(new DateOnly(2024, 12, 31), result[^1].BusinessDate);
    }

    /// <summary>稼働日例外が週末の休業指定より優先することを確認します。</summary>
    [Fact]
    public void ExplicitOpenDateOverridesWeekend()
    {
        var units = Jp1DefinitionParser.Parse("unit=g;{ty=g;cl=sa;cl=su;op=2026/11/7;unit=n;{ty=n;}}", new DefinitionSource());
        var all = units.Flatten().ToDictionary(u => u.Path);
        Assert.True(ScheduleCalculator.IsWorking(new(2026, 11, 7), all["/g/n"], all));
        Assert.False(ScheduleCalculator.IsWorking(new(2026, 11, 8), all["/g/n"], all));
    }

    /// <summary>1000個の末端ジョブを想定した1年分の毎日予定が決定的に生成されることを確認します。</summary>
    [Fact]
    public void CalculatesOneThousandDailyJobsForOneYear()
    {
        var units = Jp1DefinitionParser.Parse("unit=net;{ty=n;sd=2026/1/1;st=12:00;cy=(1,d);sh=no;}", new DefinitionSource());
        var all = units.ToDictionary(u => u.Path);
        int count = 0;
        for (int i = 0; i < 1000; i++)
        {
            count += ScheduleCalculator.Calculate(units[0], all, new Registration { Anchor = new(2026, 1, 1) }, new(2026, 1, 1), new(2027, 1, 1), new RunnerOptions()).Count();
        }

        Assert.Equal(365000, count);
    }

    /// <summary>古い基準日の稼働・休業日サイクルを独立した逐日カウントと照合します。</summary>
    /// <param name="selector">稼働日または休業日です。</param>
    /// <param name="cycle">日または週サイクルです。</param>
    /// <param name="interval">期待する対象日数です。</param>
    [Theory]
    [InlineData("*1", "(3,d)", 3)]
    [InlineData("@1", "(2,w)", 14)]
    public void OldAnchorCyclesMatchIndependentDayCount(string selector, string cycle, int interval)
    {
        var units = Jp1DefinitionParser.Parse($"unit=g;{{ty=g;cl=sa;cl=su;op=2026/1/3;cl=2026/1/5;unit=n;{{ty=n;sd=1,{selector};cy=1,{cycle};sh=1,no;st=1,12:00;}}}}", new DefinitionSource());
        var all = units.Flatten().ToDictionary(u => u.Path);
        var root = all["/g/n"];
        var anchor = new DateOnly(1994, 1, 1);
        var from = new DateOnly(2026, 1, 1);
        var until = from.AddDays(366);
        bool Wanted(DateOnly day)
        {
            bool working = day == new DateOnly(2026, 1, 3) || (day != new DateOnly(2026, 1, 5) && day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
            return selector[0] == '*' ? working : !working;
        }

        var expected = new List<DateOnly>();
        int count = 0;
        for (var day = anchor; day < until; day = day.AddDays(1))
        {
            if (Wanted(day))
            {
                if (day >= from && count % interval == 0)
                {
                    expected.Add(day);
                }

                count++;
            }
        }

        var actual = ScheduleCalculator.Calculate(root, all, new Registration { Anchor = anchor }, from, until, new RunnerOptions()).ToList();
        Assert.Equal(expected, actual.Select(p => p.BusinessDate));
    }

    /// <summary>同じ列挙内の別位相と、次の計算のカレンダー変更を混同しません。</summary>
    [Fact]
    public void CalendarMemoIsLocalAndKeepsRulePriority()
    {
        var units = Jp1DefinitionParser.Parse("unit=g;{ty=g;cl=su;unit=n;{ty=n;sd=9,1994/1/*1;cy=9,(1,d);sd=2,2026/2/*1;cy=2,(1,d);st=9,12:00;st=2,12:00;}}", new DefinitionSource());
        var all = units.Flatten().ToDictionary(u => u.Path);
        var root = all["/g/n"];
        List<SchedulePoint> Run() => ScheduleCalculator.Calculate(root, all, new Registration { Anchor = new(2026, 1, 1) }, new(2026, 1, 1), new(2026, 3, 1), new RunnerOptions()).OrderBy(p => p.Due).ToList();
        var before = Run();
        Assert.All(before.Where(p => p.BusinessDate.Month == 1), p => Assert.Equal(9, p.Rule));
        Assert.All(before.Where(p => p.BusinessDate.Month == 2), p => Assert.Equal(2, p.Rule));
        all["/g"].Attributes["cl"].Add("sa");
        Assert.Equal(before.Where(p => p.BusinessDate.DayOfWeek != DayOfWeek.Saturday), Run());
    }

    /// <summary>結果の集約前でも呼出元から中断できます。</summary>
    [Fact]
    public void CanceledCalculationDoesNotEnumerateRules()
    {
        var units = Jp1DefinitionParser.Parse("unit=n;{ty=n;sd=*1;cy=(1,d);}", new DefinitionSource());
        Assert.Throws<OperationCanceledException>(() => ScheduleCalculator.Calculate(units[0], units.ToDictionary(u => u.Path), new Registration { Anchor = new(1994, 1, 1) }, new(2026, 1, 1), new(2027, 1, 1), new RunnerOptions(), new CancellationToken(true)).ToList());
    }
}
