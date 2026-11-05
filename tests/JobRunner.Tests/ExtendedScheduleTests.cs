using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;
using Xunit;

namespace JobRunner.Tests;

/// <summary>業務月と起算予定を暦月境界で検証します。</summary>
public sealed class ExtendedScheduleTests
{
    /// <summary>基準日と月区分を継承し、翌月区分では前月の基準日から数えます。</summary>
    /// <param name="classification">当月または翌月区分です。</param>
    /// <param name="date">期待する暦日です。</param>
    [Theory]
    [InlineData("th", "2026-11-22")]
    [InlineData("ne", "2026-10-22")]
    public void RelativeDayUsesInheritedBusinessMonth(string classification, string date)
    {
        var units = Jp1DefinitionParser.Parse($"unit=g;{{ty=g;sdd=20;md={classification};unit=n;{{ty=n;sd=2026/11/+3;st=10:00;sh=no;}}}}", new DefinitionSource());
        DefinitionValidator.Validate(units, new RunnerOptions());
        var all = units.Flatten().ToDictionary(u => u.Path);
        var point = Assert.Single(ScheduleCalculator.Calculate(all["/g/n"], all, new Registration { Anchor = new(2026, 10, 1) }, new(2026, 10, 1), new(2026, 12, 1), new RunnerOptions()));
        Assert.Equal(DateOnly.Parse(date), point.BusinessDate);
    }

    /// <summary>休業日振替後の起点日へ起算を適用し、生成範囲外の起点も取りこぼしません。</summary>
    [Fact]
    public void OriginFollowsHolidayShiftAcrossMonthBoundary()
    {
        var units = Jp1DefinitionParser.Parse("unit=g;{ty=g;cl=sa;cl=su;unit=n;{ty=n;sd=2026/10/31;st=10:00;sh=af;shd=3;cftd=af,1,10;}}", new DefinitionSource());
        DefinitionValidator.Validate(units, new RunnerOptions());
        var all = units.Flatten().ToDictionary(u => u.Path);
        var point = Assert.Single(ScheduleCalculator.Calculate(all["/g/n"], all, new Registration { Anchor = new(2026, 10, 1) }, new(2026, 11, 3), new(2026, 11, 4), new RunnerOptions()));
        Assert.Equal(new DateOnly(2026, 11, 3), point.BusinessDate);
    }
}
