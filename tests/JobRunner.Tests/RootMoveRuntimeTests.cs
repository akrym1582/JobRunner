using System.Text;
using Azure.Data.Tables;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>移動前の世代を固定ルートIDで保護します。</summary>
public sealed class RootMoveRuntimeTests
{
    /// <summary>別グループ移動後も開始済み世代との排他を守ります。</summary>
    /// <param name="unknown">結果不明の子があるかです。</param>
    /// <param name="count">件数方式かです。</param>
    /// <returns>再起動・翌日枠での検証です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MovePreservesRunningAndUnknownGuard(bool unknown, bool count)
    {
        await using var scenario = new EngineScenario();
        scenario.Scope.Options.ConfirmedFutureGenerations = count ? 2 : null;
        const string network = "unit=n;{ty=n;sd=1,en;st=10:00;cy=(1,d);sh=no;unit=j;{ty=j;te=\"true\";}}";
        await scenario.StartAsync("unit=a;{ty=g;" + network + "}unit=b;{ty=g;}");
        var storage = scenario.Scope.Repository;
        var root = (await scenario.OccurrencesAsync()).Where(o => o.Id == o.RootId).OrderBy(o => o.Due).First();
        var child = (await scenario.OccurrencesAsync()).Single(o => o.RootId == root.Id && o.Id != o.RootId);
        root.State = "Running";
        root.Started = true;
        child.State = unknown ? "Unknown" : "Held";
        foreach (var value in new[] { root, child })
        {
            var row = (await storage.GetAsync("occurrence:" + value.Id, CancellationToken.None))!;
            await storage.Table.UpdateEntityAsync(storage.Entity(row.RowKey, value), row.ETag, TableUpdateMode.Replace);
        }

        scenario.Clock.Now = root.Due.AddMinutes(1);
        var service = new DefinitionService(storage, scenario.Scope.Options);
        var next = service.Validate([new SourceEvidence { Mapping = new() { BlobPath = "local/moved" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=a;{ty=g;}unit=b;{ty=g;" + network + "}")) }]);
        await service.SaveAsync(next, CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "move", Kind = "Activate", Target = next.Hash, GroupOrigins = new() { ["/a"] = "/a", ["/b"] = "/b" }, RootOrigins = new() { ["/b/n"] = "/a/n" } });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Completed", StorageRepository.Read<Operation>((await storage.GetAsync("command:" + StorageRepository.Hash("move"), CancellationToken.None))!).Status);
        await scenario.RestartAsync();
        scenario.Clock.Now = root.Due.AddDays(1);
        await scenario.Engine.TickAsync(CancellationToken.None);
        var pending = (await scenario.OccurrencesAsync()).Single(o => o.Id == o.RootId && o.RootPath == "/b/n" && o.Due == scenario.Clock.Now);
        Assert.Equal("Waiting", pending.State);
        Assert.Empty(await storage.ListAsync("attempt:", CancellationToken.None));
        Assert.Equal(root.GroupCatalogHash, (await scenario.OccurrencesAsync()).Single(o => o.Id == root.Id).GroupCatalogHash);
        child.State = "Succeeded";
        var saved = (await storage.GetAsync("occurrence:" + child.Id, CancellationToken.None))!;
        await storage.Table.UpdateEntityAsync(storage.Entity(saved.RowKey, child), saved.ETag, TableUpdateMode.Replace);
        await scenario.AdvanceUntilAsync(values => values.Any(o => o.Id == pending.Id && o.Started));
    }

    /// <summary>移動で解除済み登録や保留した予定枠を復活させません。</summary>
    /// <param name="disabled">登録解除を試験するかです。</param>
    /// <returns>移動後の登録と枠の保護です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MovePreservesDisabledRegistrationAndProtectedSlot(bool disabled)
    {
        await using var scenario = new EngineScenario();
        const string network = "unit=n;{ty=n;sd=2026/11/5;st=10:00;sh=no;}";
        await scenario.StartAsync("unit=a;{ty=g;" + network + "}unit=b;{ty=g;}");
        var storage = scenario.Scope.Repository;
        var root = (await scenario.OccurrencesAsync()).Single(o => o.Id == o.RootId);
        await scenario.EnqueueAsync(new Operation { Id = "protect", Kind = disabled ? "Unregister" : "Hold", Target = disabled ? StorageRepository.Hash("/a/n") : root.Id });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var service = new DefinitionService(storage, scenario.Scope.Options);
        var next = service.Validate([new SourceEvidence { Mapping = new() { BlobPath = "local/moved" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=a;{ty=g;}unit=b;{ty=g;" + network + "}")) }]);
        await service.SaveAsync(next, CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "move", Kind = "Activate", Target = next.Hash, RootOrigins = new() { ["/b/n"] = "/a/n" } });
        await scenario.Engine.TickAsync(CancellationToken.None);
        await scenario.RestartAsync();
        await scenario.Engine.TickAsync(CancellationToken.None);
        var registration = StorageRepository.Read<Registration>((await storage.GetAsync($"registration:{scenario.Engine.Namespace}:{StorageRepository.Hash("/b/n")}", CancellationToken.None))!);
        Assert.Equal(disabled, registration.Disabled);
        Assert.DoesNotContain(await scenario.OccurrencesAsync(), o => o.RootPath == "/b/n" && o.Due == root.Due);
        if (!disabled)
        {
            Assert.Equal("Held", (await scenario.OccurrencesAsync()).Single(o => o.Id == root.Id).State);
        }
    }

    /// <summary>個別確定登録の条件を移動で黙って消しません。</summary>
    /// <returns>拒否後も登録と採用が維持されることを確認します。</returns>
    [Fact]
    public async Task ExplicitConfirmedRegistrationRequiresReviewBeforePublication()
    {
        await using var scenario = new EngineScenario();
        const string network = "unit=n;{ty=n;sd=1,en;st=10:00;cy=(1,d);sh=no;}";
        await scenario.StartAsync("unit=a;{ty=g;" + network + "}unit=b;{ty=g;}");
        var storage = scenario.Scope.Repository;
        await scenario.EnqueueAsync(new Operation { Id = "manual", Kind = "Register", Target = "/a/n", RegistrationKind = "Confirmed", FutureGenerations = 2 });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var service = new DefinitionService(storage, scenario.Scope.Options);
        var next = service.Validate([new SourceEvidence { Mapping = new() { BlobPath = "local/moved" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=a;{ty=g;}unit=b;{ty=g;" + network + "}")) }]);
        await service.SaveAsync(next, CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "move", Kind = "Activate", Target = next.Hash, RootOrigins = new() { ["/b/n"] = "/a/n" } });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var result = StorageRepository.Read<Operation>((await storage.GetAsync("command:" + StorageRepository.Hash("move"), CancellationToken.None))!);
        Assert.Equal("Rejected", result.Status);
        Assert.Contains("個別確定登録", result.Result, StringComparison.Ordinal);
        var registration = StorageRepository.Read<Registration>((await storage.GetAsync($"registration:{scenario.Engine.Namespace}:request-{StorageRepository.Hash("manual")}", CancellationToken.None))!);
        Assert.False(registration.ReplenishmentStopped);
        Assert.False(registration.Disabled);
        Assert.Equal(2, registration.FutureGenerations);
        Assert.Equal(0, StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", CancellationToken.None))!).Revision);
        await scenario.EnqueueAsync(new Operation { Id = "unregister-manual", Kind = "Unregister", Target = registration.Id });
        await scenario.Engine.TickAsync(CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "move-after-review", Kind = "Activate", Target = next.Hash, RootOrigins = new() { ["/b/n"] = "/a/n" } });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Completed", StorageRepository.Read<Operation>((await storage.GetAsync("command:" + StorageRepository.Hash("move-after-review"), CancellationToken.None))!).Status);
    }

    /// <summary>同じ完全名を新しいIDで作り直しても不明結果を追い越しません。</summary>
    /// <returns>旧パスによる保守的な抑止です。</returns>
    [Fact]
    public async Task RecreatedPathStillBlocksUnknownGeneration()
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=a;{ty=g;unit=n;{ty=n;sd=1,en;st=10:00;cy=(1,d);sh=no;unit=j;{ty=j;te=\"true\";}}}");
        var storage = scenario.Scope.Repository;
        var values = await scenario.OccurrencesAsync();
        var root = values.Where(o => o.Id == o.RootId).OrderBy(o => o.Due).First();
        var child = values.Single(o => o.RootId == root.Id && o.Id != root.Id);
        root.State = "Running";
        root.Started = true;
        child.State = "Unknown";
        foreach (var value in new[] { root, child })
        {
            var row = (await storage.GetAsync("occurrence:" + value.Id, CancellationToken.None))!;
            await storage.Table.UpdateEntityAsync(storage.Entity(row.RowKey, value), row.ETag, TableUpdateMode.Replace);
        }

        scenario.Clock.Now = root.Due.AddMinutes(1);
        var active = StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", CancellationToken.None))!);
        await scenario.EnqueueAsync(new Operation { Id = "recreate", Kind = "Activate", Target = active.DefinitionHash, GroupOrigins = new(), RootOrigins = new() });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var after = StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", CancellationToken.None))!);
        var service = new GroupCatalogService(storage);
        var oldCatalog = await service.ReadAsync(root.GroupCatalogHash!, CancellationToken.None);
        var newCatalog = await service.ReadAsync(after.GroupCatalogHash!, CancellationToken.None);
        Assert.NotEqual(oldCatalog.Roots[root.RootPath], newCatalog.Roots[root.RootPath]);
        await scenario.EnqueueAsync(new Operation { Id = "resume", Kind = "ResumeGroup", Target = newCatalog.Groups["/a"], ExpectedDefinitionRevision = after.Revision, ExpectedControlRevision = 0 });
        await scenario.Engine.TickAsync(CancellationToken.None);
        scenario.Clock.Now = root.Due.AddDays(1);
        await scenario.Engine.TickAsync(CancellationToken.None);
        var pending = Assert.Single(await scenario.OccurrencesAsync(), o => o.Id == o.RootId && o.Due == scenario.Clock.Now && o.GenerationRevision == after.Revision);
        Assert.Equal("Waiting", pending.State);
        Assert.False(pending.Started);
        Assert.Empty(await storage.ListAsync("attempt:", CancellationToken.None));
    }
}
