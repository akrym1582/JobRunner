using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using JobRunner.Workers;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>履歴のコピー先行、分割削除の再開、未知結果と冪等性の保存を検証します。</summary>
public sealed class RetentionTests
{
    /// <summary>旧版で保存した要求ハッシュを、追加項目の省略によって維持します。</summary>
    [Fact]
    public void PreservesLegacyOperationHashWhenNewFieldsAreOmitted()
    {
        const string legacyJson = """
            {"StateNamespace":"","Id":"legacy-request","Kind":"Hold","Target":"generation","RequestHash":"","Status":"Pending","Result":null,"AttemptId":null,"ExpectedState":null,"Reason":null,"Resolution":null,"Due":null,"RegistrationKind":"Confirmed","End":null}
            """;
        var operation = new Operation { Id = "legacy-request", Kind = "Hold", Target = "generation" };
        string legacyHash = StorageRepository.Hash(legacyJson);
        Assert.Equal(legacyHash, StorageRepository.Hash(System.Text.Json.JsonSerializer.Serialize(operation)));
        operation.Holding = false;
        Assert.NotEqual(legacyHash, StorageRepository.Hash(System.Text.Json.JsonSerializer.Serialize(operation)));
    }

    /// <summary>99操作を超える履歴を途中停止させ、コピー済みデータから安全に削除を再開します。</summary>
    /// <returns>スナップショットと墓標の保存結果を確認します。</returns>
    [Fact]
    public async Task ResumesArchiveAfterDeleteResponseIsLost()
    {
        await using var scope = new StorageScope();
        scope.Options.HistoryArchiveAfterDays = 1;
        var storage = scope.Repository;
        await storage.InitializeAsync(CancellationToken.None);
        var clock = new TestClock();
        await SeedAsync(storage, clock.Now.AddDays(-2), 101);
        await using var leader = new LeaderCoordinator(storage, clock);
        Assert.True(await leader.AcquireAsync(CancellationToken.None));
        var service = new HistoryRetentionService(storage, scope.Options, clock);
        bool fail = true;
        async Task Commit(IEnumerable<TableTransactionAction> actions, CancellationToken token)
        {
            var list = actions.ToList();
            await storage.FencedAsync(leader.Owner, leader.Epoch, list, token);
            if (fail && list.Any(a => a.ActionType == TableTransactionActionType.Delete))
            {
                fail = false;
                throw new IOException("削除成功後の応答喪失を注入します。");
            }
        }

        await Assert.ThrowsAsync<IOException>(() => service.SweepAsync(Commit, leader.Ensure, CancellationToken.None));
        var interrupted = StorageRepository.Read<Occurrence>((await storage.GetAsync("occurrence:root", CancellationToken.None))!);
        Assert.Equal("Archiving", interrupted.State);
        Assert.NotNull(interrupted.ArchivePath);
        await service.SweepAsync(Commit, leader.Ensure, CancellationToken.None);
        var root = StorageRepository.Read<Occurrence>(Assert.Single(await storage.ListAsync("occurrence:", CancellationToken.None)));
        Assert.Equal("Archived", root.State);
        var archive = (await storage.Container.GetBlobClient(root.ArchivePath).DownloadContentAsync()).Value.Content.ToObjectFromJson<HistoryArchive>()!;
        Assert.Equal(103, archive.Rows.Count);
        Assert.Equal("Succeeded", JsonSerializer.Deserialize<Occurrence>(archive.Rows.Single(r => r.Key == "occurrence:root").Json)!.State);
        Assert.Empty(await storage.ListAsync("attempt:", CancellationToken.None));
    }

    /// <summary>コピー先Blobが利用不能なら、Tableの履歴を一行も削除しません。</summary>
    /// <returns>失敗後の原状態が維持されることを確認します。</returns>
    [Fact]
    public async Task CopyFailurePreservesAllTableRows()
    {
        await using var scope = new StorageScope();
        scope.Options.HistoryArchiveAfterDays = 1;
        var storage = scope.Repository;
        await storage.InitializeAsync(CancellationToken.None);
        var clock = new TestClock();
        await SeedAsync(storage, clock.Now.AddDays(-2), 1);
        await storage.Container.DeleteAsync();
        var service = new HistoryRetentionService(storage, scope.Options, clock);
        await Assert.ThrowsAsync<RequestFailedException>(() => service.SweepAsync((_, _) => throw new InvalidOperationException("保存前の削除は禁止です。"), () => { }, CancellationToken.None));
        Assert.Equal(2, (await storage.ListAsync("occurrence:", CancellationToken.None)).Count);
        Assert.Equal("Succeeded", StorageRepository.Read<Occurrence>((await storage.GetAsync("occurrence:root", CancellationToken.None))!).State);
        Assert.Single(await storage.ListAsync("attempt:", CancellationToken.None));
    }

    /// <summary>Unknownを含むネットとログは期限を過ぎても保持し、既知のログだけ削除します。</summary>
    /// <returns>異なる結果の保持方針を確認します。</returns>
    [Fact]
    public async Task UnknownResultsAndLogsNeverExpireAutomatically()
    {
        await using var scope = new StorageScope();
        scope.Options.HistoryArchiveAfterDays = 1;
        scope.Options.LogRetentionDays = 1;
        var storage = scope.Repository;
        await storage.InitializeAsync(CancellationToken.None);
        var clock = new TestClock();
        await SeedAsync(storage, clock.Now.AddDays(-2), 1);
        var childRow = (await storage.GetAsync("occurrence:job0", CancellationToken.None))!;
        var child = StorageRepository.Read<Occurrence>(childRow);
        child.State = "Unknown";
        await storage.Table.UpdateEntityAsync(storage.Entity(childRow.RowKey, child), childRow.ETag, TableUpdateMode.Replace);
        await storage.Table.AddEntityAsync(storage.Entity("attempt:unknown", new JobAttempt { Id = "unknown", OccurrenceId = "job0", State = "Unknown", FinishedAt = clock.Now.AddDays(-20) }));
        await storage.PutImmutableAsync("logs/job0/unknown/stdout/0.txt", BinaryData.FromString("業務確認用"), CancellationToken.None);
        await storage.PutImmutableAsync("logs/job0/known/stdout/0.txt", BinaryData.FromString("完了済み"), CancellationToken.None);
        var service = new HistoryRetentionService(storage, scope.Options, clock);
        await service.SweepAsync((_, _) => throw new InvalidOperationException("Unknownを持つネットは退避禁止です。"), () => { }, CancellationToken.None);
        Assert.True((await storage.Container.GetBlobClient("logs/job0/unknown/stdout/0.txt").ExistsAsync()).Value);
        Assert.False((await storage.Container.GetBlobClient("logs/job0/known/stdout/0.txt").ExistsAsync()).Value);
        Assert.Equal(2, (await storage.ListAsync("occurrence:", CancellationToken.None)).Count);
    }

    /// <summary>操作の本文を退避しても要求ハッシュと元の結果を取得できます。</summary>
    /// <returns>期限境界と再送判定の保持を確認します。</returns>
    [Fact]
    public async Task ArchivedOperationRetainsIdempotencyAndOriginalResult()
    {
        await using var scope = new StorageScope();
        scope.Options.OperationArchiveAfterDays = 1;
        var storage = scope.Repository;
        await storage.InitializeAsync(CancellationToken.None);
        var clock = new TestClock();
        var operation = new Operation { Id = "same-id", RequestHash = "unchanged", Kind = "Resolve", Target = "job", Reason = "業務結果を確認済み", Result = "resolved", Status = "Completed", CompletedAt = clock.Now.AddDays(-1) };
        await storage.Table.AddEntityAsync(storage.Entity("command:one", operation));
        await using var leader = new LeaderCoordinator(storage, clock);
        Assert.True(await leader.AcquireAsync(CancellationToken.None));
        var service = new HistoryRetentionService(storage, scope.Options, clock);
        await service.SweepAsync((actions, token) => storage.FencedAsync(leader.Owner, leader.Epoch, actions, token), leader.Ensure, CancellationToken.None);
        var row = (await storage.GetAsync("command:one", CancellationToken.None))!;
        var tombstone = StorageRepository.Read<Operation>(row);
        Assert.Equal("unchanged", tombstone.RequestHash);
        Assert.Null(tombstone.Reason);
        var restored = await HistoryRetentionService.ReadOperationAsync(row, storage, CancellationToken.None);
        Assert.Equal(operation.Reason, restored.Reason);
        Assert.Equal(operation.Result, restored.Result);
    }

    /// <summary>時刻が確定したネットと試行を作り、保持期限の判断を独立して検証します。</summary>
    /// <param name="storage">専用のテストStorageです。</param>
    /// <param name="finished">業務結果の確定UTC時刻です。</param>
    /// <param name="children">ルート直下の子数です。</param>
    /// <returns>試験用の永続状態を用意します。</returns>
    private static async Task SeedAsync(StorageRepository storage, DateTimeOffset finished, int children)
    {
        await storage.Table.AddEntityAsync(storage.Entity("occurrence:root", new Occurrence { Id = "root", RootId = "root", UnitPath = "/net", State = "Succeeded", FinishedAt = finished }));
        for (int i = 0; i < children; i++)
        {
            await storage.Table.AddEntityAsync(storage.Entity("occurrence:job" + i, new Occurrence { Id = "job" + i, RootId = "root", UnitPath = "/net/job" + i, State = "Succeeded", FinishedAt = finished }));
        }

        await storage.Table.AddEntityAsync(storage.Entity("attempt:known", new JobAttempt { Id = "known", OccurrenceId = "job0", State = "Succeeded", FinishedAt = finished }));
    }
}
