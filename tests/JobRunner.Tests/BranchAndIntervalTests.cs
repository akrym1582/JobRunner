using JobRunner.Models;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>条件分岐、異常時後続、一定間隔の永続予定を検証します。</summary>
public sealed class BranchAndIntervalTests
{
    /// <summary>判定不成立の従属を実行せず、通常後続を継続します。</summary>
    /// <param name="threshold">先行終了コードと比較する値です。</param>
    /// <param name="attempts">起動されるプロセス数です。</param>
    /// <returns>実プロセスと確定した条件結果を確認します。</returns>
    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 2)]
    public async Task BranchUsesPersistedExitCode(int threshold, int attempts)
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync($"unit=net;{{ty=n;sd=2026/11/5;st=09:00;sh=no;ar=(f=a,t=judge);ar=(f=judge,t=branch,con);ar=(f=judge,t=after);unit=a;{{ty=j;te=\"true\";}}unit=judge;{{ty=jdj;ej=eq;ejc={threshold};}}unit=branch;{{ty=j;te=\"true\";}}unit=after;{{ty=j;te=\"true\";}}}}");
        await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.UnitPath == "/net" && o.State == "Succeeded"));
        Assert.Equal(attempts, (await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None)).Count);
        Assert.Equal(threshold == 0, Assert.Single(await scenario.OccurrencesAsync(), o => o.UnitPath == "/net/judge").ConditionMatched);
    }

    /// <summary>リカバリージョブは先行の異常時だけ実行します。</summary>
    /// <param name="code">先行の終了コードです。</param>
    /// <returns>保存された起動試行数を確認します。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RecoveryJobRunsOnlyOnFailure(int code)
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync($"unit=net;{{ty=n;sd=2026/11/5;st=09:00;sh=no;ar=(f=a,t=recovery);unit=a;{{ty=j;te=\"exit {code}\";}}unit=recovery;{{ty=rj;te=\"true\";}}}}");
        await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.UnitPath == "/net" && o.State is "Succeeded" or "Failed"));
        Assert.Equal(code == 0 ? 1 : 2, (await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None)).Count);
    }

    /// <summary>予定時刻からの一定間隔を復旧で維持し、同じ予定を再生成しません。</summary>
    /// <returns>境界と実行後の補充を確認します。</returns>
    [Fact]
    public async Task IntervalKeepsPhaseAndExclusiveEndAcrossRestart()
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=net;{ty=n;sd=0,ud;}");
        var start = scenario.Clock.Now.AddMinutes(15);
        await scenario.EnqueueAsync(new Operation { Id = "interval", Kind = "Register", Target = "/net", FixedDue = start, IntervalUntil = start.AddMinutes(90), IntervalMinutes = 30, Holding = true });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var roots = (await scenario.OccurrencesAsync()).OrderBy(o => o.Due).ToList();
        Assert.Equal(new[] { start, start.AddMinutes(30), start.AddMinutes(60) }, roots.Select(o => o.Due));
        Assert.Equal("Held", roots[0].State);
        await scenario.RestartAsync();
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(3, (await scenario.OccurrencesAsync()).Count);
        scenario.Clock.Now = scenario.Clock.Now.AddDays(10);
        await scenario.RestartAsync();
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(3, (await scenario.OccurrencesAsync()).Count);
        Assert.Empty(await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
    }
}
