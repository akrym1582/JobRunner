using JobRunner.Models;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>再実行範囲と過去試行の保持を検証します。</summary>
public sealed class SelectiveRerunTests
{
    /// <summary>後続から再開する場合は異常ジョブを再起動せず、元の結果を残します。</summary>
    /// <param name="method">ルートまたはジョブ対象の方式です。</param>
    /// <returns>実プロセスの試行数を確認します。</returns>
    [Theory]
    [InlineData("ABNORMAL_NEXT")]
    [InlineData("NEXT")]
    public async Task NextRunsSuccessorWithoutRepeatingFailedJob(string method)
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=09:00;sh=no;ar=(f=a,t=b);unit=a;{ty=j;te=\"exit 1\";}unit=b;{ty=j;te=\"true\";}}");
        await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.UnitPath == "/net" && o.State == "Failed"));
        var rows = await scenario.OccurrencesAsync();
        var target = Assert.Single(rows, o => o.UnitPath == (method == "NEXT" ? "/net/a" : "/net"));
        await scenario.EnqueueAsync(new Operation { Id = "rerun", Kind = "Rerun", Target = target.Id, RerunMethod = method });
        await scenario.AdvanceUntilAsync(current => current.Any(o => o.UnitPath == "/net/b" && o.State == "Succeeded"));
        Assert.Equal(2, (await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None)).Count);
        Assert.Equal("Failed", Assert.Single(await scenario.OccurrencesAsync(), o => o.UnitPath == "/net/a").State);
    }

    /// <summary>各選択方式が対象の試行だけを追加し、過去の試行を残します。</summary>
    /// <param name="method">選択再実行の方式です。</param>
    /// <returns>方式別の実行結果と試行履歴を確認します。</returns>
    [Theory]
    [InlineData("ABNORMAL_JOB")]
    [InlineData("ABNORMAL_JOBNET")]
    [InlineData("WARNING")]
    [InlineData("FROM")]
    public async Task SelectiveMethodsPreserveAttemptHistory(string method)
    {
        await using var scenario = new EngineScenario();
        string threshold = method == "WARNING" ? "wth=0;tho=2;" : string.Empty;
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=09:00;sh=no;unit=n;{ty=n;unit=j;{ty=j;" + threshold + "te=\"exit 1\";}}}");
        await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.UnitPath == "/net" && o.State is "Warning" or "Failed"));
        var job = Assert.Single(await scenario.OccurrencesAsync(), o => o.UnitPath == "/net/n/j");
        var target = method == "FROM" ? job : Assert.Single(await scenario.OccurrencesAsync(), o => o.UnitPath == "/net");
        await scenario.EnqueueAsync(new Operation { Id = "selected", Kind = "Rerun", Target = target.Id, RerunMethod = method });
        await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.Id == job.Id && o.AttemptId != job.AttemptId && o.State is "Warning" or "Failed"));
        Assert.Equal(2, (await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None)).Count);
    }

    /// <summary>単独再実行は上位の終了状態と後続の未開始状態を維持します。</summary>
    /// <returns>二度目の試行と後続未実行を確認します。</returns>
    [Fact]
    public async Task OnlyDoesNotReleaseSuccessor()
    {
        await using var scenario = new EngineScenario();
        string marker = Path.Combine(Path.GetTempPath(), "jobrunner-only-" + Guid.NewGuid().ToString("N"));
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=09:00;sh=no;ar=(f=a,t=b);unit=a;{ty=j;te=\"test -f MARKER\";}unit=b;{ty=j;te=\"true\";}}".Replace("MARKER", marker, StringComparison.Ordinal));
        await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.UnitPath == "/net" && o.State == "Failed"));
        var target = Assert.Single(await scenario.OccurrencesAsync(), o => o.UnitPath == "/net/a");
        await File.WriteAllTextAsync(marker, "ready");
        await scenario.EnqueueAsync(new Operation { Id = "only", Kind = "Rerun", Target = target.Id, RerunMethod = "ONLY" });
        await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.Id == target.Id && o.State == "Succeeded" && o.AttemptId != target.AttemptId));
        Assert.Equal("Failed", Assert.Single(await scenario.OccurrencesAsync(), o => o.UnitPath == "/net").State);
        Assert.Equal("Waiting", Assert.Single(await scenario.OccurrencesAsync(), o => o.UnitPath == "/net/b").State);
        Assert.Equal(2, (await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None)).Count);
        File.Delete(marker);
    }
}
