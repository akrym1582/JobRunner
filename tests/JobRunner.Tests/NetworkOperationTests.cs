using System.Text;
using JobRunner.Models;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>子孫を含めた一時変更、実プロセス停止、明示リランを確認します。</summary>
public sealed class NetworkOperationTests
{
    /// <summary>100行を超えるネットを保留して即時化・復元し、取消を全子孫へ反映します。</summary>
    /// <returns>分割更新後の日時と終了状態を検証します。</returns>
    [Fact]
    public async Task MovesRestoresAndCancelsEntireLargeNetwork()
    {
        await using var scenario = new EngineScenario();
        var source = new StringBuilder("unit=net;{ty=n;ha=y;sd=2026/11/5;st=10:00;sh=no;");
        for (int i = 0; i < 105; i++)
        {
            source.Append($"unit=j{i};{{ty=j;te=\"true\";}}");
        }

        source.Append('}');
        await scenario.StartAsync(source.ToString());
        var root = Assert.Single(await scenario.OccurrencesAsync(), o => o.Id == o.RootId);
        await scenario.EnqueueAsync(new Operation { Id = "now", Kind = "RunNow", Target = root.Id });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.All(await scenario.OccurrencesAsync(), o => Assert.Equal(scenario.Clock.Now, o.Due));
        Assert.All(await scenario.OccurrencesAsync(), o => Assert.Equal(root.Due, o.OriginalDue));
        await scenario.EnqueueAsync(new Operation { Id = "restore", Kind = "Restore", Target = root.Id });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.All(await scenario.OccurrencesAsync(), o => Assert.Equal(root.Due, o.Due));
        await scenario.EnqueueAsync(new Operation { Id = "cancel", Kind = "Cancel", Target = root.Id });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.All(await scenario.OccurrencesAsync(), o => Assert.Equal("Cancelled", o.State));
        Assert.Empty(await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
        await scenario.RestartAsync();
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.All(await scenario.OccurrencesAsync(), o => Assert.Equal("Cancelled", o.State));
    }

    /// <summary>成功ネットのTOP再実行は新試行を作り、過去の終了記録を残します。</summary>
    /// <returns>ルートと子の再実行、および試行数を確認します。</returns>
    [Fact]
    public async Task TopRerunCreatesNewAttemptsForSuccessfulNetwork()
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=10:00;sh=no;unit=nest;{ty=n;unit=j;{ty=j;te=\"true\";}}}");
        var root = Assert.Single(await scenario.OccurrencesAsync(), o => o.Id == o.RootId);
        await scenario.EnqueueAsync(new Operation { Id = "now", Kind = "RunNow", Target = root.Id });
        await scenario.AdvanceUntilAsync(rows => rows.All(o => o.State == "Succeeded"));
        var original = StorageRepository.Read<JobAttempt>(Assert.Single(await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None)));
        await scenario.EnqueueAsync(new Operation { Id = "rerun", Kind = "Rerun", Target = root.Id, RerunMethod = "TOP" });
        await scenario.AdvanceUntilAsync(rows => rows.All(o => o.State == "Succeeded"));
        var attempts = (await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None)).Select(StorageRepository.Read<JobAttempt>).ToList();
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, a => Assert.Equal("Succeeded", a.State));
        Assert.Contains(attempts, a => a.Id == original.Id);
        Assert.NotEqual(original.Id, Assert.Single(await scenario.OccurrencesAsync(), o => o.UnitPath == "/net/nest/j").AttemptId);
    }

    /// <summary>ネット停止は未開始後続も止め、結果不明がある間のTOP再実行を拒否します。</summary>
    /// <returns>実プロセス停止と永続化された結果不明を確認します。</returns>
    [Fact]
    public async Task StopBlocksSuccessorsAndRejectsRerunUntilUnknownIsResolved()
    {
        string marker = Path.Combine(Path.GetTempPath(), "jobrunner-resolved-" + Guid.NewGuid().ToString("N"));
        await using var scenario = new EngineScenario();
        try
        {
            await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=09:00;sh=no;ar=(f=slow,t=after,seq);unit=slow;{ty=j;te=\"test -f '" + marker + "' || sleep 60\";}unit=after;{ty=j;te=\"true\";}}");
            await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.UnitPath == "/net/slow" && o.State == "Running"));
            var root = Assert.Single(await scenario.OccurrencesAsync(), o => o.Id == o.RootId);
            await scenario.EnqueueAsync(new Operation { Id = "stop", Kind = "Stop", Target = root.Id });
            await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.UnitPath == "/net/slow" && o.State == "Unknown"));
            var rows = await scenario.OccurrencesAsync();
            Assert.Equal("Interrupted", Assert.Single(rows, o => o.Id == root.Id).State);
            Assert.Equal("Cancelled", Assert.Single(rows, o => o.UnitPath == "/net/after").State);
            await scenario.EnqueueAsync(new Operation { Id = "rerun", Kind = "Rerun", Target = root.Id, RerunMethod = "TOP" });
            await scenario.Engine.TickAsync(CancellationToken.None);
            Assert.Equal("Rejected", StorageRepository.Read<Operation>((await scenario.Scope.Repository.GetAsync("command:" + StorageRepository.Hash("rerun"), CancellationToken.None))!).Status);
            Assert.Single(await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
            var unknown = Assert.Single(rows, o => o.State == "Unknown");
            await File.WriteAllTextAsync(marker, "停止後の業務確認が済んだテスト条件");
            await scenario.EnqueueAsync(new Operation { Id = "resolve", Kind = "Resolve", Target = unknown.Id, ExpectedState = "Unknown", AttemptId = unknown.AttemptId, Reason = "停止されたテストプロセスを確認済み", Resolution = "Failed" });
            await scenario.Engine.TickAsync(CancellationToken.None);
            await scenario.EnqueueAsync(new Operation { Id = "retry-resolved", Kind = "Rerun", Target = root.Id, RerunMethod = "TOP" });
            await scenario.AdvanceUntilAsync(values => values.All(o => o.State == "Succeeded"));
            var attempts = (await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None)).Select(StorageRepository.Read<JobAttempt>).ToList();
            Assert.Equal(3, attempts.Count);
            Assert.Equal("Unknown", Assert.Single(attempts, a => a.Id == unknown.AttemptId).State);
        }
        finally
        {
            File.Delete(marker);
        }
    }
}
