using JobRunner.Models;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>期間・日時の確定登録と再起動後の超過時動作を検証します。</summary>
public sealed class FixedRegistrationTests
{
    /// <summary>先読み期間を超える指定を一括確定し、最初だけ保留します。</summary>
    /// <returns>実Storageで期間の両端と再送を確認します。</returns>
    [Fact]
    public async Task MaterializesWholePeriodAndPreservesFirstHold()
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=10:00;cy=(1,d);sh=no;}");
        await scenario.EnqueueAsync(new Operation { Id = "period", Kind = "Register", Target = "/net", Start = new(2026, 11, 7), End = new(2026, 11, 10), Holding = true });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var result = StorageRepository.Read<Operation>((await scenario.Scope.Repository.GetAsync("command:" + StorageRepository.Hash("period"), CancellationToken.None))!);
        Assert.Equal("Completed", result.Status);
        var roots = (await scenario.OccurrencesAsync()).Where(o => o.RegistrationId == result.Result).OrderBy(o => o.Due).ToList();
        Assert.Equal(3, roots.Count);
        Assert.Equal("Held", roots[0].State);
        Assert.All(roots.Skip(1), r => Assert.Equal("Waiting", r.State));
        await scenario.RestartAsync();
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(3, (await scenario.OccurrencesAsync()).Count(o => o.RegistrationId == result.Result));
    }

    /// <summary>固定日時の登録は一世代だけを作り、過去予定の方針を復旧でも維持します。</summary>
    /// <param name="policy">超過時の動作です。</param>
    /// <param name="expected">復旧後に期待するルート状態です。</param>
    /// <returns>日時と保存された方針を確認します。</returns>
    [Theory]
    [InlineData("NEXTTIME", "Skipped")]
    [InlineData("IMMEDIATE", "Succeeded")]
    public async Task RecoversFixedDateAccordingToExplicitPolicy(string policy, string expected)
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=net;{ty=n;sd=0,ud;}");
        await scenario.EnqueueAsync(new Operation { Id = "fixed", Kind = "Register", Target = "/net", FixedDue = scenario.Clock.Now.AddHours(1), PassedDaemonStarts = policy });
        await scenario.Engine.TickAsync(CancellationToken.None);
        scenario.Clock.Now = scenario.Clock.Now.AddDays(10);
        await scenario.RestartAsync();
        await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.State == expected));
        Assert.Single(await scenario.OccurrencesAsync());
    }
}
