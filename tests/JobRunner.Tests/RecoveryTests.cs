using JobRunner.Execution;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using JobRunner.Workers;
using Xunit;

namespace JobRunner.Tests;

/// <summary>メモリを捨てて再構築し、ハッシュと永続状態による復旧を検証します。</summary>
public sealed class RecoveryTests
{
    /// <summary>再起動は同じ採用IDを維持し、内容変更と切り戻しは別IDへ分離します。</summary>
    /// <returns>入力と管理状態の一貫性を確認するタスクです。</returns>
    [Fact]
    public async Task RestartChangeRollbackAndMissingState()
    {
        string suffix = Guid.NewGuid().ToString("N");
        var options = new RunnerOptions
        {
            ManagementContainer = "test-" + suffix,
            TableName = "Test" + suffix,
            ScheduleTimeMode = 48,
            ConfirmedHorizonDays = 2,
            DefinitionSources = [new DefinitionSource { BlobPath = "input-" + suffix + "/jobs" }],
        };
        var clock = new TestClock();
        var storage = new StorageRepository(options);
        await storage.InitializeAsync(CancellationToken.None);
        var input = storage.Blobs.GetBlobContainerClient("input-" + suffix);
        await input.CreateAsync();
        const string original = "unit=net;{ty=n;sd=2026/11/5;st=09:00;cy=(1,d);sh=no;unit=j;{ty=j;te=\"true\";}}";
        await input.GetBlobClient("jobs").UploadAsync(BinaryData.FromString(original));
        try
        {
            string first = await InitializeAsync(storage, options, clock);
            var initial = (await storage.ListAsync("occurrence:", CancellationToken.None)).Select(StorageRepository.Read<Occurrence>).ToList();
            Assert.Equal(4, initial.Count);
            Assert.All(initial, o => Assert.Equal("Waiting", o.State));
            clock.Now = clock.Now.AddHours(1);
            Assert.Equal(first, await InitializeAsync(storage, options, clock));
            var restored = (await storage.ListAsync("occurrence:", CancellationToken.None)).Select(StorageRepository.Read<Occurrence>).ToList();
            Assert.Single(restored, o => o.Id == o.RootId && o.State == "Skipped");
            Assert.All(restored.Where(o => o.Id != o.RootId), o => Assert.Equal("Waiting", o.State));
            await input.GetBlobClient("jobs").UploadAsync(BinaryData.FromString(original.Replace("true", "printf changed", StringComparison.Ordinal)), overwrite: true);
            string changed = await InitializeAsync(storage, options, clock);
            Assert.NotEqual(first, changed);
            await input.GetBlobClient("jobs").UploadAsync(BinaryData.FromString(original), overwrite: true);
            string rollback = await InitializeAsync(storage, options, clock);
            Assert.NotEqual(first, rollback);
            Assert.NotEqual(changed, rollback);
            options.ScheduleTimeMode = 24;
            Assert.NotEqual(rollback, await InitializeAsync(storage, options, clock));
            var active = await storage.GetAsync("definition-active", CancellationToken.None);
            await storage.Table.DeleteEntityAsync(storage.Partition, "definition-active", active!.ETag);
            await Assert.ThrowsAsync<InvalidDataException>(() => InitializeAsync(storage, options, clock));
        }
        finally
        {
            await input.DeleteIfExistsAsync();
            await storage.Container.DeleteIfExistsAsync();
            await storage.Table.DeleteAsync();
        }
    }

    /// <summary>旧所有者のStartingは結果不明とし、再起動しても新たな試行を作りません。</summary>
    /// <returns>試行履歴が一件のまま保たれることを確認します。</returns>
    [Fact]
    public async Task StartingAttemptBecomesUnknownWithoutRerun()
    {
        string suffix = Guid.NewGuid().ToString("N");
        var options = new RunnerOptions
        {
            ManagementContainer = "test-" + suffix,
            TableName = "Test" + suffix,
            DefinitionSources = [new DefinitionSource { BlobPath = "input-" + suffix + "/jobs" }],
        };
        var storage = new StorageRepository(options);
        await storage.InitializeAsync(CancellationToken.None);
        var input = storage.Blobs.GetBlobContainerClient("input-" + suffix);
        await input.CreateAsync();
        await input.GetBlobClient("jobs").UploadAsync(BinaryData.FromString("unit=net;{ty=n;sd=0,ud;}"));
        try
        {
            var clock = new TestClock();
            string state = await InitializeAsync(storage, options, clock);
            var occurrence = new Occurrence { Id = "test-job", Namespace = state, RootId = "root", RootPath = "/net", UnitPath = "/net/job", State = "Starting", AttemptId = "old-attempt" };
            await storage.Table.AddEntityAsync(storage.Entity("occurrence:test-job", occurrence));
            await storage.Table.AddEntityAsync(storage.Entity("attempt:old-attempt", new JobAttempt { Id = "old-attempt", OccurrenceId = "test-job", State = "Starting", Owner = "dead", Epoch = 1 }));
            Assert.Equal(state, await InitializeAsync(storage, options, clock));
            var restored = StorageRepository.Read<Occurrence>((await storage.GetAsync("occurrence:test-job", CancellationToken.None))!);
            Assert.Equal("Unknown", restored.State);
            Assert.Single(await storage.ListAsync("attempt:", CancellationToken.None));
            Assert.Equal(state, await InitializeAsync(storage, options, clock));
            Assert.Single(await storage.ListAsync("attempt:", CancellationToken.None));
        }
        finally
        {
            await input.DeleteIfExistsAsync();
            await storage.Container.DeleteIfExistsAsync();
            await storage.Table.DeleteAsync();
        }
    }

    /// <summary>新しい実行エンジンを作り、メモリキャッシュを再利用せず初期化します。</summary>
    /// <param name="storage">既存の永続データです。</param>
    /// <param name="options">今回の実効設定です。</param>
    /// <param name="clock">復旧時点を決める時計です。</param>
    /// <returns>復旧または新規採用された状態領域です。</returns>
    private static async Task<string> InitializeAsync(StorageRepository storage, RunnerOptions options, TimeProvider clock)
    {
        await using var leader = new LeaderCoordinator(storage, clock);
        Assert.True(await leader.AcquireAsync(CancellationToken.None));
        var service = new DefinitionService(storage, options);
        var engine = new SchedulerEngine(storage, service, new ProcessJobExecutor(storage), options, clock);
        await engine.InitializeAsync(leader, CancellationToken.None);
        return engine.Namespace!;
    }
}
