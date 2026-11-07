using JobRunner.Services;
using JobRunner.Storage;

namespace JobRunner.Workers;

/// <summary>APIとは独立して選出・復旧・実行を進め、障害時には起動を停止します。</summary>
/// <param name="storage">APIと共有する状態の保存先です。</param>
/// <param name="engine">復旧と世代実行を担当するサービスです。</param>
/// <param name="clock">ポーリングとリース管理の時計です。</param>
/// <param name="logger">初期化や実行停止の原因を記録するログです。</param>
public sealed class SchedulerWorker(StorageRepository storage, SchedulerEngine engine, TimeProvider clock, ILogger<SchedulerWorker> logger) : BackgroundService
{
    /// <summary>現在の選出試行です。以前の権限を取得し直して流用しません。</summary>
    private LeaderCoordinator? leader;

    /// <summary>Gets a value indicating whether. API受付に必要なStorageの初期接続が確認できたことを表します。</summary>
    public bool StorageReady { get; private set; }

    /// <summary>Gets a value indicating whether. 初期化が済んだ現在の稼働系である場合だけtrueです。</summary>
    public bool IsLeader => Phase is "Ready" or "AwaitingDefinition" && leader?.IsLeader == true;

    /// <summary>Gets. 選出・復旧・障害を利用者が区別するための状態です。</summary>
    public string Phase { get; private set; } = "Starting";

    /// <summary>Gets. このインスタンスが最後に取得したEpochです。</summary>
    public long Epoch => leader?.Epoch ?? 0;

    /// <summary>選出と実行を継続し、失敗時は新しい選出からやり直します。</summary>
    /// <param name="stoppingToken">ホスト終了の通知です。</param>
    /// <returns>サービスの終了まで継続するタスクです。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await storage.InitializeAsync(stoppingToken);
                StorageReady = true;
                await using var candidate = new LeaderCoordinator(storage, clock);
                leader = candidate;
                Phase = "Standby";
                if (await candidate.AcquireAsync(stoppingToken))
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, candidate.Lost);
                    try
                    {
                        Phase = "Recovering";
                        await engine.InitializeAsync(candidate, linked.Token);
                        Phase = engine.Namespace is null ? "AwaitingDefinition" : "Ready";
                        while (!linked.IsCancellationRequested)
                        {
                            await engine.TickAsync(linked.Token);
                            Phase = engine.Namespace is null ? "AwaitingDefinition" : "Ready";
                            await Task.Delay(TimeSpan.FromMilliseconds(250), clock, linked.Token);
                        }
                    }
                    finally
                    {
                        Phase = "Stopped";
                        await engine.StopAsync();
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Phase = "Faulted";
                logger.LogError(ex, "スケジューラーを停止しました。状態は初期化せず、次の選出で復旧します。");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5 + (Random.Shared.NextDouble() * 5)), clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
