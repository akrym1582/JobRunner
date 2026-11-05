using JobRunner.Execution;
using JobRunner.Models;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>安全なローカルコマンドで終了判定と出力障害時の停止を確認します。</summary>
public sealed class ExecutionTests
{
    /// <summary>V12の警告・異常しきい値に応じて実際の終了コードを分類します。</summary>
    /// <param name="code">テストプロセスが返す終了コードです。</param>
    /// <param name="expected">独立して指定した期待状態です。</param>
    /// <returns>出力をStorageへ保存した実行結果の検証です。</returns>
    [Theory]
    [InlineData(0, "Succeeded")]
    [InlineData(3, "Warning")]
    [InlineData(7, "Failed")]
    public async Task ClassifiesActualExitCodes(int code, string expected)
    {
        string suffix = Guid.NewGuid().ToString("N");
        var storage = new StorageRepository(new RunnerOptions { ManagementContainer = "test-" + suffix, TableName = "Test" + suffix });
        await storage.InitializeAsync(CancellationToken.None);
        try
        {
            var unit = new UnitDefinition { Attributes = new() { ["te"] = [$"printf output; printf error >&2; exit {code}"], ["tho"] = ["5"], ["wth"] = ["0"] } };
            var executor = new ProcessJobExecutor(storage);
            var attempt = await executor.ExecuteAsync(unit, new JobAttempt { Id = "attempt", OccurrenceId = "execution" }, () => { }, _ => Task.CompletedTask, CancellationToken.None);
            Assert.Equal(expected, attempt.State);
            Assert.Equal(code, attempt.ExitCode);
            Assert.True(attempt.LogsComplete);
        }
        finally
        {
            await storage.Container.DeleteIfExistsAsync();
            await storage.Table.DeleteAsync();
        }
    }

    /// <summary>ログ保存先が失われても、出力パイプ詰まりで無期限に待たないことを確認します。</summary>
    /// <returns>10秒以内に実行器が異常を返すことを確認します。</returns>
    [Fact]
    public async Task LogStorageFailureStopsProducer()
    {
        string suffix = Guid.NewGuid().ToString("N");
        var storage = new StorageRepository(new RunnerOptions { ManagementContainer = "missing-" + suffix, TableName = "Test" + suffix });
        var unit = new UnitDefinition { Attributes = new() { ["te"] = ["while true; do printf 'bounded output line\n'; done"] } };
        var attempt = new JobAttempt { Id = "attempt", OccurrenceId = "execution" };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<Exception>(() => new ProcessJobExecutor(storage).ExecuteAsync(unit, attempt, () => { }, _ => Task.CompletedTask, timeout.Token));
        Assert.False(timeout.IsCancellationRequested);
        Assert.NotNull(attempt.ProcessId);
        Assert.False(attempt.LogsComplete);
    }
}
