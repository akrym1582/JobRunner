using System.Text;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>補充定義の切替が確定済み世代と復旧を変更しないことを検証します。</summary>
public sealed class DefinitionVersionTests
{
    /// <summary>旧形式を同じ採用領域で昇格し、旧バイナリの再選出時には形式検査で停止させます。</summary>
    /// <param name="format">移行元の保存形式です。</param>
    /// <returns>採用IDと既存世代の維持を確認します。</returns>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task UpgradesLegacyActivationWithoutReplacingNamespace(int format)
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=10:00;sh=no;}");
        var storage = scenario.Scope.Repository;
        var row = (await storage.GetAsync("definition-active", CancellationToken.None))!;
        var legacy = StorageRepository.Read<Activation>(row);
        legacy.FormatVersion = format;
        await storage.Table.UpdateEntityAsync(storage.Entity(row.RowKey, legacy), row.ETag, Azure.Data.Tables.TableUpdateMode.Replace);
        await storage.Container.GetBlobClient($"management/{storage.Partition}/active.json").UploadAsync(BinaryData.FromObjectAsJson(legacy), overwrite: true);
        var before = (await scenario.OccurrencesAsync()).Select(o => o.Id).Order().ToList();
        await scenario.RestartAsync();
        var upgraded = StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", CancellationToken.None))!);
        Assert.Equal(6, upgraded.FormatVersion);
        Assert.Equal(legacy.Id, upgraded.Id);
        Assert.Equal(before, (await scenario.OccurrencesAsync()).Select(o => o.Id).Order().ToList());
    }

    /// <summary>旧形式の個別監視変更と待機中登録を、移行後の定義切替から保護します。</summary>
    /// <returns>省略定義の固定と旧運用指定の保持を確認します。</returns>
    [Fact]
    public async Task MigrationPreservesIndividualMonitorAndPendingRegistrationRevision()
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=10:00;sh=no;sy=C5;}");
        var storage = scenario.Scope.Repository;
        var activeRow = (await storage.GetAsync("definition-active", CancellationToken.None))!;
        var activation = StorageRepository.Read<Activation>(activeRow);
        activation.FormatVersion = 5;
        await storage.Table.UpdateEntityAsync(storage.Entity(activeRow.RowKey, activation), activeRow.ETag, Azure.Data.Tables.TableUpdateMode.Replace);
        var row = Assert.Single(await storage.ListAsync("occurrence:", CancellationToken.None));
        var root = StorageRepository.Read<Occurrence>(row);
        root.DefinitionHash = null;
        root.DelayMonitor!.StartMinutes = 25;
        root.IndividuallyModified = false;
        await storage.Table.UpdateEntityAsync(storage.Entity(row.RowKey, root), row.ETag, Azure.Data.Tables.TableUpdateMode.Replace);
        await scenario.EnqueueAsync(new Operation { Id = "legacy-register", StateNamespace = activation.Id, Kind = "Register", Target = "/net", RegistrationKind = "Immediate" });
        await scenario.RestartAsync();
        var restored = Assert.Single(await scenario.OccurrencesAsync());
        Assert.Equal(activation.DefinitionHash, restored.DefinitionHash);
        Assert.True(restored.IndividuallyModified);
        Assert.Equal(25, restored.DelayMonitor!.StartMinutes);
        var pending = StorageRepository.Read<Operation>((await storage.GetAsync("command:" + StorageRepository.Hash("legacy-register"), CancellationToken.None))!);
        Assert.Equal(activation.Revision, pending.AcceptedDefinitionRevision);
        Assert.Equal("Pending", pending.Status);
    }

    /// <summary>生成済みの部分世代は旧版で補完し、次の補充だけ新版を使用します。</summary>
    /// <param name="missingChildren">子保存前の中断を再現する場合trueです。</param>
    /// <returns>実Storageと再起動を含む検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PinsExistingGenerationAcrossUpdateAndRestart(bool missingChildren)
    {
        await using var scenario = new EngineScenario();
        scenario.Scope.Options.ConfirmedFutureGenerations = 1;
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=10:00;cy=(1,d);sh=no;sy=C5;fd=7;unit=old;{ty=j;te=\"true\";}}");
        var root = Assert.Single(await scenario.OccurrencesAsync(), o => o.Id == o.RootId);
        var storage = scenario.Scope.Repository;
        var service = new DefinitionService(storage, scenario.Scope.Options);
        var definition = service.Validate([new SourceEvidence { Mapping = new DefinitionSource { BlobPath = "input/new" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=net;{ty=n;sd=2026/11/5;st=10:00;cy=(1,d);sh=no;sy=C20;fd=30;unit=new;{ty=j;te=\"true\";}}")) }]);
        await service.SaveAsync(definition, CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "update", Kind = "UpdateRegistrationDefinition", Target = root.RegistrationId, ExpectedDefinitionHash = root.DefinitionHash, DefinitionHash = definition.Hash });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var result = StorageRepository.Read<Operation>((await storage.GetAsync("command:" + StorageRepository.Hash("update"), CancellationToken.None))!);
        Assert.Equal("Completed", result.Status);
        if (missingChildren)
        {
            var rows = await storage.ListAsync("occurrence:", CancellationToken.None);
            var rootRow = Assert.Single(rows, r => StorageRepository.Read<Occurrence>(r).Id == root.Id);
            root.Materialized = false;
            await storage.Table.UpdateEntityAsync(storage.Entity(rootRow.RowKey, root), rootRow.ETag, Azure.Data.Tables.TableUpdateMode.Replace);
            var child = Assert.Single(rows, r => StorageRepository.Read<Occurrence>(r).UnitPath == "/net/old");
            await storage.Table.DeleteEntityAsync(storage.Partition, child.RowKey, child.ETag);
        }

        await scenario.RestartAsync();
        Assert.Single(await scenario.OccurrencesAsync(), o => o.UnitPath == "/net/old" && o.DefinitionHash == root.DefinitionHash);
        Assert.DoesNotContain(await scenario.OccurrencesAsync(), o => o.UnitPath == "/net/new");
        scenario.Clock.Now = root.Due;
        await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.Id == root.Id && o.State == "Succeeded"));
        var next = Assert.Single(await scenario.OccurrencesAsync(), o => o.Id == o.RootId && o.Id != root.Id);
        Assert.Equal(definition.Hash, next.DefinitionHash);
        Assert.Equal(20, next.DelayMonitor!.StartMinutes);
        Assert.Equal(30, next.DelayMonitor.DurationMinutes);
        var previous = (await scenario.OccurrencesAsync()).Single(o => o.Id == root.Id);
        Assert.Equal(5, previous.DelayMonitor!.StartMinutes);
        Assert.Equal(7, previous.DelayMonitor.DurationMinutes);
        Assert.Single(await scenario.OccurrencesAsync(), o => o.RootId == next.Id && o.UnitPath == "/net/new");
        await scenario.EnqueueAsync(new Operation { Id = "stale", Kind = "UpdateRegistrationDefinition", Target = root.RegistrationId, ExpectedDefinitionHash = root.DefinitionHash, DefinitionHash = definition.Hash });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Rejected", StorageRepository.Read<Operation>((await storage.GetAsync("command:" + StorageRepository.Hash("stale"), CancellationToken.None))!).Status);
    }
}
