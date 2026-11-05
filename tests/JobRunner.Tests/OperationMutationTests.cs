using System.Text.Json;
using Azure.Data.Tables;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using JobRunner.Workers;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>大きな操作の途中停止が二重の時刻変更や部分実行を起こさないことを確認します。</summary>
public sealed class OperationMutationTests
{
    /// <summary>最初の99行の成功応答を失っても、新しいエンジンが残りを適用します。</summary>
    /// <returns>復旧後の全世代と操作完了を検証します。</returns>
    [Fact]
    public async Task RestartResumesMutationAfterSuccessfulBatchResponseIsLost()
    {
        await using var scenario = new EngineScenario();
        string children = string.Concat(Enumerable.Range(0, 105).Select(i => $"unit=j{i};{{ty=j;te=\"true\";}}"));
        await scenario.StartAsync("unit=net;{ty=n;ha=y;sd=2026/11/5;st=10:00;sh=no;" + children + "}");
        var storage = scenario.Scope.Repository;
        var operation = new Operation { Id = "move-large", Kind = "Move", StateNamespace = scenario.Engine.Namespace! };
        string commandKey = "command:" + StorageRepository.Hash(operation.Id);
        await storage.Table.AddEntityAsync(storage.Entity(commandKey, operation));
        var plan = new OperationMutation { OperationId = operation.Id };
        foreach (var row in await storage.ListAsync("occurrence:", CancellationToken.None))
        {
            var occurrence = StorageRepository.Read<Occurrence>(row);
            occurrence.Due = occurrence.Due.AddHours(2);
            plan.Changes.Add(new ArchivedRow(row.RowKey, row.ETag.ToString(), JsonSerializer.Serialize(occurrence)));
        }

        async Task Commit(IEnumerable<TableTransactionAction> changes, CancellationToken token)
        {
            var batch = changes.ToList();
            await storage.FencedAsync(scenario.Leader.Owner, scenario.Leader.Epoch, batch, token);
            if (batch.Count == 99)
            {
                throw new IOException("99行の更新成功後、応答だけ失う障害を注入します。");
            }
        }

        var commandRow = (await storage.GetAsync(commandKey, CancellationToken.None))!;
        await Assert.ThrowsAsync<IOException>(() => new OperationMutationService(storage, scenario.Clock).ApplyAsync(
            commandRow, operation, plan, Commit, scenario.Leader.Ensure, _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("Pending", StorageRepository.Read<Operation>((await storage.GetAsync(commandKey, CancellationToken.None))!).Status);
        await scenario.RestartAsync();
        Assert.Equal("Completed", StorageRepository.Read<Operation>((await storage.GetAsync(commandKey, CancellationToken.None))!).Status);
        Assert.All(await scenario.OccurrencesAsync(), o => Assert.Equal(o.OriginalDue.AddHours(2), o.Due));
        Assert.Empty(await storage.ListAsync("attempt:", CancellationToken.None));
    }

    /// <summary>計画保存後に別内容へ変更された行を、再開処理が上書きしません。</summary>
    /// <returns>競合検出と元の外部変更の維持を確認します。</returns>
    [Fact]
    public async Task RejectsChangedEtagDuringResume()
    {
        await using var scope = new StorageScope();
        var storage = scope.Repository;
        await storage.InitializeAsync(CancellationToken.None);
        await using var leader = new LeaderCoordinator(storage, TimeProvider.System);
        Assert.True(await leader.AcquireAsync(CancellationToken.None));
        await storage.Table.AddEntityAsync(storage.Entity("occurrence:root", new Occurrence { Id = "root", State = "Held" }));
        var original = (await storage.GetAsync("occurrence:root", CancellationToken.None))!;
        var operation = new Operation { Id = "conflict" };
        await storage.Table.AddEntityAsync(storage.Entity("command:conflict", operation));
        var plan = new OperationMutation { OperationId = operation.Id, Changes = [new ArchivedRow(original.RowKey, original.ETag.ToString(), JsonSerializer.Serialize(new Occurrence { Id = "root", State = "Cancelled" }))] };
        async Task Commit(IEnumerable<TableTransactionAction> actions, CancellationToken token)
        {
            await storage.FencedAsync(leader.Owner, leader.Epoch, actions, token);
            throw new IOException("計画参照を保存した直後に停止します。");
        }

        var service = new OperationMutationService(storage, TimeProvider.System);
        var command = (await storage.GetAsync("command:conflict", CancellationToken.None))!;
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(command, operation, plan, Commit, leader.Ensure, _ => Task.CompletedTask, CancellationToken.None));
        await storage.Table.UpdateEntityAsync(storage.Entity(original.RowKey, new Occurrence { Id = "root", State = "Waiting" }), original.ETag, TableUpdateMode.Replace);
        command = (await storage.GetAsync("command:conflict", CancellationToken.None))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ResumeAsync(command, (actions, token) => storage.FencedAsync(leader.Owner, leader.Epoch, actions, token), _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("Waiting", StorageRepository.Read<Occurrence>((await storage.GetAsync(original.RowKey, CancellationToken.None))!).State);
    }
}
