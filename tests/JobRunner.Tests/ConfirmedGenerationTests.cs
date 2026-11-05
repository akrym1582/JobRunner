using JobRunner.Models;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>確定登録の未来世代枠と、再起動・一時変更・解除の相互作用を検証します。</summary>
public sealed class ConfirmedGenerationTests
{
    /// <summary>開始のたびに次世代を補充し、保持する件数は履歴数と区別します。</summary>
    /// <returns>実Storageの登録・世代・履歴を検証します。</returns>
    [Fact]
    public async Task MaintainsFutureCountAcrossStartRestartAndUnregister()
    {
        await using var scenario = new EngineScenario();
        scenario.Scope.Options.ConfirmedFutureGenerations = 3;
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=10:00;cy=(1,d);sh=no;unit=j;{ty=j;te=\"true\";}}");
        var roots = (await scenario.OccurrencesAsync()).Where(o => o.Id == o.RootId).OrderBy(o => o.Due).ToList();
        Assert.Equal(3, roots.Count);
        scenario.Clock.Now = roots[0].Due;
        await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.Id == roots[0].Id && o.State == "Succeeded"));
        roots = (await scenario.OccurrencesAsync()).Where(o => o.Id == o.RootId).OrderBy(o => o.Due).ToList();
        Assert.Equal(4, roots.Count);
        Assert.Equal(3, roots.Count(o => !o.Started));
        await scenario.EnqueueAsync(new Operation { Id = "hold", Kind = "Hold", Target = roots[1].Id });
        await scenario.Engine.TickAsync(CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "move", Kind = "Move", Target = roots[1].Id, Due = roots[1].Due.AddHours(4) });
        await scenario.Engine.TickAsync(CancellationToken.None);
        await scenario.RestartAsync();
        await scenario.Engine.TickAsync(CancellationToken.None);
        var restored = Assert.Single(await scenario.OccurrencesAsync(), o => o.Id == roots[1].Id);
        Assert.Equal("Held", restored.State);
        Assert.Equal(roots[1].Due.AddHours(4), restored.Due);
        Assert.Equal(4, (await scenario.OccurrencesAsync()).Count(o => o.Id == o.RootId));
        await scenario.EnqueueAsync(new Operation { Id = "unregister", Kind = "Unregister", Target = roots[0].RegistrationId });
        await scenario.Engine.TickAsync(CancellationToken.None);
        scenario.Clock.Now = scenario.Clock.Now.AddDays(10);
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(4, (await scenario.OccurrencesAsync()).Count(o => o.Id == o.RootId));
    }

    /// <summary>ルート保存後に子生成が中断しても、枠が満杯という理由で部分ネットを残しません。</summary>
    /// <returns>同じ世代IDの子が補完されることを確認します。</returns>
    [Fact]
    public async Task RepairsInterruptedMaterializationBeforeCountingFutureSlots()
    {
        await using var scenario = new EngineScenario();
        scenario.Scope.Options.ConfirmedFutureGenerations = 1;
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=10:00;sh=no;unit=j;{ty=j;te=\"true\";}}");
        var storage = scenario.Scope.Repository;
        var rows = await storage.ListAsync("occurrence:", CancellationToken.None);
        var rootRow = Assert.Single(rows, r => StorageRepository.Read<Occurrence>(r).UnitPath == "/net");
        var childRow = Assert.Single(rows, r => StorageRepository.Read<Occurrence>(r).UnitPath == "/net/j");
        var root = StorageRepository.Read<Occurrence>(rootRow);
        root.Materialized = false;
        await storage.Table.UpdateEntityAsync(storage.Entity(rootRow.RowKey, root), rootRow.ETag, Azure.Data.Tables.TableUpdateMode.Replace);
        await storage.Table.DeleteEntityAsync(storage.Partition, childRow.RowKey, childRow.ETag);
        await scenario.RestartAsync();
        await scenario.Engine.TickAsync(CancellationToken.None);
        var repaired = await scenario.OccurrencesAsync();
        Assert.Equal(2, repaired.Count);
        Assert.True(Assert.Single(repaired, o => o.Id == root.Id).Materialized);
        Assert.Equal(StorageRepository.Read<Occurrence>(childRow).Id, Assert.Single(repaired, o => o.UnitPath == "/net/j").Id);
    }

    /// <summary>複数の非稼働日が同じ運用日へ振り替わっても、同一世代を枠へ重複計上しません。</summary>
    /// <returns>登録直後に指定した個数の異なるルートが存在することを検証します。</returns>
    [Fact]
    public async Task CountsDistinctGenerationsAfterHolidayShift()
    {
        await using var scenario = new EngineScenario();
        scenario.Scope.Options.ConfirmedFutureGenerations = 3;
        await scenario.StartAsync("unit=g;{ty=g;cl=sa;cl=su;unit=net;{ty=n;sd=2026/11/7;st=10:00;cy=(1,d);sh=af;shd=3;}}");
        var roots = (await scenario.OccurrencesAsync()).OrderBy(o => o.Due).ToList();
        Assert.Equal(3, roots.Count);
        Assert.Equal(3, roots.Select(o => o.Id).Distinct().Count());
        Assert.Equal(new DateOnly(2026, 11, 9), roots[0].BusinessDate);
        Assert.Equal(new DateOnly(2026, 11, 11), roots[2].BusinessDate);
    }

    /// <summary>実日時が次でも業務日は前日になる48時間制の予定を補充から落としません。</summary>
    /// <returns>二つのルールの実日時順と、業務日の保持を確認します。</returns>
    [Fact]
    public async Task ReplenishesPreviousBusinessDayAfterLaterBusinessDayStarts()
    {
        await using var scenario = new EngineScenario();
        scenario.Scope.Options.ConfirmedFutureGenerations = 1;
        scenario.Scope.Options.ScheduleTimeMode = 48;
        await scenario.StartAsync("unit=net;{ty=n;sd=1,2026/11/4;st=1,47:00;sh=1,no;sd=2,2026/11/5;st=2,10:00;sh=2,no;}");
        var first = Assert.Single(await scenario.OccurrencesAsync());
        Assert.Equal(new DateOnly(2026, 11, 5), first.BusinessDate);
        scenario.Clock.Now = first.Due;
        await scenario.Engine.TickAsync(CancellationToken.None);
        await scenario.Engine.TickAsync(CancellationToken.None);
        var next = Assert.Single(await scenario.OccurrencesAsync(), o => !o.Started);
        Assert.Equal(new DateOnly(2026, 11, 4), next.BusinessDate);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 14, 0, 0, TimeSpan.Zero), next.Due);
    }

    /// <summary>登録操作で件数を指定でき、範囲外の要求は何も生成しません。</summary>
    /// <param name="count">要求する未来世代数です。</param>
    /// <param name="accepted">受理すべき場合trueです。</param>
    /// <returns>要求結果と実世代数の検証です。</returns>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(99, true)]
    [InlineData(100, false)]
    public async Task ValidatesGenerationCountBeforeCreatingRegistration(int count, bool accepted)
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/6;st=10:00;cy=(1,d);sh=no;}");
        await scenario.EnqueueAsync(new Operation { Id = "/net", Kind = "Register", Target = "/net", FutureGenerations = count });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var operation = StorageRepository.Read<Operation>((await scenario.Scope.Repository.GetAsync("command:" + StorageRepository.Hash("/net"), CancellationToken.None))!);
        Assert.Equal(accepted ? "Completed" : "Rejected", operation.Status);
        Assert.Equal(accepted ? count : 0, (await scenario.OccurrencesAsync()).Count(o => o.RegistrationId == operation.Result));
        Assert.Equal(accepted ? 2 : 1, (await scenario.Scope.Repository.ListAsync("registration:", CancellationToken.None)).Count);
    }
}
