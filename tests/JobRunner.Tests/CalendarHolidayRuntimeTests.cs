using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>月編集と同じ日付例外を反映し、確定予定と実起動で休日の扱いを確認します。</summary>
public sealed class CalendarHolidayRuntimeTests
{
    /// <summary>運用日予定と休業日中止は起動せず、明示的な休日実行だけを許可します。</summary>
    /// <param name="count">未来世代数方式かです。</param>
    /// <returns>再起動後の休業日と翌稼働日の実プロセス起動です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditedHolidayRemovesNormalSchedulesAndSkipsExecution(bool count)
    {
        await using var scenario = new EngineScenario();
        scenario.Scope.Options.ConfirmedHorizonDays = 3;
        scenario.Scope.Options.ConfirmedFutureGenerations = count ? 2 : null;
        string Network(string name, string date, string shift) => $"unit={name};{{ty=n;jc=\"/calendar/child\";sd=1,{date};st=10:00;cy=(1,d);sh={shift};unit=j;{{ty=j;te=\"true\";}}}}";
        await scenario.StartAsync("unit=calendar;{ty=g;op=mo;op=tu;op=we;op=th;op=fr;cl=sa;cl=su;unit=child;{ty=g;}}" + Network("working", "2026/11/*4", "no") + Network("skip", "2026/11/5", "ca") + Network("always", "2026/11/5", "no"));
        var before = await scenario.OccurrencesAsync();
        var holidayDue = before.First(o => o.Id == o.RootId && o.BusinessDate == new DateOnly(2026, 11, 5)).Due;
        var storage = scenario.Scope.Repository;
        var service = new DefinitionService(storage, scenario.Scope.Options);
        var active = StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", CancellationToken.None))!);
        var bundle = await service.ReadSourcesAsync(active.DefinitionHash, CancellationToken.None);
        var draft = DefinitionEditor.Project(bundle, 9);
        draft.Calendars!.Single(c => c.Id == "/calendar").Attributes["cl"] = ["sa", "su", "2026/11/5"];
        var next = service.Validate(DefinitionEditor.Render(bundle, draft));
        var preview = DefinitionEditor.PreviewSchedule(next, new EditorScheduleRequest(draft, "/working", new(2026, 11, 5), new(2026, 11, 5), new(2026, 11, 7)), scenario.Scope.Options);
        Assert.Single(preview.Points);
        Assert.Equal(new DateOnly(2026, 11, 6), preview.Points[0].BusinessDate);
        await service.SaveAsync(next, CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "holiday", Kind = "Activate", Target = next.Hash });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Completed", StorageRepository.Read<Operation>((await storage.GetAsync("command:" + StorageRepository.Hash("holiday"), CancellationToken.None))!).Status);
        await scenario.RestartAsync();
        await scenario.Engine.TickAsync(CancellationToken.None);
        var published = await scenario.OccurrencesAsync();
        Assert.All(before, old => Assert.Equal("Superseded", published.Single(o => o.Id == old.Id).State));
        Assert.DoesNotContain(published, o => o.State != "Superseded" && o.BusinessDate == new DateOnly(2026, 11, 5) && o.RootPath is "/working" or "/skip");
        scenario.Clock.Now = holidayDue;
        await scenario.AdvanceUntilAsync(values => values.Any(o => o.RootPath == "/always" && o.BusinessDate == new DateOnly(2026, 11, 5) && o.State == "Succeeded"));
        var holiday = await scenario.OccurrencesAsync();
        Assert.DoesNotContain(holiday, o => o.Started && o.RootPath is "/working" or "/skip");
        Assert.Single(await storage.ListAsync("attempt:", CancellationToken.None));
        scenario.Clock.Now = holidayDue.AddDays(1);
        await scenario.AdvanceUntilAsync(values => new[] { "/working", "/skip" }.All(path => values.Any(o => o.RootPath == path && o.BusinessDate == new DateOnly(2026, 11, 6) && o.State == "Succeeded")));
        var completed = await scenario.OccurrencesAsync();
        Assert.All(new[] { "/working", "/skip" }, path => Assert.Single(completed, o => o.Id == o.RootId && o.RootPath == path && o.BusinessDate == new DateOnly(2026, 11, 6) && o.State == "Succeeded"));
        Assert.All(before, old => Assert.Equal("Superseded", completed.Single(o => o.Id == old.Id).State));
    }

    /// <summary>48時間制で実日時が翌日になっても、休日判定は元の業務日で行います。</summary>
    [Fact]
    public void ExtendedHoursUseBusinessDateForHolidayDecision()
    {
        var options = new RunnerOptions();
        var service = new DefinitionService(new StorageRepository(options), options);
        var source = new SourceEvidence { Mapping = new() { BlobPath = "calendar.ajs" }, Base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("unit=cal;{ty=g;cl=2026/11/5;}unit=n;{ty=n;jc=\"/cal\";sd=2026/11/4;st=25:00;cy=(1,d);sh=ca;}")) };
        var bundle = service.Validate([source]);
        var all = bundle.Units.Flatten().ToDictionary(u => u.Path);
        var points = ScheduleCalculator.Calculate(all["/n"], all, new Registration { Anchor = new(2026, 11, 4) }, new(2026, 11, 4), new(2026, 11, 7), options).ToList();
        Assert.Equal(new[] { new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 6) }, points.Select(p => p.BusinessDate));
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 16, 0, 0, TimeSpan.Zero), points[0].Due);
    }
}
