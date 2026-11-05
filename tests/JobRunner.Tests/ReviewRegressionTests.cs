using Azure.Data.Tables;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>独立レビューで再現した復旧・採用・予定計算・手動解決の境界を検証します。</summary>
public sealed class ReviewRegressionTests
{
    /// <summary>保存済みの先読み範囲を超えて停止しても、補充した過去予定を追跡実行しません。</summary>
    /// <param name="futureCount">件数方式の場合の枠です。nullは日数方式です。</param>
    /// <returns>復旧後の最初のTickと再復旧で、未来から再開することを確認します。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task RecoveryDoesNotLaunchBackfilledPastGenerations(int? futureCount)
    {
        await using var scenario = new EngineScenario();
        scenario.Scope.Options.ConfirmedFutureGenerations = futureCount;
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=10:00;cy=(1,d);sh=no;unit=j;{ty=j;te=\"true\";}}");
        scenario.Clock.Now = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        await scenario.RestartAsync();
        await scenario.Engine.TickAsync(CancellationToken.None);
        await scenario.Engine.TickAsync(CancellationToken.None);
        var roots = (await scenario.OccurrencesAsync()).Where(o => o.Id == o.RootId).ToList();
        Assert.DoesNotContain(roots, o => o.Due < scenario.Clock.Now && o.Started);
        Assert.Empty(await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
        var next = roots.Where(o => o.State == "Waiting").OrderBy(o => o.Due).First();
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 1, 0, 0, TimeSpan.Zero), next.Due);
        await scenario.RestartAsync();
        scenario.Clock.Now = next.Due;
        await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.Id == next.Id && o.State == "Succeeded"));
        Assert.Single(await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
    }

    /// <summary>プロセスがなくても実行中ネットの保留子を残して定義を切り替えません。</summary>
    /// <returns>切替拒否後も元の保留を解除して完了できることを確認します。</returns>
    [Fact]
    public async Task ActivationRejectsRunningNetworkWithHeldChild()
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=net;{ty=n;sd=0,ud;unit=j;{ty=j;ha=y;te=\"true\";}}");
        await scenario.EnqueueAsync(new Operation { Id = "start", Kind = "Register", Target = "/net", RegistrationKind = "Immediate", Due = scenario.Clock.Now });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var root = Assert.Single(await scenario.OccurrencesAsync(), o => o.Id == o.RootId);
        var child = Assert.Single(await scenario.OccurrencesAsync(), o => o.Id != o.RootId);
        Assert.Equal("Running", root.State);
        Assert.Equal("Held", child.State);
        string originalNamespace = scenario.Engine.Namespace!;
        var definitions = new DefinitionService(scenario.Scope.Repository, scenario.Scope.Options);
        var source = new SourceEvidence
        {
            Mapping = new DefinitionSource { BlobPath = "new/jobs" },
            Base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("unit=net;{ty=n;sd=0,ud;unit=j;{ty=j;te=\"echo changed\";}}")),
        };
        var bundle = definitions.Validate([source]);
        await definitions.SaveAsync(bundle, CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "activate", Kind = "Activate", Target = bundle.Hash });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var operation = StorageRepository.Read<Operation>((await scenario.Scope.Repository.GetAsync("command:" + StorageRepository.Hash("activate"), CancellationToken.None))!);
        Assert.Equal("Rejected", operation.Status);
        Assert.Equal(originalNamespace, scenario.Engine.Namespace);
        await scenario.EnqueueAsync(new Operation { Id = "release", Kind = "Release", Target = child.Id });
        await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.Id == root.Id && o.State == "Succeeded"));
        await scenario.EnqueueAsync(new Operation { Id = "activate-after-completion", Kind = "Activate", Target = bundle.Hash });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.NotEqual(originalNamespace, scenario.Engine.Namespace);
    }

    /// <summary>開始月に指定日がなくても月・年サイクルの位相を維持して後の有効日を生成します。</summary>
    /// <param name="schedule">開始日とサイクルの指定です。</param>
    /// <param name="anchor">登録基準日です。</param>
    /// <param name="until">計算範囲の終端です。</param>
    /// <param name="expected">生成すべき業務日の一覧です。</param>
    [Theory]
    [InlineData("sd=31;cy=(1,m);", "2026-11-05", "2027-02-01", "2026-12-31,2027-01-31")]
    [InlineData("sd=31;cy=(2,m);", "2026-11-05", "2027-02-01", "2027-01-31")]
    [InlineData("sd=2/29;cy=(1,y);", "2025-02-01", "2029-01-01", "2028-02-29")]
    public void MissingStartDayDoesNotEraseLaterCycles(string schedule, string anchor, string until, string expected)
    {
        var units = Jp1DefinitionParser.Parse($"unit=net;{{ty=n;{schedule}st=10:00;sh=no;}}", new DefinitionSource());
        var options = new RunnerOptions();
        DefinitionValidator.Validate(units, options);
        var start = DateOnly.Parse(anchor);
        var points = ScheduleCalculator.Calculate(units[0], units.ToDictionary(u => u.Path), new Registration { Anchor = start }, start, DateOnly.Parse(until), options).ToList();
        Assert.Equal(expected.Split(',').Select(DateOnly.Parse), points.Select(p => p.BusinessDate));
    }

    /// <summary>手動解決を世代と試行へ一括保存し、期限後は未解決扱いせず履歴を退避します。</summary>
    /// <param name="resolution">確認済みの業務結果です。</param>
    /// <returns>再起動後のAPI相当の試行情報と履歴退避を検証します。</returns>
    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    public async Task ResolutionCompletesAttemptAndAllowsHistoryArchive(string resolution)
    {
        await using var scenario = new EngineScenario();
        scenario.Scope.Options.HistoryArchiveAfterDays = 1;
        scenario.Scope.Options.LogRetentionDays = 1;
        await scenario.StartAsync("unit=net;{ty=n;sd=0,ud;unit=j;{ty=j;te=\"true\";}}");
        await scenario.EnqueueAsync(new Operation { Id = "start", Kind = "Register", Target = "/net", RegistrationKind = "Immediate", Due = scenario.Clock.Now, Holding = true });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var storage = scenario.Scope.Repository;
        var root = Assert.Single(await scenario.OccurrencesAsync(), o => o.Id == o.RootId);
        var child = Assert.Single(await scenario.OccurrencesAsync(), o => o.Id != o.RootId);
        var row = (await storage.GetAsync("occurrence:" + child.Id, CancellationToken.None))!;
        child.State = "Unknown";
        child.Started = true;
        child.AttemptId = "unknown-attempt";
        await storage.Table.UpdateEntityAsync(storage.Entity(row.RowKey, child), row.ETag, TableUpdateMode.Replace);
        await storage.Table.AddEntityAsync(storage.Entity("attempt:" + child.AttemptId, new JobAttempt { Id = child.AttemptId, OccurrenceId = child.Id, State = "Unknown" }));
        string logPath = $"logs/{child.Id}/{child.AttemptId}/stdout/00000000.txt";
        await storage.PutImmutableAsync(logPath, BinaryData.FromString("partial output"), CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "resolve", Kind = "Resolve", Target = child.Id, ExpectedState = "Unknown", AttemptId = child.AttemptId, Reason = "業務結果を照合済み", Resolution = resolution });
        await scenario.Engine.TickAsync(CancellationToken.None);
        await scenario.RestartAsync();
        var attempt = StorageRepository.Read<JobAttempt>((await storage.GetAsync("attempt:" + child.AttemptId, CancellationToken.None))!);
        Assert.Equal(resolution, attempt.State);
        Assert.Equal(scenario.Clock.Now, attempt.FinishedAt);
        Assert.Null(attempt.ExitCode);
        Assert.False(attempt.LogsComplete);
        await scenario.EnqueueAsync(new Operation { Id = "release", Kind = "Release", Target = root.Id });
        await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.Id == root.Id && o.State == resolution));
        scenario.Clock.Now = scenario.Clock.Now.AddDays(2);
        await scenario.Engine.TickAsync(CancellationToken.None);
        var archived = Assert.Single(await scenario.OccurrencesAsync());
        Assert.Equal("Archived", archived.State);
        Assert.Empty(await storage.ListAsync("attempt:", CancellationToken.None));
        Assert.False((await storage.Container.GetBlobClient(logPath).ExistsAsync()).Value);
        var data = await storage.Container.GetBlobClient(archived.ArchivePath).DownloadContentAsync();
        var history = data.Value.Content.ToObjectFromJson<HistoryArchive>()!;
        var saved = Assert.Single(history.Rows, r => r.Key == "attempt:" + child.AttemptId);
        Assert.Equal(resolution, System.Text.Json.JsonSerializer.Deserialize<JobAttempt>(saved.Json)!.State);
    }
}
