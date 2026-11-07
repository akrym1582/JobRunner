using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>試算と永続世代を比較し、祖先選択・復旧・起動制御を確認します。</summary>
public sealed class NestedSchedulePreviewRuntimeTests
{
    /// <summary>子孫の日時と採否が試算に一致し、再起動で保留・取消・実行回数が変わりません。</summary>
    /// <param name="count">件数方式かです。</param>
    /// <returns>実Storageと実プロセスによる検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewMatchesMaterializedChildrenAndRestartControls(bool count)
    {
        await using var scenario = new EngineScenario();
        scenario.Scope.Options.ConfirmedHorizonDays = 3;
        scenario.Scope.Options.ConfirmedFutureGenerations = count ? 3 : null;
        await scenario.StartAsync("unit=n;{ty=n;sd=en;st=10:00;cy=(1,d);sh=no;unit=c;{ty=n;de=n;sd=2026/11/6;st=11:00;ln=1,1;sh=no;unit=h;{ty=n;unit=j;{ty=j;te=\"true\";}}}}");
        var service = new DefinitionService(scenario.Scope.Repository, scenario.Scope.Options);
        var active = StorageRepository.Read<Activation>((await scenario.Scope.Repository.GetAsync("definition-active", CancellationToken.None))!);
        var bundle = await service.ReadSourcesAsync(active.DefinitionHash, CancellationToken.None);
        var draft = DefinitionEditor.Project(bundle, 9);
        var initial = await scenario.OccurrencesAsync();
        foreach (string path in new[] { "/n/c", "/n/c/h" })
        {
            var preview = DefinitionEditor.PreviewNestedSchedule(bundle, new EditorScheduleRequest(draft, path, new(2026, 11, 5), new(2026, 11, 5), new(2026, 11, 8)), scenario.Scope.Options);
            foreach (var row in preview.Rows)
            {
                var actual = Assert.Single(initial, o => o.UnitPath == path && o.BusinessDate == row.BusinessDate);
                Assert.Equal(row.Eligible ? "Waiting" : "NotScheduled", actual.State);
                if (row.Eligible)
                {
                    Assert.Equal(row.Selected!.Rule, actual.Rule);
                    Assert.Equal(row.Selected.Due, actual.Due);
                }
            }
        }

        Assert.Empty(await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
        await scenario.RestartAsync();
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(initial.Select(o => o.Id).Order(), (await scenario.OccurrencesAsync()).Select(o => o.Id).Order());
        var first = Assert.Single(initial, o => o.Id == o.RootId && o.BusinessDate.Day == 5);
        var last = Assert.Single(initial, o => o.Id == o.RootId && o.BusinessDate.Day == 7);
        await scenario.EnqueueAsync(new Operation { Id = "cancel", Kind = "Cancel", Target = first.Id });
        await scenario.Engine.TickAsync(CancellationToken.None);
        await scenario.EnqueueAsync(new Operation { Id = "hold", Kind = "Hold", Target = last.Id });
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Completed", StorageRepository.Read<Operation>((await scenario.Scope.Repository.GetAsync("command:" + StorageRepository.Hash("cancel"), CancellationToken.None))!).Status);
        Assert.Equal("Completed", StorageRepository.Read<Operation>((await scenario.Scope.Repository.GetAsync("command:" + StorageRepository.Hash("hold"), CancellationToken.None))!).Status);
        await scenario.RestartAsync();
        scenario.Clock.Now = initial.Single(o => o.UnitPath == "/n/c/h" && o.BusinessDate.Day == 6).Due;
        await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.UnitPath == "/n/c/h" && o.BusinessDate.Day == 6 && o.State == "Succeeded"));
        await scenario.RestartAsync();
        scenario.Clock.Now = last.Due;
        await scenario.Engine.TickAsync(CancellationToken.None);
        var final = await scenario.OccurrencesAsync();
        Assert.Equal("Cancelled", final.Single(o => o.Id == first.Id).State);
        Assert.Equal("Held", final.Single(o => o.Id == last.Id).State);
        Assert.Single(await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
        Assert.All(final.GroupBy(o => (o.RootId, o.UnitPath)), rows => Assert.Single(rows));
    }

    /// <summary>リカバリーの試算は対象条件だけを示し、異常後の起動も同じ予定を使います。</summary>
    /// <returns>実プロセスの異常とリカバリーを確認します。</returns>
    [Fact]
    public async Task RecoveryNetworkKeepsItsScheduleWithoutPredictingFailure()
    {
        await using var scenario = new EngineScenario();
        await scenario.StartAsync("unit=n;{ty=n;sd=2026/11/5;st=10:00;sh=no;ar=(f=fail,t=rc);unit=fail;{ty=j;te=\"false\";}unit=rc;{ty=rn;de=n;sd=2026/11/5;st=11:00;ln=1,1;sh=no;unit=j;{ty=j;te=\"true\";}}}");
        var initial = await scenario.OccurrencesAsync();
        var recovery = Assert.Single(initial, o => o.UnitPath == "/n/rc");
        var service = new DefinitionService(scenario.Scope.Repository, scenario.Scope.Options);
        var bundle = await service.ReadSourcesAsync(recovery.DefinitionHash!, CancellationToken.None);
        var draft = DefinitionEditor.Project(bundle, 9);
        var row = Assert.Single(DefinitionEditor.PreviewNestedSchedule(bundle, new(draft, "/n/rc", new(2026, 11, 5), new(2026, 11, 5), new(2026, 11, 6)), scenario.Scope.Options).Rows);
        Assert.True(row.Eligible);
        Assert.Equal("rn", row.Chain[^1].Type);
        Assert.Equal(recovery.Due, row.Selected!.Due);
        Assert.Empty(await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
        scenario.Clock.Now = recovery.Due;
        await scenario.AdvanceUntilAsync(rows => rows.Any(o => o.Id == recovery.Id && o.State == "Succeeded"));
        Assert.Equal(2, (await scenario.Scope.Repository.ListAsync("attempt:", CancellationToken.None)).Count);
    }
}
