using JobRunner.Models;
using JobRunner.Storage;

namespace JobRunner.Tests;

/// <summary>各テストに専用Tableとコンテナーを作り、他の並行試験へ影響させません。</summary>
internal sealed class StorageScope : IAsyncDisposable
{
    /// <summary>Initializes a new instance of the <see cref="StorageScope"/> class. 名前空間をテストごとに分離します。</summary>
    public StorageScope()
    {
        string suffix = Guid.NewGuid().ToString("N");
        Options = new RunnerOptions { ManagementContainer = "test-" + suffix, TableName = "Test" + suffix, SchedulerId = suffix };

        // 共有エミュレーターと競合しない専用ポートでの検証も可能にします。
        if (Environment.GetEnvironmentVariable("JOBRUNNER_TEST_CONNECTION_STRING") is { Length: > 0 } connectionString)
        {
            Options.ConnectionString = connectionString;
        }

        Repository = new StorageRepository(Options);
    }

    /// <summary>Gets. この試験だけが使用するStorage設定です。</summary>
    public RunnerOptions Options { get; }

    /// <summary>Gets. 本番と同じSDK経路でAzuriteへ接続するアダプターです。</summary>
    public StorageRepository Repository { get; }

    /// <summary>試験が作成したコンテナーとTableだけを削除します。</summary>
    /// <returns>後片付けの完了を表します。</returns>
    public async ValueTask DisposeAsync()
    {
        await Repository.Container.DeleteIfExistsAsync();
        await Repository.Table.DeleteAsync();
    }
}
