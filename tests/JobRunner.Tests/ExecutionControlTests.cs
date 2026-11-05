using JobRunner.Models;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>異常時の後続待機と明示再実行を、実プロセスと永続状態で確認します。</summary>
public sealed class ExecutionControlTests
{
    /// <summary>異常先行を再実行すると祖先が再開し、独立して成功したジョブは再実行しません。</summary>
    /// <returns>試行履歴とネット結果を検証します。</returns>
    [Fact]
    public async Task FailedNetworkResumesOnlyFailedBranchOnRerun()
    {
        string marker = Path.Combine(Path.GetTempPath(), "jobrunner-" + Guid.NewGuid().ToString("N"));
        await using var scenario = new EngineScenario();
        try
        {
            string source = $"unit=net;{{ty=n;sd=2026/11/5;st=09:00;sh=no;ar=(f=fail,t=after,seq);unit=fail;{{ty=j;te=\"test -f '{marker}'\";}}unit=after;{{ty=j;te=\"printf after\";}}unit=independent;{{ty=j;te=\"printf independent\";}}}}";
            await scenario.StartAsync(source);
            await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.UnitPath == "/net" && o.State == "Failed"));
            var rows = await scenario.OccurrencesAsync();
            Assert.Equal("Waiting", Assert.Single(rows, o => o.UnitPath == "/net/after").State);
            Assert.Equal("Succeeded", Assert.Single(rows, o => o.UnitPath == "/net/independent").State);
            var failed = Assert.Single(rows, o => o.UnitPath == "/net/fail");
            string oldAttempt = failed.AttemptId!;
            await File.WriteAllTextAsync(marker, "再実行を成功させるテスト専用条件");
            await scenario.EnqueueAsync(new Operation { Id = "retry", Kind = "Rerun", Target = failed.Id, ExpectedState = "Failed" });
            await scenario.AdvanceUntilAsync(values => values.All(o => o.State == "Succeeded"));
            var attempts = (await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None)).Select(StorageRepository.Read<JobAttempt>).ToList();
            Assert.Equal(4, attempts.Count);
            Assert.Equal("Failed", Assert.Single(attempts, a => a.Id == oldAttempt).State);
            Assert.Equal(2, attempts.Count(a => a.OccurrenceId == failed.Id));
            Assert.NotEqual(oldAttempt, Assert.Single(await scenario.OccurrencesAsync(), o => o.Id == failed.Id).AttemptId);
        }
        finally
        {
            File.Delete(marker);
        }
    }

    /// <summary>一時変更と保留を組み合わせても、元の確定日時を失わないことを確認します。</summary>
    /// <returns>操作完了と保存された日時を確認します。</returns>
    [Fact]
    public async Task TemporaryMoveRestoreAndHoldPreserveOriginalSchedule()
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=10:00;sh=no;}");
        var root = Assert.Single(await scenario.OccurrencesAsync());
        await scenario.EnqueueAsync(new Operation { Id = "hold", Kind = "Hold", Target = root.Id, ExpectedState = "Waiting" });
        await scenario.Engine.TickAsync(CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "move", Kind = "Move", Target = root.Id, ExpectedState = "Held", Due = root.Due.AddHours(5) });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var moved = Assert.Single(await scenario.OccurrencesAsync());
        Assert.Equal("Held", moved.State);
        Assert.Equal(root.Due, moved.OriginalDue);
        Assert.Equal(root.Due.AddHours(5), moved.Due);
        await scenario.EnqueueAsync(new Operation { Id = "restore", Kind = "Restore", Target = root.Id, ExpectedState = "Held" });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(root.Due, Assert.Single(await scenario.OccurrencesAsync()).Due);
    }

    /// <summary>解除・保留・日時変更を再起動後も保持し、自動補充が解除を復活させないことを確認します。</summary>
    /// <returns>保存済み登録と世代の復旧結果です。</returns>
    [Fact]
    public async Task RestartPreservesUnregistrationHoldAndOverride()
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=10:00;cy=(1,d);sh=no;}");
        string state = scenario.Engine.Namespace!;
        var root = Assert.Single(await scenario.OccurrencesAsync());
        var registration = StorageRepository.Read<Registration>(Assert.Single(await scenario.Scope.Repository.ListAsync("registration:", CancellationToken.None)));
        await scenario.EnqueueAsync(new Operation { Id = "hold", Kind = "Hold", Target = root.Id });
        await scenario.Engine.TickAsync(CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "move", Kind = "Move", Target = root.Id, Due = root.Due.AddHours(2) });
        await scenario.Engine.TickAsync(CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "unregister", Kind = "Unregister", Target = registration.Id });
        await scenario.Engine.TickAsync(CancellationToken.None);
        long previousEpoch = scenario.Leader.Epoch;
        await scenario.RestartAsync();
        Assert.True(scenario.Leader.Epoch > previousEpoch);
        Assert.Equal(state, scenario.Engine.Namespace);
        var restored = Assert.Single(await scenario.OccurrencesAsync());
        Assert.Equal("Held", restored.State);
        Assert.Equal(root.Due.AddHours(2), restored.Due);
        Assert.Equal(root.Due, restored.OriginalDue);
        scenario.Clock.Now = scenario.Clock.Now.AddDays(3);
        await scenario.EnqueueAsync(new Operation { Id = "release", Kind = "Release", Target = root.Id });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Waiting", Assert.Single(await scenario.OccurrencesAsync()).State);
        Assert.Empty(await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
        Assert.True(StorageRepository.Read<Registration>(Assert.Single(await scenario.Scope.Repository.ListAsync("registration:", CancellationToken.None))).Disabled);
    }
}
