using System.Text;
using Azure.Data.Tables;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>子移動で実行中世代やルートの登録を作り直さないことを確認します。</summary>
public sealed class ChildMoveRuntimeTests
{
    /// <summary>開始済みの固定定義と結果不明を維持し、新世代だけへ移動を反映します。</summary>
    /// <param name="count">件数方式です。</param>
    /// <param name="crossRoot">別ルートへの移動です。</param>
    /// <param name="unknown">結果不明の子を残します。</param>
    /// <returns>旧世代の不変と新世代の構造です。</returns>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task KeepsStartedGenerationFixed(bool count, bool crossRoot, bool unknown)
    {
        await using var scenario = new EngineScenario();
        scenario.Scope.Options.ConfirmedFutureGenerations = count ? 2 : null;
        scenario.Scope.Options.ConfirmedHorizonDays = 2;
        const string schedule = "ty=n;sd=1,en;st=10:00;cy=(1,d);sh=no;";
        await scenario.StartAsync("unit=a;{" + schedule + "unit=left;{ty=n;unit=j;{ty=j;te=\"true\";}}unit=right;{ty=n;}}unit=b;{" + schedule + "}");
        var storage = scenario.Scope.Repository;
        var before = await scenario.OccurrencesAsync();
        var old = before.Where(o => o.Id == o.RootId).OrderBy(o => o.Due).ThenBy(o => o.RootPath).First();
        var members = before.Where(o => o.RootId == old.Id).ToList();
        foreach (var member in members)
        {
            member.State = member.Id == old.Id ? "Running" : unknown && member.UnitPath.EndsWith("/j", StringComparison.Ordinal) ? "Unknown" : "Held";
            member.Started = member.Id == old.Id;
            var row = (await storage.GetAsync("occurrence:" + member.Id, CancellationToken.None))!;
            await storage.Table.UpdateEntityAsync(storage.Entity(row.RowKey, member), row.ETag, TableUpdateMode.Replace);
        }

        var activation = StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", CancellationToken.None))!);
        var catalog = await new GroupCatalogService(storage).ReadAsync(activation.GroupCatalogHash!, CancellationToken.None);
        var service = new DefinitionService(storage, scenario.Scope.Options);
        var bundle = await service.ReadSourcesAsync(activation.DefinitionHash, CancellationToken.None);
        string target = crossRoot ? "/b" : "/a/right";
        var candidate = DefinitionEditor.MoveSelection(bundle, new(DefinitionEditor.Project(bundle, 9), ["/a/left/j"], target), service, scenario.Scope.Options);
        var next = service.Validate(DefinitionEditor.Render(bundle, candidate.Draft));
        await service.SaveAsync(next, CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "move-child", Kind = "Activate", Target = next.Hash, RootOrigins = candidate.Draft.RootOrigins, GroupOrigins = candidate.Draft.GroupOrigins });
        await scenario.Engine.TickAsync(CancellationToken.None);
        await scenario.RestartAsync();
        await scenario.Engine.TickAsync(CancellationToken.None);
        var values = await scenario.OccurrencesAsync();
        foreach (var member in members)
        {
            var actual = values.Single(o => o.Id == member.Id);
            Assert.Equal(member.UnitPath, actual.UnitPath);
            Assert.Equal(member.DefinitionHash, actual.DefinitionHash);
            Assert.Equal(member.State, actual.State);
            Assert.Equal(member.Jp1ExecutionId, actual.Jp1ExecutionId);
        }

        var active = StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", CancellationToken.None))!);
        var afterCatalog = await new GroupCatalogService(storage).ReadAsync(active.GroupCatalogHash!, CancellationToken.None);
        Assert.Equal(catalog.Roots.OrderBy(p => p.Key), afterCatalog.Roots.OrderBy(p => p.Key));
        var newRoots = values.Where(o => o.Id == o.RootId && o.GenerationRevision == active.Revision).ToList();
        Assert.NotEmpty(newRoots);
        Assert.Contains(values, o => o.GenerationRevision == active.Revision && o.UnitPath == target + "/j");
        Assert.DoesNotContain(values, o => o.GenerationRevision == active.Revision && o.UnitPath == "/a/left/j");
        Assert.Empty(await storage.ListAsync("attempt:", CancellationToken.None));
    }
}
