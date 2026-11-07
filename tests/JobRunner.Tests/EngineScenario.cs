using Azure.Core.Pipeline;
using JobRunner.Execution;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using JobRunner.Workers;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>実行エンジンと実Storageを接続し、時刻と操作を試験から制御します。</summary>
internal sealed class EngineScenario : IAsyncDisposable
{
    /// <summary>テストだけが使用する入力コンテナーの名前です。</summary>
    private readonly string inputName = "input-" + Guid.NewGuid().ToString("N");

    /// <summary>Initializes a new instance of the <see cref="EngineScenario"/> class. OS実行を含む各依存を結線します。</summary>
    /// <param name="policy">実Storage通信へ中断を注入する場合だけ指定します。</param>
    public EngineScenario(HttpPipelinePolicy? policy = null)
    {
        Scope = new StorageScope(policy);
        Scope.Options.ConfirmedHorizonDays = 1;
        Scope.Options.DefinitionSources = [new DefinitionSource { BlobPath = inputName + "/jobs" }];
        Leader = new LeaderCoordinator(Scope.Repository, Clock);
        Engine = new SchedulerEngine(Scope.Repository, new DefinitionService(Scope.Repository, Scope.Options), new ProcessJobExecutor(Scope.Repository), Scope.Options, Clock);
    }

    /// <summary>Gets. このシナリオだけのStorageです。</summary>
    public StorageScope Scope { get; }

    /// <summary>Gets. 復旧と予定判定を制御するUTC時計です。</summary>
    public TestClock Clock { get; } = new();

    /// <summary>Gets. 実Blobリースを取得する稼働系です。</summary>
    public LeaderCoordinator Leader { get; private set; }

    /// <summary>Gets. 実プロセスへ接続したエンジンです。</summary>
    public SchedulerEngine Engine { get; private set; }

    /// <summary>入力を配置し、稼働系取得と自動登録を実行します。</summary>
    /// <param name="source">テストで明示したJP1原文です。</param>
    /// <returns>初期化と自動登録の完了を表します。</returns>
    public async Task StartAsync(string source)
    {
        await Scope.Repository.InitializeAsync(CancellationToken.None);
        var input = Scope.Repository.Blobs.GetBlobContainerClient(inputName);
        await input.CreateAsync();
        await input.GetBlobClient("jobs").UploadAsync(BinaryData.FromString(source));
        await TestDefinitions.SeedAsync(Scope.Repository, Scope.Options, Clock);
        Assert.True(await Leader.AcquireAsync(CancellationToken.None));
        await Engine.InitializeAsync(Leader, CancellationToken.None);
    }

    /// <summary>メモリと稼働系権限を失わせ、永続状態だけから新しいエンジンを復旧します。</summary>
    /// <returns>新しいEpochでの初期化完了を表します。</returns>
    public async Task RestartAsync()
    {
        await Engine.StopAsync();
        await Leader.DisposeAsync();
        Leader = new LeaderCoordinator(Scope.Repository, Clock);
        Assert.True(await Leader.AcquireAsync(CancellationToken.None));
        Engine = new SchedulerEngine(Scope.Repository, new DefinitionService(Scope.Repository, Scope.Options), new ProcessJobExecutor(Scope.Repository), Scope.Options, Clock);
        await Engine.InitializeAsync(Leader, CancellationToken.None);
    }

    /// <summary>APIと同じ永続キューへ操作を保存します。</summary>
    /// <param name="operation">対象と期待状態を指定した操作です。</param>
    /// <returns>操作受付の保存完了を表します。</returns>
    public Task EnqueueAsync(Operation operation)
    {
        operation.StateNamespace = Engine.Namespace!;
        return Scope.Repository.Table.AddEntityAsync(Scope.Repository.Entity("command:" + StorageRepository.Hash(operation.Id), operation));
    }

    /// <summary>条件が成立するまでポーリングし、有限時間内に終わらなければ失敗させます。</summary>
    /// <param name="condition">永続状態に対する受入条件です。</param>
    /// <returns>受入条件を満たすまでのタスクです。</returns>
    public async Task AdvanceUntilAsync(Func<List<Occurrence>, bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            await Engine.TickAsync(timeout.Token);
            if (condition(await OccurrencesAsync()))
            {
                return;
            }

            await Task.Delay(20, timeout.Token);
        }

        Assert.Fail("30秒以内に状態遷移が完了しませんでした。");
    }

    /// <summary>キャッシュではなく永続化された世代を取得します。</summary>
    /// <returns>現在の全世代です。</returns>
    public async Task<List<Occurrence>> OccurrencesAsync() => (await Scope.Repository.ListAsync("occurrence:", CancellationToken.None)).Select(StorageRepository.Read<Occurrence>).ToList();

    /// <summary>起動した実プロセス、リース、専用Storageを順に終了します。</summary>
    /// <returns>後片付けの完了を表します。</returns>
    public async ValueTask DisposeAsync()
    {
        await Engine.StopAsync();
        await Leader.DisposeAsync();
        await Scope.Repository.Blobs.GetBlobContainerClient(inputName).DeleteIfExistsAsync();
        await Scope.DisposeAsync();
    }
}
