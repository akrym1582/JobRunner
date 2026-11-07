using System.Reflection;
using System.Text;
using System.Text.Json;
using Azure.Data.Tables;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>反映の行更新・補充・完了保存の各境界を失った場合の復旧です。</summary>
public sealed class PublicationRecoveryTests
{
    /// <summary>保存成功後の応答喪失と部分世代を、保存済みの同一計画から再開します。</summary>
    /// <param name="stage">1〜4は計画参照・分割バッチ・適用印、5〜7は世代保存中です。</param>
    /// <param name="count">件数方式か日数方式です。</param>
    /// <returns>採用ID・旧履歴・世代ID・実行IDの維持です。</returns>
    [Theory]
    [InlineData(1, null)]
    [InlineData(2, null)]
    [InlineData(3, null)]
    [InlineData(4, null)]
    [InlineData(5, null)]
    [InlineData(6, null)]
    [InlineData(7, null)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 1)]
    [InlineData(7, 1)]
    public async Task ResumesPublicationWithoutReplayingOldOrDuplicatingNewSlots(int stage, int? count)
    {
        await using var scenario = new EngineScenario();
        scenario.Scope.Options.ConfirmedFutureGenerations = count;
        string children = string.Concat(Enumerable.Range(0, 105).Select(i => $"unit=j{i};{{ty=j;te=\"true\";}}"));
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=10:00;cy=(1,d);sh=no;" + children + "}");
        var storage = scenario.Scope.Repository;
        var activeRow = (await storage.GetAsync("definition-active", CancellationToken.None))!;
        var active = StorageRepository.Read<Activation>(activeRow);
        string stateNamespace = active.Id;
        var registrationRow = Assert.Single(await storage.ListAsync("registration:", CancellationToken.None));
        var registration = StorageRepository.Read<Registration>(registrationRow);
        var definitions = new DefinitionService(storage, scenario.Scope.Options);
        var next = definitions.Validate([new SourceEvidence { Mapping = new DefinitionSource { BlobPath = "local/new.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=net;{ty=n;sd=2026/11/5;st=11:00;cy=(1,d);sh=no;" + children + "}")) }]);
        await definitions.SaveAsync(next, CancellationToken.None);
        var before = await scenario.OccurrencesAsync();
        var plan = new OperationMutation { FormatVersion = 2, OperationId = "publication" };
        foreach (var row in await storage.ListAsync("occurrence:", CancellationToken.None))
        {
            var value = StorageRepository.Read<Occurrence>(row);
            value.State = "Superseded";
            plan.Changes.Add(new(row.RowKey, row.ETag.ToString(), JsonSerializer.Serialize(value)));
        }

        registration.GenerationRevision = 1;
        registration.DefinitionHash = next.Hash;
        registration.Through = new(2026, 11, 4);
        registration.ExhaustedOn = null;
        active.Revision = 1;
        active.DefinitionHash = next.Hash;
        plan.Changes.Add(new(registrationRow.RowKey, registrationRow.ETag.ToString(), JsonSerializer.Serialize(registration)));
        plan.Changes.Add(new(activeRow.RowKey, activeRow.ETag.ToString(), JsonSerializer.Serialize(active)));
        var operation = new Operation { Id = plan.OperationId, Kind = "Activate", Target = next.Hash, StateNamespace = stateNamespace };
        string key = "command:" + StorageRepository.Hash(operation.Id);
        await storage.Table.AddEntityAsync(storage.Entity(key, operation));
        int commits = 0;
        async Task Commit(IEnumerable<TableTransactionAction> changes, CancellationToken token)
        {
            await storage.FencedAsync(scenario.Leader.Owner, scenario.Leader.Epoch, changes, token);
            if (++commits == stage && stage <= 4)
            {
                throw new IOException("保存成功後の応答喪失");
            }
        }

        async Task Finalize(CancellationToken token)
        {
            // 本番の補充経路で生成後、永続化の途中状態を残してプロセスを作り直します。
            var method = typeof(SchedulerEngine).GetMethod("FinalizePublicationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(scenario.Engine, [token])!;
            var roots = (await scenario.OccurrencesAsync()).Where(o => o.Id == o.RootId && o.GenerationRevision == 1).ToList();
            foreach (var root in roots)
            {
                var row = (await storage.GetAsync("occurrence:" + root.Id, token))!;
                root.Materialized = false;
                await storage.Table.UpdateEntityAsync(storage.Entity(row.RowKey, root), row.ETag, TableUpdateMode.Replace, token);
                int keep = stage == 5 ? 0 : stage == 6 ? 50 : 105;
                foreach (var child in (await storage.ListAsync("occurrence:", token)).Where(r => StorageRepository.Read<Occurrence>(r).RootId == root.Id && StorageRepository.Read<Occurrence>(r).Id != root.Id).Skip(keep))
                {
                    await storage.Table.DeleteEntityAsync(storage.Partition, child.RowKey, child.ETag, token);
                }
            }

            throw new IOException("ルート・子の途中・完了印保存前の停止");
        }

        var operationRow = (await storage.GetAsync(key, CancellationToken.None))!;
        await Assert.ThrowsAsync<IOException>(() => new OperationMutationService(storage, scenario.Clock).ApplyAsync(operationRow, operation, plan, Commit, scenario.Leader.Ensure, _ => Task.CompletedTask, CancellationToken.None, Finalize));
        var createdIds = (await scenario.OccurrencesAsync()).Where(o => o.Id == o.RootId).ToDictionary(o => o.Id, o => o.Jp1ExecutionId, StringComparer.Ordinal);
        if (stage >= 5)
        {
            scenario.Clock.Now = scenario.Clock.Now.AddDays(10);
        }

        await scenario.RestartAsync();
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(stateNamespace, scenario.Engine.Namespace);
        Assert.Equal("Completed", StorageRepository.Read<Operation>((await storage.GetAsync(key, CancellationToken.None))!).Status);
        var values = await scenario.OccurrencesAsync();
        Assert.All(before, old => Assert.Equal("Superseded", values.Single(o => o.Id == old.Id).State));
        Assert.All(createdIds, pair => Assert.Equal(pair.Value, values.Single(o => o.Id == pair.Key).Jp1ExecutionId));
        Assert.All(values.Where(o => o.Id == o.RootId), root =>
        {
            Assert.True(root.Materialized);
            Assert.Equal(106, values.Count(o => o.RootId == root.Id));
        });
        Assert.Empty(await storage.ListAsync("attempt:", CancellationToken.None));
        Assert.DoesNotContain(values, o => o.Due < scenario.Clock.Now && o.Started);
        var ids = values.Select(o => o.Id).Order(StringComparer.Ordinal).ToList();
        await scenario.RestartAsync();
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(ids, (await scenario.OccurrencesAsync()).Select(o => o.Id).Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>再開待ち反映があっても設定不一致時に保存済み計画を進めません。</summary>
    /// <returns>計画参照の保存直後からの誤った設定による復旧を拒否します。</returns>
    [Fact]
    public async Task RejectsChangedSettingsBeforeResumingPendingPublication()
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=net;{ty=n;sd=0,ud;}");
        var storage = scenario.Scope.Repository;
        var row = (await storage.GetAsync("definition-active", CancellationToken.None))!;
        var activation = StorageRepository.Read<Activation>(row);
        activation.Revision++;
        var plan = new OperationMutation { FormatVersion = 2, OperationId = "pending", Changes = [new(row.RowKey, row.ETag.ToString(), JsonSerializer.Serialize(activation))] };
        var operation = new Operation { Id = "pending", Kind = "Activate", Target = activation.DefinitionHash, StateNamespace = activation.Id };
        string key = "command:" + StorageRepository.Hash(operation.Id);
        await storage.Table.AddEntityAsync(storage.Entity(key, operation));
        async Task Commit(IEnumerable<TableTransactionAction> changes, CancellationToken token)
        {
            await storage.FencedAsync(scenario.Leader.Owner, scenario.Leader.Epoch, changes, token);
            throw new IOException("計画参照を保存後に停止");
        }

        var command = (await storage.GetAsync(key, CancellationToken.None))!;
        await Assert.ThrowsAsync<IOException>(() => new OperationMutationService(storage, scenario.Clock).ApplyAsync(command, operation, plan, Commit, scenario.Leader.Ensure, _ => Task.CompletedTask, CancellationToken.None));
        scenario.Scope.Options.TimeZone = "UTC";
        await Assert.ThrowsAsync<InvalidDataException>(() => scenario.RestartAsync());
        Assert.Equal(row.GetString("Json"), (await storage.GetAsync(row.RowKey, CancellationToken.None))!.GetString("Json"));
        Assert.Equal("Pending", StorageRepository.Read<Operation>((await storage.GetAsync(key, CancellationToken.None))!).Status);
        Assert.Empty(await scenario.OccurrencesAsync());
    }
}
