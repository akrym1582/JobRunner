using JobRunner.Models;
using Xunit;

namespace JobRunner.Tests;

/// <summary>ネストの採用ルールと非実行日の後続を検証します。</summary>
public sealed class NestedScheduleTests
{
    /// <summary>同じ上位ルールでは最早候補を選び、リンクしない子孫は実行しません。</summary>
    /// <returns>生成・復旧・実プロセスの一貫性を確認します。</returns>
    [Fact]
    public async Task SelectsEarliestLinkedRuleAndSkipsUnlinkedDescendants()
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=10:00;sh=no;ar=(f=off,t=after);unit=off;{ty=n;de=n;sd=1,2026/11/5;ln=1,2;sh=no;unit=j;{ty=j;te=\"exit 99\";}}unit=after;{ty=j;te=\"true\";}unit=on;{ty=n;de=n;sd=1,2026/11/5;sd=2,2026/11/5;ln=1,1;ln=2,1;st=1,12:00;st=2,11:00;sh=1,no;sh=2,no;unit=j;{ty=j;te=\"true\";}}}");
        var rows = await scenario.OccurrencesAsync();
        var selected = Assert.Single(rows, o => o.UnitPath == "/net/on");
        Assert.Equal(2, selected.Rule);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 2, 0, 0, TimeSpan.Zero), selected.Due);
        Assert.All(rows.Where(o => o.UnitPath.StartsWith("/net/off", StringComparison.Ordinal)), o => Assert.Equal("NotScheduled", o.State));
        await scenario.RestartAsync();
        scenario.Clock.Now = selected.Due;
        await scenario.AdvanceUntilAsync(current => current.Any(o => o.UnitPath == "/net" && o.State == "Succeeded"));
        Assert.Equal(2, (await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None)).Count);
    }
}
