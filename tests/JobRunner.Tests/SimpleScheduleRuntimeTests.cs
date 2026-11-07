using JobRunner.Models;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>起算定義を実Storageへ登録し、再起動と保留・解除を検証します。</summary>
public sealed class SimpleScheduleRuntimeTests
{
    /// <summary>前後の月次運用日が確定世代となり、再起動後も二重起動・保留解除しません。</summary>
    /// <param name="count">未来世代数方式かです。</param>
    /// <returns>実Storageと制御時計による検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonthlyOperatingOffsetsSurviveRestartAndControls(bool count)
    {
        await using var scenario = new EngineScenario();
        scenario.Clock.Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        scenario.Scope.Options.ConfirmedHorizonDays = 40;
        scenario.Scope.Options.ConfirmedFutureGenerations = count ? 2 : null;
        string Network(string name, string offset) => $"unit={name};{{ty=n;jc=/calendar;sd=1,25;st=09:00;cy=1,(1,m);sh=1,no;cftd=1,{offset};unit=j;{{ty=j;te=\"true\";}}}}";
        await scenario.StartAsync("unit=calendar;{ty=g;cl=sa;cl=su;cl=2026/11/23;cl=2026/11/26;}" + Network("forward", "af,2,10") + Network("backward", "be,5,10"));
        var initial = await scenario.OccurrencesAsync();
        var forward = Assert.Single(initial, o => o.Id == o.RootId && o.RootPath == "/forward" && o.BusinessDate == new DateOnly(2026, 11, 30));
        var backward = Assert.Single(initial, o => o.Id == o.RootId && o.RootPath == "/backward" && o.BusinessDate == new DateOnly(2026, 11, 17));
        await scenario.RestartAsync();
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(initial.Select(o => o.Id).Order(), (await scenario.OccurrencesAsync()).Select(o => o.Id).Order());
        scenario.Clock.Now = backward.Due;
        await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.Id == backward.Id && o.State == "Succeeded"));
        await scenario.EnqueueAsync(new Operation { Id = "hold-forward", Kind = "Hold", Target = forward.Id });
        await scenario.Engine.TickAsync(CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "unregister-backward", Kind = "UnregisterRoot", Target = "/backward" });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Completed", StorageRepository.Read<Operation>((await scenario.Scope.Repository.GetAsync("command:" + StorageRepository.Hash("hold-forward"), CancellationToken.None))!).Status);
        Assert.Equal("Completed", StorageRepository.Read<Operation>((await scenario.Scope.Repository.GetAsync("command:" + StorageRepository.Hash("unregister-backward"), CancellationToken.None))!).Status);
        await scenario.RestartAsync();
        scenario.Clock.Now = forward.Due;
        await scenario.Engine.TickAsync(CancellationToken.None);
        var final = await scenario.OccurrencesAsync();
        Assert.Equal("Held", Assert.Single(final, o => o.Id == forward.Id).State);
        Assert.Equal("Succeeded", Assert.Single(final, o => o.Id == backward.Id).State);
        Assert.DoesNotContain(final, o => o.RootPath == "/backward" && !o.Started && o.State is "Planned" or "Waiting");
        Assert.Single(await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
        Assert.All(final.Where(o => o.Id == o.RootId).GroupBy(o => (o.RootPath, o.BusinessDate)), group => Assert.Single(group));
    }
}
