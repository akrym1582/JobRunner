using Azure;
using Azure.Data.Tables;
using JobRunner.Models;
using JobRunner.Storage;
using JobRunner.Workers;
using Xunit;

namespace JobRunner.Tests;

/// <summary>Azuriteの実リースとTableトランザクションで排他保証を検証します。</summary>
public sealed class StorageTests
{
    /// <summary>リース競合と世代交代後の古い所有者による書き込み拒否を確認します。</summary>
    /// <returns>テスト専用リソースの削除までのタスクです。</returns>
    [Fact]
    public async Task LeaseAndEpochRejectStaleWriter()
    {
        string suffix = Guid.NewGuid().ToString("N");
        var options = new RunnerOptions { ManagementContainer = "test-" + suffix, TableName = "Test" + suffix };
        var storage = new StorageRepository(options);
        await storage.InitializeAsync(CancellationToken.None);
        try
        {
            string owner;
            long epoch;
            await using (var first = new LeaderCoordinator(storage, TimeProvider.System))
            {
                Assert.True(await first.AcquireAsync(CancellationToken.None));
                owner = first.Owner;
                epoch = first.Epoch;
                await using var second = new LeaderCoordinator(storage, TimeProvider.System);
                Assert.False(await second.AcquireAsync(CancellationToken.None));
                await storage.FencedAsync(owner, epoch, [new TableTransactionAction(TableTransactionActionType.Add, storage.Entity("value", "original"))], CancellationToken.None);
            }

            await using var replacement = new LeaderCoordinator(storage, TimeProvider.System);
            Assert.True(await replacement.AcquireAsync(CancellationToken.None));
            Assert.True(replacement.Epoch > epoch);
            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.FencedAsync(owner, epoch, [new TableTransactionAction(TableTransactionActionType.Add, storage.Entity("stale", "forbidden"))], CancellationToken.None));
            Assert.Null(await storage.GetAsync("stale", CancellationToken.None));
        }
        finally
        {
            await storage.Container.DeleteIfExistsAsync();
            await storage.Table.DeleteAsync();
        }
    }

    /// <summary>ETag不一致の更新でleaderも対象状態も部分更新されないことを確認します。</summary>
    /// <returns>Azurite上の原子性検証です。</returns>
    [Fact]
    public async Task ConflictingEntityRollsBackWholeTransaction()
    {
        string suffix = Guid.NewGuid().ToString("N");
        var storage = new StorageRepository(new RunnerOptions { ManagementContainer = "test-" + suffix, TableName = "Test" + suffix });
        await storage.InitializeAsync(CancellationToken.None);
        await using (var leader = new LeaderCoordinator(storage, TimeProvider.System))
        {
            Assert.True(await leader.AcquireAsync(CancellationToken.None));
            await storage.Table.AddEntityAsync(storage.Entity("value", "old"));
            var stale = await storage.GetAsync("value", CancellationToken.None);
            await storage.Table.UpdateEntityAsync(storage.Entity("value", "new"), stale!.ETag, TableUpdateMode.Replace);
            var before = await storage.GetAsync("leader", CancellationToken.None);
            await Assert.ThrowsAsync<TableTransactionFailedException>(() => storage.FencedAsync(leader.Owner, leader.Epoch, [new TableTransactionAction(TableTransactionActionType.UpdateReplace, storage.Entity("value", "forbidden"), stale.ETag)], CancellationToken.None));
            var after = await storage.GetAsync("leader", CancellationToken.None);
            Assert.Equal(before!.ETag, after!.ETag);
            Assert.Equal("new", StorageRepository.Read<string>((await storage.GetAsync("value", CancellationToken.None))!));
        }

        await storage.Container.DeleteIfExistsAsync();
        await storage.Table.DeleteAsync();
    }
}
