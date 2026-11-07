using System.Text;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>登録の明示条件と、削除ルートに残る保護世代を検証します。</summary>
public sealed class PublicationPolicyTests
{
    /// <summary>再計算後も登録期間・固定日時・間隔・最初の明示保留を維持します。</summary>
    /// <param name="kind">既存エンジンの確定方式です。</param>
    /// <returns>定義反映後の登録と世代の検証です。</returns>
    [Theory]
    [InlineData("count")]
    [InlineData("period")]
    [InlineData("fixed")]
    [InlineData("interval")]
    public async Task PreservesExplicitRegistrationTermsAndInitialHolding(string kind)
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=net;{ty=n;sd=1,en;st=10:00;cy=1,(1,d);sh=no;unit=j;{ty=j;te=\"true\";}}");
        await scenario.EnqueueAsync(new Operation { Id = "pause", Kind = "PauseAll", ExpectedControlRevision = 0 });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var request = new Operation { Id = "explicit", Kind = "Register", Target = "/net", RegistrationKind = "Confirmed", Holding = true, PassedDaemonStarts = "NEXTTIME" };
        if (kind == "count")
        {
            request.FutureGenerations = 2;
        }
        else if (kind == "period")
        {
            request.Start = new(2026, 11, 5);
            request.End = new(2026, 11, 7);
        }
        else
        {
            request.FixedDue = scenario.Clock.Now.AddHours(1);
            if (kind == "interval")
            {
                request.IntervalMinutes = 30;
                request.IntervalUntil = scenario.Clock.Now.AddHours(3);
            }
        }

        await scenario.EnqueueAsync(request);
        await scenario.Engine.TickAsync(CancellationToken.None);
        var storage = scenario.Scope.Repository;
        string key = $"registration:{scenario.Engine.Namespace}:request-{StorageRepository.Hash(request.Id)}";
        var before = StorageRepository.Read<Registration>((await storage.GetAsync(key, CancellationToken.None))!);
        string hash = await SaveAsync(scenario, "unit=net;{ty=n;sd=1,en;st=11:00;cy=1,(1,d);sh=no;unit=j;{ty=j;te=\"printf new\";}}");
        await scenario.EnqueueAsync(new Operation { Id = "publish", Kind = "Activate", Target = hash });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var after = StorageRepository.Read<Registration>((await storage.GetAsync(key, CancellationToken.None))!);
        Assert.Equal(before.Anchor, after.Anchor);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.Start, after.Start);
        Assert.Equal(before.End, after.End);
        Assert.Equal(before.FixedDue, after.FixedDue);
        Assert.Equal(before.IntervalMinutes, after.IntervalMinutes);
        Assert.Equal(before.IntervalUntil, after.IntervalUntil);
        Assert.Equal(before.Holding, after.Holding);
        Assert.Equal(before.FutureGenerations, after.FutureGenerations);
        var roots = (await scenario.OccurrencesAsync()).Where(o => o.Id == o.RootId && o.RegistrationId == after.Id && o.DefinitionHash == hash).OrderBy(o => o.Due).ToList();
        Assert.NotEmpty(roots);
        Assert.Equal("Held", roots[0].State);
        Assert.True(roots[0].HoldOverride);
        Assert.All(roots.Skip(1), root => Assert.Equal("Waiting", root.State));
        Assert.Empty(await storage.ListAsync("attempt:", CancellationToken.None));
    }

    /// <summary>削除したルートも保護した実行回は旧版で完了でき、通常予定の補充は止まります。</summary>
    /// <returns>削除・再開・再起動の検証です。</returns>
    [Fact]
    public async Task RemovedRootRetainsOperatedGenerationAndRejectsStaleRegistration()
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=old;{ty=n;sd=1,en;st=10:00;cy=1,(1,d);sh=no;unit=j;{ty=j;te=\"true\";}}");
        var root = (await scenario.OccurrencesAsync()).First(o => o.Id == o.RootId && o.State == "Waiting");
        await scenario.EnqueueAsync(new Operation { Id = "hold", Kind = "Hold", Target = root.Id });
        await scenario.Engine.TickAsync(CancellationToken.None);
        string hash = await SaveAsync(scenario, "unit=next;{ty=n;sd=0,ud;ha=y;}");
        await scenario.EnqueueAsync(new Operation { Id = "publish", Kind = "Activate", Target = hash });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var storage = scenario.Scope.Repository;
        var registration = StorageRepository.Read<Registration>((await storage.GetAsync($"registration:{scenario.Engine.Namespace}:{root.RegistrationId}", CancellationToken.None))!);
        Assert.True(registration.ReplenishmentStopped);
        Assert.False(registration.Disabled);
        scenario.Clock.Now = root.Due;
        await scenario.EnqueueAsync(new Operation { Id = "release", Kind = "Release", Target = root.Id });
        await scenario.AdvanceUntilAsync(values => values.Any(o => o.Id == root.Id && o.State == "Succeeded"));
        await scenario.EnqueueAsync(new Operation { Id = "old-request", Kind = "Register", Target = "/next", AcceptedDefinitionRevision = 0, RegistrationKind = "Immediate", Due = scenario.Clock.Now });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Rejected", StorageRepository.Read<Operation>((await storage.GetAsync("command:" + StorageRepository.Hash("old-request"), CancellationToken.None))!).Status);
        var ids = (await scenario.OccurrencesAsync()).Select(o => o.Id).Order().ToList();
        await scenario.RestartAsync();
        scenario.Clock.Now = scenario.Clock.Now.AddDays(10);
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(ids, (await scenario.OccurrencesAsync()).Select(o => o.Id).Order().ToList());
    }

    /// <summary>初回の明示保留は、置換後の世代を消化してから再反映しても後続へ移りません。</summary>
    /// <param name="period">期間方式で検査する場合trueです。</param>
    /// <returns>再反映と取消済み原予定枠の非復活を検証します。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DoesNotReapplyConsumedInitialHolding(bool period)
    {
        await using var scenario = new EngineScenario();
        const string source = "unit=net;{ty=n;sd=1,en;st=10:00;cy=1,(1,d);sh=no;}";
        await scenario.StartAsync(source);
        await scenario.EnqueueAsync(new Operation { Id = "pause", Kind = "PauseAll", ExpectedControlRevision = 0 });
        await scenario.Engine.TickAsync(CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "explicit", Kind = "Register", Target = "/net", RegistrationKind = "Confirmed", Holding = true, FutureGenerations = period ? null : 2, Start = period ? new(2026, 11, 5) : null, End = period ? new(2026, 11, 8) : null });
        await scenario.Engine.TickAsync(CancellationToken.None);
        string registration = "request-" + StorageRepository.Hash("explicit");
        string firstHash = await SaveAsync(scenario, source.Replace("10:00", "11:00", StringComparison.Ordinal));
        await scenario.EnqueueAsync(new Operation { Id = "first", Kind = "Activate", Target = firstHash });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var first = (await scenario.OccurrencesAsync()).Where(o => o.Id == o.RootId && o.RegistrationId == registration && o.State != "Superseded").OrderBy(o => o.OriginalDue).First();
        Assert.Equal("Held", first.State);
        await scenario.EnqueueAsync(new Operation { Id = "cancel", Kind = "Cancel", Target = first.Id });
        await scenario.Engine.TickAsync(CancellationToken.None);
        string secondHash = await SaveAsync(scenario, source.Replace("10:00", "12:00", StringComparison.Ordinal));
        await scenario.EnqueueAsync(new Operation { Id = "second", Kind = "Activate", Target = secondHash });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var roots = (await scenario.OccurrencesAsync()).Where(o => o.Id == o.RootId && o.RegistrationId == registration && o.DefinitionHash == secondHash).ToList();
        Assert.NotEmpty(roots);
        Assert.All(roots, o => Assert.Equal("Waiting", o.State));
        Assert.DoesNotContain(roots, o => o.BusinessDate == first.BusinessDate);
    }

    /// <summary>確認後に予定時刻を越えただけでも置換可否の差を検出します。</summary>
    /// <returns>ETagが変わらない時刻経過の確認です。</returns>
    [Fact]
    public async Task PreviewDetectsTimeBasedProtectionChange()
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=10:00;sh=no;}");
        var storage = scenario.Scope.Repository;
        var active = await storage.GetAsync("definition-active", CancellationToken.None);
        var registrations = await storage.ListAsync($"registration:{scenario.Engine.Namespace}:", CancellationToken.None);
        var occurrences = await storage.ListAsync("occurrence:", CancellationToken.None);
        var root = Assert.Single(await scenario.OccurrencesAsync());
        string hash = DefinitionPublication.ImpactHash(active, registrations, occurrences, root.Due);
        Assert.NotEqual(hash, DefinitionPublication.ImpactHash(active, registrations, occurrences, root.Due.AddTicks(1)));
    }

    /// <summary>保存不可能な完全名を反映開始前に拒否し、現在の運用定義を維持します。</summary>
    /// <param name="nameLength">名前だけで上限を超える場合と、世代メタデータを含めて超える境界です。</param>
    /// <returns>部分反映でFaultedになる前に操作を拒否できることを確認します。</returns>
    [Theory]
    [InlineData(6000)]
    [InlineData(2470)]
    public async Task RejectsOversizedOperationalPathsBeforeChangingActiveDefinition(int nameLength)
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=net;{ty=n;sd=0,ud;}");
        var storage = scenario.Scope.Repository;
        string before = (await storage.GetAsync("definition-active", CancellationToken.None))!.GetString("Json");
        string hash = await SaveAsync(scenario, "unit=" + new string('あ', nameLength) + ";{ty=n;sd=1,en;st=10:00;cy=1,(1,d);sh=no;}");
        await scenario.EnqueueAsync(new Operation { Id = "oversized", Kind = "Activate", Target = hash });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Rejected", StorageRepository.Read<Operation>((await storage.GetAsync("command:" + StorageRepository.Hash("oversized"), CancellationToken.None))!).Status);
        Assert.Equal(before, (await storage.GetAsync("definition-active", CancellationToken.None))!.GetString("Json"));
        Assert.Empty(await scenario.OccurrencesAsync());
    }

    private static async Task<string> SaveAsync(EngineScenario scenario, string text)
    {
        var definitions = new DefinitionService(scenario.Scope.Repository, scenario.Scope.Options);
        var bundle = definitions.Validate([new SourceEvidence { Mapping = new DefinitionSource { BlobPath = "local/next.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) }]);
        await definitions.SaveAsync(bundle, CancellationToken.None);
        return bundle.Hash;
    }
}
