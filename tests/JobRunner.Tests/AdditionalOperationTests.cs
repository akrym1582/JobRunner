using System.Text.Json;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>中断・監視・手動結果を実Storageと実プロセスで確認します。</summary>
public sealed class AdditionalOperationTests
{
    /// <summary>開始前に変更したコードを、新しい実プロセスの結果へ持ち越しません。</summary>
    /// <returns>新しい試行の終了コードが優先されることを確認します。</returns>
    [Fact]
    public async Task StartingNewAttemptClearsPreviousManualCode()
    {
        await using var s = new EngineScenario();
        await s.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=09:00;sh=no;unit=j;{ty=j;ha=y;te=\"true\";}}");
        await s.Engine.TickAsync(CancellationToken.None);
        var job = (await s.OccurrencesAsync()).Single(o => o.UnitPath == "/net/j");
        await s.EnqueueAsync(new Operation { Id = "code", Kind = "ChangeStatus", Target = job.Id, NewReturnCode = 7 });
        await s.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(7, (await s.OccurrencesAsync()).Single(o => o.Id == job.Id).ManualExitCode);
        await s.EnqueueAsync(new Operation { Id = "release", Kind = "Release", Target = job.Id });
        await s.AdvanceUntilAsync(o => o.Single(v => v.Id == job.Id).State == "Succeeded");
        job = (await s.OccurrencesAsync()).Single(o => o.Id == job.Id);
        Assert.Null(job.ManualExitCode);
        Assert.Equal(0, StorageRepository.Read<JobAttempt>((await s.Scope.Repository.GetAsync("attempt:" + job.AttemptId, CancellationToken.None))!).ExitCode);
    }

    /// <summary>ルート・上位・自ネットの基点と実開始からの所要時間を混同しません。</summary>
    [Fact]
    public void DelayReferencesAndElapsedTimeUseDifferentClocks()
    {
        var day = new DateOnly(2026, 11, 5);
        var basis = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        var root = new Occurrence { Id = "root", RootId = "root", UnitPath = "/root", Due = basis, BusinessDate = day };
        var parent = new Occurrence { Id = "parent", RootId = "root", UnitPath = "/root/parent", Due = basis.AddHours(1), BusinessDate = day };
        var child = new Occurrence { Id = "child", RootId = "root", UnitPath = "/root/parent/child", Due = basis.AddHours(2), BusinessDate = day, State = "Running", Started = true, StartedAt = basis.AddHours(2).AddMinutes(10), DelayMonitor = new() { Duration = true, DurationMinutes = 5 } };
        var options = new RunnerOptions { TimeZone = "UTC" };
        Occurrence[] all = [root, parent, child];
        Assert.Equal(basis.AddMinutes(5), DelayMonitoring.Limit(child, "ROOT", 5, all, options));
        Assert.Equal(basis.AddHours(1).AddMinutes(5), DelayMonitoring.Limit(child, "TOP", 5, all, options));
        Assert.Equal(basis.AddHours(2).AddMinutes(5), DelayMonitoring.Limit(child, "OWN", 5, all, options));
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 1, 0, 0, TimeSpan.Zero), DelayMonitoring.Limit(child, "ABSOLUTE", 1500, all, options));
        Assert.False(DelayMonitoring.Evaluate(child, all, options, basis.AddHours(2).AddMinutes(14)).End);
        Assert.True(DelayMonitoring.Evaluate(child, all, options, basis.AddHours(2).AddMinutes(15)).End);
        child.FinishedAt = basis.AddHours(2).AddMinutes(14);
        Assert.False(DelayMonitoring.Evaluate(child, all, options, basis.AddDays(1)).End);
    }

    /// <summary>中断待ちの稼働系消失を成功と推測せず、手動解決まで待ちます。</summary>
    /// <returns>Unknownと後続抑止を確認します。</returns>
    [Fact]
    public async Task InterruptedRecoveryWaitsForExplicitUnknownResolution()
    {
        await using var s = new EngineScenario();
        await s.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=09:00;sh=no;ar=(f=j,t=last,seq);unit=j;{ty=j;te=\"sleep 60\";}unit=last;{ty=j;te=\"true\";}}");
        await s.AdvanceUntilAsync(o => o.Any(v => v.UnitPath == "/net/j" && v.State == "Running"));
        var root = (await s.OccurrencesAsync()).Single(o => o.Id == o.RootId);
        await s.EnqueueAsync(new Operation { Id = "interrupt", Kind = "Interrupt", Target = root.Id });
        await s.Engine.TickAsync(CancellationToken.None);
        await s.RestartAsync();
        await s.Engine.TickAsync(CancellationToken.None);
        var rows = await s.OccurrencesAsync();
        var unknown = rows.Single(o => o.UnitPath == "/net/j");
        Assert.Equal("Unknown", unknown.State);
        Assert.Equal("Running", rows.Single(o => o.Id == root.Id).State);
        await s.EnqueueAsync(new Operation { Id = "no-shortcut", Kind = "ChangeStatus", Target = unknown.Id, AttemptId = unknown.AttemptId, NewStatus = "NORMAL" });
        await s.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Unknown", (await s.OccurrencesAsync()).Single(o => o.Id == unknown.Id).State);
        await s.EnqueueAsync(new Operation { Id = "resolve", Kind = "Resolve", Target = unknown.Id, AttemptId = unknown.AttemptId, ExpectedState = "Unknown", Resolution = "Failed", Reason = "試験で停止と業務結果を確認" });
        await s.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Interrupted", (await s.OccurrencesAsync()).Single(o => o.Id == root.Id).State);
        Assert.Single(await s.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
    }

    /// <summary>中断は稼働プロセスを完了させ、後続を起動せず、TOPで再開できます。</summary>
    /// <returns>元試行の成功と中断復旧を確認します。</returns>
    [Fact]
    public async Task InterruptDrainsRunningJobAndSuppressesSuccessors()
    {
        string gate = Path.Combine(Path.GetTempPath(), "interrupt-" + Guid.NewGuid().ToString("N"));
        await using var s = new EngineScenario();
        try
        {
            await s.StartAsync($"unit=net;{{ty=n;sd=2026/11/5;st=09:00;sh=no;ar=(f=first,t=last,seq);unit=first;{{ty=j;te=\"while [ ! -f '{gate}' ]; do sleep 0.05; done\";}}unit=last;{{ty=j;te=\"true\";}}}}");
            await s.AdvanceUntilAsync(o => o.Any(v => v.UnitPath == "/net/first" && v.State == "Running"));
            var root = (await s.OccurrencesAsync()).Single(o => o.Id == o.RootId);
            await s.EnqueueAsync(new Operation { Id = "interrupt", Kind = "Interrupt", Target = root.Id });
            await s.Engine.TickAsync(CancellationToken.None);
            var rows = await s.OccurrencesAsync();
            Assert.Equal("Running", rows.Single(o => o.UnitPath == "/net/first").State);
            Assert.Equal("Running", rows.Single(o => o.Id == root.Id).State);
            Assert.Equal("NotScheduled", rows.Single(o => o.UnitPath == "/net/last").State);
            await File.WriteAllTextAsync(gate, "finish");
            await s.AdvanceUntilAsync(o => o.Single(v => v.Id == root.Id).State == "Interrupted");
            Assert.Equal("Succeeded", (await s.OccurrencesAsync()).Single(o => o.UnitPath == "/net/first").State);
            Assert.Single(await s.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
            await s.RestartAsync();
            await s.Engine.TickAsync(CancellationToken.None);
            Assert.Equal("Interrupted", (await s.OccurrencesAsync()).Single(o => o.Id == root.Id).State);
            await s.EnqueueAsync(new Operation { Id = "resume", Kind = "Rerun", Target = root.Id, RerunMethod = "TOP" });
            await s.AdvanceUntilAsync(o => o.All(v => v.State == "Succeeded"));
            Assert.Equal(3, (await s.Scope.Repository.ListAsync("attempt:", CancellationToken.None)).Count);
        }
        finally
        {
            File.Delete(gate);
        }
    }

    /// <summary>監視の相対基準、時刻境界、省略維持と復旧を確認します。</summary>
    /// <param name="mode">監視方式です。</param>
    /// <param name="minutes">設定する分数です。</param>
    /// <returns>開始/終了の遅延と永続設定を確認します。</returns>
    [Theory]
    [InlineData("OWN", 1)]
    [InlineData("ABSOLUTE", 601)]
    public async Task DelaySettingsSurviveRestart(string mode, int minutes)
    {
        await using var s = new EngineScenario();
        await s.StartAsync("unit=net;{ty=n;ha=y;sd=2026/11/5;st=10:00;sh=no;}");
        var root = Assert.Single(await s.OccurrencesAsync());
        await s.EnqueueAsync(new Operation { Id = "monitor", Kind = "ChangeDelayMonitor", Target = root.Id, DelayMonitor = new() { Start = mode, StartMinutes = minutes, End = "OWN", EndMinutes = 2 } });
        await s.Engine.TickAsync(CancellationToken.None);
        await s.RestartAsync();
        root = Assert.Single(await s.OccurrencesAsync());
        Assert.Equal(mode, root.DelayMonitor!.Start);
        Assert.Equal((false, false), DelayMonitoring.Evaluate(root, [root], s.Scope.Options, root.Due.AddSeconds(59)));
        Assert.Equal((true, false), DelayMonitoring.Evaluate(root, [root], s.Scope.Options, root.Due.AddMinutes(1)));
        Assert.Equal((true, true), DelayMonitoring.Evaluate(root, [root], s.Scope.Options, root.Due.AddMinutes(2)));
        await s.EnqueueAsync(new Operation { Id = "disable-start", Kind = "ChangeDelayMonitor", Target = root.Id, DelayMonitor = new() { Start = "NOT" } });
        await s.Engine.TickAsync(CancellationToken.None);
        root = Assert.Single(await s.OccurrencesAsync());
        Assert.Equal((false, true), DelayMonitoring.Evaluate(root, [root], s.Scope.Options, root.Due.AddMinutes(2)));
        string before = JsonSerializer.Serialize(root.DelayMonitor);
        await s.EnqueueAsync(new Operation { Id = "invalid-root", Kind = "ChangeDelayMonitor", Target = root.Id, DelayMonitor = new() { Start = "ROOT", StartMinutes = 1 } });
        await s.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(before, JsonSerializer.Serialize(Assert.Single(await s.OccurrencesAsync()).DelayMonitor));
        Assert.Equal("Rejected", StorageRepository.Read<Operation>((await s.Scope.Repository.GetAsync("command:" + StorageRepository.Hash("invalid-root"), CancellationToken.None))!).Status);
    }

    /// <summary>手動コードは後続判定へ反映し、OSが返した過去の終了コードを保存します。</summary>
    /// <returns>状態変更前の試行と新しい判定を確認します。</returns>
    [Fact]
    public async Task ManualResultPreservesAttemptAndReachesJudgment()
    {
        await using var s = new EngineScenario();
        await s.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=09:00;sh=no;ar=(f=j,t=judge,seq);unit=j;{ty=j;te=\"exit 4\";}unit=judge;{ty=jdj;ej=eq;ejc=0;}unit=held;{ty=j;ha=y;te=\"true\";}}");
        await s.AdvanceUntilAsync(o => o.Any(v => v.UnitPath == "/net/j" && v.State == "Failed"));
        var job = (await s.OccurrencesAsync()).Single(o => o.UnitPath == "/net/j");
        await s.EnqueueAsync(new Operation { Id = "manual", Kind = "ChangeStatus", Target = job.Id, AttemptId = job.AttemptId, ExpectedState = "Failed", NewStatus = "NORMAL", NewReturnCode = 0 });
        await s.AdvanceUntilAsync(o => o.Single(v => v.UnitPath == "/net/judge").State == "Succeeded");
        Assert.True((await s.OccurrencesAsync()).Single(o => o.UnitPath == "/net/judge").ConditionMatched);
        Assert.Equal(4, StorageRepository.Read<JobAttempt>((await s.Scope.Repository.GetAsync("attempt:" + job.AttemptId, CancellationToken.None))!).ExitCode);
        await s.RestartAsync();
        Assert.Equal(0, (await s.OccurrencesAsync()).Single(o => o.Id == job.Id).ManualExitCode);
    }
}
