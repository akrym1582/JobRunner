using System.Text;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>グループ停止と固定所属を実Storageで検証します。</summary>
public sealed class GroupOperationTests
{
    private const string Source = "unit=parent;{ty=g;unit=child;{ty=g;unit=net;{ty=n;sd=1,en;st=10:00;cy=(1,d);sh=no;unit=j;{ty=j;te=\"true\";}}}}unit=sibling;{ty=g;unit=net;{ty=n;sd=1,en;st=10:00;cy=(1,d);sh=no;}}";

    /// <summary>親停止は子の再開では解除されず、兄弟の実行を妨げません。</summary>
    /// <returns>再開・再起動後の世代と操作結果です。</returns>
    [Fact]
    public async Task AncestorPauseBlocksChildButNotSiblingAndSkipsPastRoots()
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync(Source);
        await ControlAsync(scenario, "/parent", true, "pause-parent");
        await ControlAsync(scenario, "/parent/child", false, "resume-child");
        var roots = (await scenario.OccurrencesAsync()).Where(o => o.Id == o.RootId).ToList();
        var blocked = roots.First(o => o.RootPath == "/parent/child/net" && o.State == "Waiting");
        scenario.Clock.Now = blocked.Due;
        await scenario.AdvanceUntilAsync(values => values.Any(o => o.RootPath == "/sibling/net" && o.State == "Succeeded"));
        Assert.Equal("Waiting", (await scenario.OccurrencesAsync()).Single(o => o.Id == blocked.Id).State);
        scenario.Clock.Now = blocked.Due.AddMinutes(1);
        await ControlAsync(scenario, "/parent", false, "resume-parent");
        Assert.Equal("Skipped", (await scenario.OccurrencesAsync()).Single(o => o.Id == blocked.Id).State);
        await scenario.RestartAsync();
        Assert.Equal("Skipped", (await scenario.OccurrencesAsync()).Single(o => o.Id == blocked.Id).State);
        Assert.Empty(await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
    }

    /// <summary>改名で停止を引き継ぎ、新規グループだけ停止を作ります。</summary>
    /// <returns>保存したカタログと制御版の確認です。</returns>
    [Fact]
    public async Task PublicationPreservesRenamedIdentityAndPausesNewGroups()
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync(Source);
        await ControlAsync(scenario, "/parent", true, "pause");
        var before = await CatalogAsync(scenario);
        var service = new DefinitionService(scenario.Scope.Repository, scenario.Scope.Options);
        var next = service.Validate([new SourceEvidence { Mapping = new() { BlobPath = "local/new.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Source.Replace("unit=parent", "unit=renamed", StringComparison.Ordinal) + "unit=new;{ty=g;}")) }]);
        await service.SaveAsync(next, CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "publish", Kind = "Activate", Target = next.Hash, GroupOrigins = new() { ["/renamed"] = "/parent", ["/renamed/child"] = "/parent/child", ["/sibling"] = "/sibling" } });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var after = await CatalogAsync(scenario);
        Assert.Equal(before.Groups["/parent"], after.Groups["/renamed"]);
        Assert.Equal(before.Groups["/parent/child"], after.Groups["/renamed/child"]);
        foreach (string path in new[] { "/renamed", "/new" })
        {
            var row = await scenario.Scope.Repository.GetAsync("group-control:" + after.Groups[path], CancellationToken.None);
            Assert.True(StorageRepository.Read<RuntimeControl>(row!).Paused);
        }

        Assert.All((await scenario.OccurrencesAsync()).Where(o => o.RootPath == "/parent/child/net"), o => Assert.NotNull(o.GroupCatalogHash));
        await scenario.RestartAsync();
        Assert.Equal(after.Groups, (await CatalogAsync(scenario)).Groups);
    }

    /// <summary>古い画面からの再開は新しい停止を上書きしません。</summary>
    /// <returns>拒否と永続停止の検証です。</returns>
    [Fact]
    public async Task StaleControlRevisionIsRejected()
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync(Source);
        await ControlAsync(scenario, "/parent", true, "pause");
        var catalog = await CatalogAsync(scenario);
        await scenario.EnqueueAsync(new Operation { Id = "stale", Kind = "ResumeGroup", Target = catalog.Groups["/parent"], ExpectedControlRevision = 0, ExpectedDefinitionRevision = 0 });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var row = await scenario.Scope.Repository.GetAsync("command:" + StorageRepository.Hash("stale"), CancellationToken.None);
        Assert.Equal("Rejected", StorageRepository.Read<Operation>(row!).Status);
    }

    /// <summary>開始済み世代は停止中も結果回収し、定義削除後も旧所属から再開できます。</summary>
    /// <returns>後続の待機と旧世代の完了を検証します。</returns>
    [Fact]
    public async Task RunningGenerationCanResumeAfterItsGroupIsDeleted()
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=g;{ty=g;unit=n;{ty=n;sd=2026/11/5;st=10:00;sh=no;ar=(f=a,t=b,seq);unit=a;{ty=j;te=\"true\";}unit=b;{ty=j;te=\"true\";}}}");
        var root = (await scenario.OccurrencesAsync()).Single(o => o.Id == o.RootId);
        scenario.Clock.Now = root.Due;
        await scenario.Engine.TickAsync(CancellationToken.None);
        await ControlAsync(scenario, "/g", true, "pause");
        await scenario.AdvanceUntilAsync(values => values.Any(o => o.UnitPath == "/g/n/a" && o.State == "Succeeded"));
        Assert.Equal("Waiting", (await scenario.OccurrencesAsync()).Single(o => o.UnitPath == "/g/n/b").State);
        var old = await CatalogAsync(scenario);
        var storage = scenario.Scope.Repository;
        var service = new DefinitionService(storage, scenario.Scope.Options);
        var next = service.Validate([new SourceEvidence { Mapping = new() { BlobPath = "local/next.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=other;{ty=n;sd=0,ud;}")) }]);
        await service.SaveAsync(next, CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "delete", Kind = "Activate", Target = next.Hash });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var active = StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", CancellationToken.None))!);
        var visible = await new GroupCatalogService(storage).VisibleAsync(active, CancellationToken.None);
        Assert.True(visible[old.Groups["/g"]].Retired);
        scenario.Clock.Now = scenario.Clock.Now.AddMinutes(1);
        await scenario.EnqueueAsync(new Operation { Id = "resume-retired", Kind = "ResumeGroup", Target = old.Groups["/g"], ExpectedControlRevision = 1, ExpectedDefinitionRevision = active.Revision });
        await scenario.AdvanceUntilAsync(values => values.Any(o => o.Id == root.Id && o.State == "Succeeded"));
        Assert.Equal(2, (await storage.ListAsync("attempt:", CancellationToken.None)).Count);
    }

    /// <summary>完了ルート内の単独再実行にも、削除したグループの停止制御を表示します。</summary>
    /// <returns>ONLY再実行の旧所属を検証します。</returns>
    [Fact]
    public async Task RetiredGroupRemainsVisibleForStandaloneChildRerun()
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=g;{ty=g;unit=n;{ty=n;sd=2026/11/5;st=10:00;sh=no;unit=j;{ty=j;te=\"true\";}}}");
        var root = (await scenario.OccurrencesAsync()).Single(o => o.Id == o.RootId);
        scenario.Clock.Now = root.Due;
        await scenario.AdvanceUntilAsync(values => values.Any(o => o.Id == root.Id && o.State == "Succeeded"));
        await ControlAsync(scenario, "/g", true, "pause");
        var old = await CatalogAsync(scenario);
        var storage = scenario.Scope.Repository;
        var service = new DefinitionService(storage, scenario.Scope.Options);
        var next = service.Validate([new SourceEvidence { Mapping = new() { BlobPath = "local/next.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=other;{ty=n;sd=0,ud;}")) }]);
        await service.SaveAsync(next, CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "delete", Kind = "Activate", Target = next.Hash });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var child = (await scenario.OccurrencesAsync()).Single(o => o.UnitPath == "/g/n/j");
        await scenario.EnqueueAsync(new Operation { Id = "rerun", Kind = "Rerun", Target = child.Id, RerunMethod = "ONLY", ExpectedState = "Succeeded" });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var active = StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", CancellationToken.None))!);
        var visible = await new GroupCatalogService(storage).VisibleAsync(active, CancellationToken.None);
        Assert.True(visible[old.Groups["/g"]].Retired);
        await scenario.EnqueueAsync(new Operation { Id = "resume", Kind = "ResumeGroup", Target = old.Groups["/g"], ExpectedControlRevision = 1, ExpectedDefinitionRevision = active.Revision });
        await scenario.AdvanceUntilAsync(values => values.Any(o => o.Id == child.Id && o.State == "Succeeded"));
        Assert.Equal(2, (await storage.ListAsync("attempt:", CancellationToken.None)).Count);
    }

    /// <summary>グループ改名で新しい登録パスになっても、当日の消化済み予定を再実行しません。</summary>
    /// <returns>改名後の過去世代の見送りを検証します。</returns>
    [Fact]
    public async Task RenameDoesNotExecutePastScheduleAgain()
    {
        await using var scenario = new EngineScenario();
        const string source = "unit=g;{ty=g;unit=n;{ty=n;sd=2026/11/5;st=10:00;sh=no;unit=j;{ty=j;te=\"true\";}}}";
        await scenario.StartAsync(source);
        var root = (await scenario.OccurrencesAsync()).Single(o => o.Id == o.RootId);
        scenario.Clock.Now = root.Due;
        await scenario.AdvanceUntilAsync(values => values.Any(o => o.Id == root.Id && o.State == "Succeeded"));
        scenario.Clock.Now = root.Due.AddMinutes(1);
        var storage = scenario.Scope.Repository;
        var service = new DefinitionService(storage, scenario.Scope.Options);
        var next = service.Validate([new SourceEvidence { Mapping = new() { BlobPath = "local/renamed.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(source.Replace("unit=g;", "unit=renamed;", StringComparison.Ordinal))) }]);
        await service.SaveAsync(next, CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "rename", Kind = "Activate", Target = next.Hash, GroupOrigins = new() { ["/renamed"] = "/g" } });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.DoesNotContain(await scenario.OccurrencesAsync(), o => o.UnitPath == "/renamed/n" && o.State != "Skipped");
        Assert.Single(await storage.ListAsync("attempt:", CancellationToken.None));
    }

    /// <summary>個別保留した未来世代と同じ枠を、繰り返すグループ改名で生成し直しません。</summary>
    /// <returns>旧世代の維持と改名先の非重複を検証します。</returns>
    [Fact]
    public async Task RepeatedRenamePreservesProtectedFutureSlot()
    {
        await using var scenario = new EngineScenario();
        const string source = "unit=g;{ty=g;unit=n;{ty=n;sd=2026/11/5;st=10:00;sh=no;}}";
        await scenario.StartAsync(source);
        var root = (await scenario.OccurrencesAsync()).Single(o => o.Id == o.RootId);
        await scenario.EnqueueAsync(new Operation { Id = "hold", Kind = "Hold", Target = root.Id });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var service = new DefinitionService(scenario.Scope.Repository, scenario.Scope.Options);
        string previous = "/g";
        foreach (string name in new[] { "renamed", "again" })
        {
            var next = service.Validate([new SourceEvidence { Mapping = new() { BlobPath = "local/rename.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(source.Replace("unit=g;", $"unit={name};", StringComparison.Ordinal))) }]);
            await service.SaveAsync(next, CancellationToken.None);
            await scenario.EnqueueAsync(new Operation { Id = name, Kind = "Activate", Target = next.Hash, GroupOrigins = new() { ["/" + name] = previous } });
            await scenario.Engine.TickAsync(CancellationToken.None);
            previous = "/" + name;
            Assert.Equal("Held", (await scenario.OccurrencesAsync()).Single(o => o.Id == root.Id).State);
            Assert.DoesNotContain(await scenario.OccurrencesAsync(), o => o.RootPath == previous + "/n");
        }
    }

    /// <summary>解除した自動登録をグループ改名で再登録しません。</summary>
    /// <returns>改名先の解除状態の維持を検証します。</returns>
    [Fact]
    public async Task RenamePreservesDisabledAutomaticRegistration()
    {
        await using var scenario = new EngineScenario();
        const string source = "unit=g;{ty=g;unit=n;{ty=n;sd=1,en;st=10:00;cy=(1,d);sh=no;}}";
        await scenario.StartAsync(source);
        var storage = scenario.Scope.Repository;
        await scenario.EnqueueAsync(new Operation { Id = "unregister", Kind = "Unregister", Target = StorageRepository.Hash("/g/n") });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var service = new DefinitionService(storage, scenario.Scope.Options);
        var next = service.Validate([new SourceEvidence { Mapping = new() { BlobPath = "local/rename.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(source.Replace("unit=g;", "unit=renamed;", StringComparison.Ordinal))) }]);
        await service.SaveAsync(next, CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "rename", Kind = "Activate", Target = next.Hash, GroupOrigins = new() { ["/renamed"] = "/g" } });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var row = await storage.GetAsync($"registration:{scenario.Engine.Namespace}:{StorageRepository.Hash("/renamed/n")}", CancellationToken.None);
        Assert.True(StorageRepository.Read<Registration>(row!).Disabled);
        Assert.DoesNotContain(await scenario.OccurrencesAsync(), o => o.RootPath == "/renamed/n");
    }

    private static async Task<GroupCatalog> CatalogAsync(EngineScenario scenario)
    {
        var storage = scenario.Scope.Repository;
        var active = StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", CancellationToken.None))!);
        return await new GroupCatalogService(storage).ReadAsync(active.GroupCatalogHash!, CancellationToken.None);
    }

    private static async Task ControlAsync(EngineScenario scenario, string path, bool pause, string id)
    {
        var storage = scenario.Scope.Repository;
        var active = StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", CancellationToken.None))!);
        var catalog = await CatalogAsync(scenario);
        var control = StorageRepository.Read<RuntimeControl>((await storage.GetAsync("group-control:" + catalog.Groups[path], CancellationToken.None))!);
        await scenario.EnqueueAsync(new Operation { Id = id, Kind = pause ? "PauseGroup" : "ResumeGroup", Target = catalog.Groups[path], ExpectedControlRevision = control.Revision, ExpectedDefinitionRevision = active.Revision });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Completed", StorageRepository.Read<Operation>((await storage.GetAsync("command:" + StorageRepository.Hash(id), CancellationToken.None))!).Status);
    }
}
