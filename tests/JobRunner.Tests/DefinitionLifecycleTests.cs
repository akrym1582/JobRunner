using System.Text;
using Azure.Data.Tables;
using JobRunner.Execution;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using JobRunner.Workers;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>Storage起動・全体停止・同じ領域での反映を実Storageで検証します。</summary>
public sealed class DefinitionLifecycleTests
{
    /// <summary>入力設定なしで管理可能な状態を作り、明示反映後も停止を維持します。</summary>
    /// <returns>初回反映と入力なしの復旧の検証です。</returns>
    [Fact]
    public async Task EmptyStartupRequiresExplicitPublicationAndResume()
    {
        await using var scope = new StorageScope();
        await scope.Repository.InitializeAsync(CancellationToken.None);
        var clock = new TestClock();
        await using var leader = new LeaderCoordinator(scope.Repository, clock);
        Assert.True(await leader.AcquireAsync(CancellationToken.None));
        var definitions = new DefinitionService(scope.Repository, scope.Options);
        var engine = new SchedulerEngine(scope.Repository, definitions, new ProcessJobExecutor(scope.Repository), scope.Options, clock);
        await engine.InitializeAsync(leader, CancellationToken.None);
        Assert.Null(engine.Namespace);
        Assert.True(StorageRepository.Read<RuntimeControl>((await scope.Repository.GetAsync("runtime-control", CancellationToken.None))!).Paused);
        var next = definitions.Validate([Source("unit=net;{ty=n;sd=1,en;st=09:00;cy=1,(1,d);sh=no;unit=j;{ty=j;te=\"true\";}}")]);
        await definitions.SaveAsync(next, CancellationToken.None);
        await scope.Repository.Table.AddEntityAsync(scope.Repository.Entity("command:publish", new Operation { Id = "publish", Kind = "Activate", Target = next.Hash, ExpectedDefinitionRevision = 0 }));
        await engine.TickAsync(CancellationToken.None);
        var active = StorageRepository.Read<Activation>((await scope.Repository.GetAsync("definition-active", CancellationToken.None))!);
        Assert.Equal(1, active.Revision);
        Assert.Equal(8, active.FormatVersion);
        Assert.NotNull(engine.Namespace);
        Assert.Empty(await scope.Repository.ListAsync("attempt:", CancellationToken.None));
        var restarted = new SchedulerEngine(scope.Repository, definitions, new ProcessJobExecutor(scope.Repository), scope.Options, clock);
        await restarted.InitializeAsync(leader, CancellationToken.None);
        Assert.Equal(active.Id, restarted.Namespace);
        Assert.Equal("Completed", StorageRepository.Read<Operation>((await scope.Repository.GetAsync("command:publish", CancellationToken.None))!).Status);
    }

    /// <summary>長い識別子を含む世代もTableの一括更新サイズを越えずに完成させます。</summary>
    /// <returns>全子の保存と完了印、同じIDでの再起動を確認します。</returns>
    [Fact]
    public async Task MaterializesLargeRowsWithinTransactionPayloadLimit()
    {
        await using var scenario = new EngineScenario();
        scenario.Scope.Options.ConfirmedFutureGenerations = 1;
        string name = new('あ', 4000);
        string children = string.Concat(Enumerable.Range(0, 110).Select(i => $"unit={name}{i};{{ty=j;te=\"true\";}}"));
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=10:00;sh=no;" + children + "}");
        var values = await scenario.OccurrencesAsync();
        Assert.Equal(111, values.Count);
        Assert.True(Assert.Single(values, o => o.Id == o.RootId).Materialized);
        var definitions = new DefinitionService(scenario.Scope.Repository, scenario.Scope.Options);
        var next = definitions.Validate([Source("unit=net;{ty=n;cm=changed;sd=2026/11/5;st=10:00;sh=no;" + children + "}")]);
        await definitions.SaveAsync(next, CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "large-publish", Kind = "Activate", Target = next.Hash });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Completed", StorageRepository.Read<Operation>((await scenario.Scope.Repository.GetAsync("command:" + StorageRepository.Hash("large-publish"), CancellationToken.None))!).Status);
        values = await scenario.OccurrencesAsync();
        Assert.Equal(222, values.Count);
        Assert.Equal(111, values.Count(o => o.State == "Superseded"));
        Assert.True(Assert.Single(values, o => o.Id == o.RootId && o.DefinitionHash == next.Hash).Materialized);
        await scenario.RestartAsync();
        Assert.Equal(values.Select(o => o.Id).Order(), (await scenario.OccurrencesAsync()).Select(o => o.Id).Order());
        Assert.Empty(await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
    }

    /// <summary>保留した未開始ルートを再開で見送り、開始済みネットの後続だけを再開します。</summary>
    /// <returns>再起動・未来補充・子の起動ゲートの検証です。</returns>
    [Fact]
    public async Task PauseSurvivesRestartAndResumeSkipsHeldRootsButContinuesStartedTree()
    {
        await using var scenario = new EngineScenario();
        scenario.Scope.Options.ConfirmedFutureGenerations = 2;
        await scenario.StartAsync("unit=net;{ty=n;sd=1,en;st=10:00;cy=1,(1,d);sh=no;unit=j;{ty=j;ha=y;te=\"true\";}}");
        await scenario.EnqueueAsync(new Operation { Id = "immediate", Kind = "Register", Target = "/net", RegistrationKind = "Immediate", Due = scenario.Clock.Now });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var started = Assert.Single(await scenario.OccurrencesAsync(), o => o.Id == o.RootId && o.Started);
        var child = Assert.Single(await scenario.OccurrencesAsync(), o => o.RootId == started.Id && o.Id != o.RootId);
        await scenario.EnqueueAsync(new Operation { Id = "pause", Kind = "PauseAll", ExpectedControlRevision = 0 });
        await scenario.Engine.TickAsync(CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "release", Kind = "Release", Target = child.Id });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Empty(await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
        var held = (await scenario.OccurrencesAsync()).First(o => o.Id == o.RootId && !o.Started);
        await scenario.EnqueueAsync(new Operation { Id = "hold", Kind = "Hold", Target = held.Id });
        await scenario.Engine.TickAsync(CancellationToken.None);
        scenario.Clock.Now = scenario.Clock.Now.AddDays(8);
        await scenario.RestartAsync();
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Empty(await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
        await scenario.EnqueueAsync(new Operation { Id = "resume", Kind = "ResumeAll", ExpectedControlRevision = 1 });
        await scenario.AdvanceUntilAsync(values => values.Any(o => o.Id == started.Id && o.State == "Succeeded"));
        Assert.Equal("Skipped", (await scenario.OccurrencesAsync()).Single(o => o.Id == held.Id).State);
        Assert.Single(await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
        Assert.DoesNotContain(await scenario.OccurrencesAsync(), o => o.Id == o.RootId && o.Id != started.Id && o.Due < scenario.Clock.Now && o.Started);
        await scenario.EnqueueAsync(new Operation { Id = "stale", Kind = "PauseAll", ExpectedControlRevision = 0 });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Rejected", StorageRepository.Read<Operation>((await scenario.Scope.Repository.GetAsync("command:" + StorageRepository.Hash("stale"), CancellationToken.None))!).Status);
    }

    /// <summary>未来の通常予定だけを新版へ置換し、個別保留と取消枠を復活させません。</summary>
    /// <param name="count">件数方式か日数方式かを切り替えます。</param>
    /// <returns>反映・切戻し・再起動の検証です。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public async Task PublicationRecalculatesOrdinaryFutureAndProtectsOperatedSlots(int? count)
    {
        await using var scenario = new EngineScenario();
        scenario.Scope.Options.ConfirmedHorizonDays = 4;
        scenario.Scope.Options.ConfirmedFutureGenerations = count;
        await scenario.StartAsync("unit=net;{ty=n;sd=1,en;st=10:00;cy=1,(1,d);sh=no;unit=old;{ty=j;te=\"true\";}}");
        var storage = scenario.Scope.Repository;
        var active = StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", CancellationToken.None))!);
        var roots = (await scenario.OccurrencesAsync()).Where(o => o.Id == o.RootId).OrderBy(o => o.Due).ToList();
        await scenario.EnqueueAsync(new Operation { Id = "hold", Kind = "Hold", Target = roots[0].Id });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var cancelRow = (await storage.GetAsync("occurrence:" + roots[1].Id, CancellationToken.None))!;
        var cancelled = StorageRepository.Read<Occurrence>(cancelRow);
        cancelled.State = "Cancelled";
        await storage.Table.UpdateEntityAsync(storage.Entity(cancelRow.RowKey, cancelled), cancelRow.ETag, TableUpdateMode.Replace);
        var definitions = new DefinitionService(storage, scenario.Scope.Options);
        var next = definitions.Validate([Source("unit=net;{ty=n;sd=1,en;st=11:00;cy=1,(1,d);sh=no;unit=new;{ty=j;te=\"true\";}}")]);
        await definitions.SaveAsync(next, CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "publish", Kind = "Activate", Target = next.Hash, ExpectedDefinitionRevision = active.Revision });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var values = await scenario.OccurrencesAsync();
        Assert.Equal(active.Id, scenario.Engine.Namespace);
        Assert.Equal("Held", values.Single(o => o.Id == roots[0].Id).State);
        Assert.Equal("Cancelled", values.Single(o => o.Id == roots[1].Id).State);
        Assert.Equal("Superseded", values.Single(o => o.Id == roots[2].Id).State);
        Assert.DoesNotContain(values, o => o.Id == o.RootId && o.DefinitionHash == next.Hash && new[] { roots[0].BusinessDate, roots[1].BusinessDate }.Contains(o.BusinessDate));
        Assert.Contains(values, o => o.DefinitionHash == next.Hash && o.UnitPath == "/net/new" && o.Due.Hour == 2);
        var ids = values.Select(o => o.Id).Order().ToList();
        await scenario.RestartAsync();
        Assert.Equal(ids, (await scenario.OccurrencesAsync()).Select(o => o.Id).Order().ToList());
        await scenario.EnqueueAsync(new Operation { Id = "same", Kind = "Activate", Target = next.Hash });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(ids, (await scenario.OccurrencesAsync()).Select(o => o.Id).Order().ToList());
        await scenario.EnqueueAsync(new Operation { Id = "rollback", Kind = "Activate", Target = active.DefinitionHash });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.DoesNotContain(await scenario.OccurrencesAsync(), o => o.Id == o.RootId && o.GenerationRevision > 1 && new[] { roots[0].BusinessDate, roots[1].BusinessDate }.Contains(o.BusinessDate));
    }

    /// <summary>全体停止は起動済みプロセスを中止せず、完了結果を回収して後続だけを待機させます。</summary>
    /// <returns>実OSプロセスの完了と再開後の後続実行です。</returns>
    [Fact]
    public async Task PauseCollectsRunningProcessWithoutStartingSuccessor()
    {
        string signal = Path.Combine(Path.GetTempPath(), "jobrunner-resume-" + Guid.NewGuid().ToString("N"));
        await using var scenario = new EngineScenario();
        try
        {
            await scenario.StartAsync("unit=net;{ty=n;sd=0,ud;ar=(f=a,t=b);unit=a;{ty=j;te=\"while [ ! -f '" + signal + "' ]; do sleep 0.02; done; printf finished\";}unit=b;{ty=j;te=\"printf successor\";}}");
            await scenario.EnqueueAsync(new Operation { Id = "run", Kind = "Register", Target = "/net", RegistrationKind = "Immediate", Due = scenario.Clock.Now });
            await scenario.AdvanceUntilAsync(values => values.Any(o => o.UnitPath == "/net/a" && o.State == "Running"));
            await scenario.EnqueueAsync(new Operation { Id = "pause", Kind = "PauseAll", ExpectedControlRevision = 0 });
            await scenario.Engine.TickAsync(CancellationToken.None);
            await File.WriteAllTextAsync(signal, "finish");
            await scenario.AdvanceUntilAsync(values => values.Any(o => o.UnitPath == "/net/a" && o.State == "Succeeded"));
            Assert.Single(await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
            Assert.Equal("Waiting", (await scenario.OccurrencesAsync()).Single(o => o.UnitPath == "/net/b").State);
            await scenario.EnqueueAsync(new Operation { Id = "resume", Kind = "ResumeAll", ExpectedControlRevision = 1 });
            await scenario.AdvanceUntilAsync(values => values.Any(o => o.Id == o.RootId && o.State == "Succeeded"));
            Assert.Equal(2, (await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None)).Count);
        }
        finally
        {
            File.Delete(signal);
        }
    }

    private static SourceEvidence Source(string text) => new() { Mapping = new DefinitionSource { BlobPath = "local/jobs.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) };
}
