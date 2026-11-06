using System.Text.Json;
using Azure.Data.Tables;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>監視属性の受理から世代固定、予定変更、部分復旧までを確認します。</summary>
public sealed class DefinitionMonitorTests
{
    /// <summary>ルールと値の異常を有効化前に検出します。</summary>
    /// <param name="attributes">拒否する属性です。</param>
    [Theory]
    [InlineData("sy=;")]
    [InlineData("sy=C0;")]
    [InlineData("ey=C2880;")]
    [InlineData("sy=48:00;")]
    [InlineData("sy=00:60;")]
    [InlineData("sy=0,C1;")]
    [InlineData("sy=145,C1;")]
    [InlineData("sy=C1;sy=1,C2;")]
    [InlineData("fd=0;")]
    [InlineData("fd=2880;")]
    [InlineData("fd=1;fd=2;")]
    [InlineData("sy=M1;")]
    [InlineData("ey=U1;")]
    public void RejectsInvalidDefinition(string attributes)
    {
        var units = Jp1DefinitionParser.Parse("unit=n;{ty=n;" + attributes + "}", new DefinitionSource());
        Assert.Throws<FormatException>(() => DefinitionValidator.Validate(units, new RunnerOptions()));
    }

    /// <summary>ネット以外のfdと未採用ルールの誤用を防止します。</summary>
    [Fact]
    public void SelectsOnlySpecifiedRuleAndRejectsJobDuration()
    {
        var units = Jp1DefinitionParser.Parse("unit=n;{ty=n;sy=1,00:00;sy=144,C2879;ey=144,47:59;fd=2879;}", new DefinitionSource());
        DefinitionValidator.Validate(units, new RunnerOptions());
        var settings = DefinitionDelayMonitor.Read(units[0], 144)!;
        Assert.Equal("OWN", settings.Start);
        Assert.Equal(2879, settings.StartMinutes);
        Assert.Equal("ABSOLUTE", settings.End);
        Assert.Equal(2879, settings.EndMinutes);
        Assert.Equal(2879, settings.DurationMinutes);
        Assert.Null(DefinitionDelayMonitor.Read(units[0], 2)!.Start);
        var jobs = Jp1DefinitionParser.Parse("unit=j;{ty=j;te=\"true\";fd=1;}", new DefinitionSource());
        Assert.Throws<FormatException>(() => DefinitionValidator.Validate(jobs, new RunnerOptions()));
    }

    /// <summary>上位リンクで選んだルールだけの監視を保存します。</summary>
    /// <param name="firstTime">第1ルールの時刻です。</param>
    /// <param name="rule">採用されるルールです。</param>
    /// <param name="mode">採用される監視方式です。</param>
    /// <param name="minutes">採用される相対分です。</param>
    /// <returns>ネット階層と監視の対応を確認します。</returns>
    [Theory]
    [InlineData("12:00", 2, "TOP", 2)]
    [InlineData("11:00", 1, "OWN", 99)]
    public async Task NestedSelectedRuleDeterminesMonitor(string firstTime, int rule, string mode, int minutes)
    {
        await using var s = new EngineScenario();
        await s.StartAsync("unit=n;{ty=n;sd=2026/11/5;st=10:00;sh=no;sy=C1;unit=child;{ty=n;de=n;sd=1,2026/11/5;sd=2,2026/11/5;ln=1,1;ln=2,1;st=1,12:00;st=2,11:00;sh=1,no;sh=2,no;sy=1,C99;sy=2,U2;ey=1,M3;ey=2,M3;fd=4;}}".Replace("st=1,12:00", "st=1," + firstTime, StringComparison.Ordinal));
        var child = (await s.OccurrencesAsync()).Single(o => o.UnitPath == "/n/child");
        Assert.Equal(rule, child.Rule);
        Assert.Equal(mode, child.DelayMonitor!.Start);
        Assert.Equal(minutes, child.DelayMonitor.StartMinutes);
        Assert.Equal("ROOT", child.DelayMonitor.End);
        var root = (await s.OccurrencesAsync()).Single(o => o.Id == o.RootId);
        Assert.Equal((mode == "OWN" ? child.Due : root.Due).AddMinutes(minutes), DelayMonitoring.Limit(child, mode, minutes, await s.OccurrencesAsync(), s.Scope.Options));
        await s.RestartAsync();
        Assert.Equal(JsonSerializer.Serialize(child.DelayMonitor), JsonSerializer.Serialize((await s.OccurrencesAsync()).Single(o => o.Id == child.Id).DelayMonitor));
    }

    /// <summary>手動NOTと既存の子を上書きせず、固定版から不足行を復旧します。</summary>
    /// <param name="keep">保存されていた子の数です。</param>
    /// <param name="future">件数方式か日数方式かを指定します。</param>
    /// <returns>長期停止後も重複起動せずに復旧します。</returns>
    [Theory]
    [InlineData(0, null)]
    [InlineData(1, null)]
    [InlineData(2, null)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public async Task RepairsMonitorsWithoutOverwritingSavedSettings(int keep, int? future)
    {
        await using var s = new EngineScenario();
        s.Scope.Options.ConfirmedFutureGenerations = future;
        await s.StartAsync("unit=n;{ty=n;sd=2026/11/5;st=10:00;cy=(1,d);sh=no;sy=C5;ey=C10;fd=7;unit=a;{ty=n;sy=U2;ey=M3;fd=4;}unit=b;{ty=n;sy=U2;ey=M3;fd=4;}}");
        var storage = s.Scope.Repository;
        var rows = await storage.ListAsync("occurrence:", CancellationToken.None);
        var rootRow = rows.Single(r => StorageRepository.Read<Occurrence>(r).UnitPath == "/n");
        var root = StorageRepository.Read<Occurrence>(rootRow);
        root.DelayMonitor!.Start = "NOT";
        root.DelayMonitor.StartMinutes = null;
        root.Materialized = false;
        await storage.Table.UpdateEntityAsync(storage.Entity(rootRow.RowKey, root), rootRow.ETag, TableUpdateMode.Replace);
        var children = rows.Where(r => r.RowKey != rootRow.RowKey).OrderBy(r => r.RowKey).ToList();
        foreach (var row in children.Skip(keep))
        {
            await storage.Table.DeleteEntityAsync(storage.Partition, row.RowKey, row.ETag);
        }

        s.Clock.Now = s.Clock.Now.AddDays(10);
        await s.RestartAsync();
        await s.Engine.TickAsync(CancellationToken.None);
        var repaired = (await s.OccurrencesAsync()).Where(o => o.RootId == root.Id).ToList();
        Assert.Equal(rows.Select(r => r.RowKey).Order(), repaired.Select(o => "occurrence:" + o.Id).Order());
        Assert.Equal("NOT", repaired.Single(o => o.Id == root.Id).DelayMonitor!.Start);
        Assert.All(repaired.Where(o => o.Id != root.Id), o =>
        {
            Assert.Equal("TOP", o.DelayMonitor!.Start);
            Assert.Equal("ROOT", o.DelayMonitor.End);
            Assert.Equal(4, o.DelayMonitor.DurationMinutes);
        });
        Assert.Equal("Skipped", repaired.Single(o => o.Id == root.Id).State);
        Assert.Empty(await storage.ListAsync("attempt:", CancellationToken.None));
        await s.RestartAsync();
        var next = (await s.OccurrencesAsync()).Where(o => o.Id == o.RootId && o.State == "Waiting").OrderBy(o => o.Due).First();
        Assert.Equal("OWN", next.DelayMonitor!.Start);
        Assert.Equal(5, next.DelayMonitor.StartMinutes);
    }

    /// <summary>予定変更と再実行は世代の監視設定を保持し、基点だけを更新します。</summary>
    /// <returns>API変更、ADD、復元、再実行後の値を確認します。</returns>
    [Fact]
    public async Task ScheduleChangesAndRerunKeepGenerationSettings()
    {
        await using var s = new EngineScenario();
        await s.StartAsync("unit=n;{ty=n;sd=2026/11/5;st=10:00;sh=no;sy=C5;ey=C10;fd=7;}");
        var root = Assert.Single(await s.OccurrencesAsync());
        await s.EnqueueAsync(new Operation { Id = "monitor", Kind = "ChangeDelayMonitor", Target = root.Id, DelayMonitor = new() { Start = "NOT" } });
        await s.Engine.TickAsync(CancellationToken.None);
        await s.EnqueueAsync(new Operation { Id = "move", Kind = "Move", Target = root.Id, Due = root.Due.AddMinutes(-30), PushedAhead = "ADD" });
        await s.Engine.TickAsync(CancellationToken.None);
        var roots = await s.OccurrencesAsync();
        Assert.Equal(2, roots.Count);
        Assert.All(roots, o => Assert.Equal("NOT", o.DelayMonitor!.Start));
        var moved = roots.Single(o => o.Id == root.Id);
        Assert.Equal(root.Due.AddMinutes(-20), DelayMonitoring.Limit(moved, moved.DelayMonitor!.End, moved.DelayMonitor.EndMinutes, roots, s.Scope.Options));
        await s.EnqueueAsync(new Operation { Id = "restore", Kind = "Restore", Target = root.Id });
        await s.Engine.TickAsync(CancellationToken.None);
        s.Clock.Now = root.Due;
        await s.AdvanceUntilAsync(o => o.Single(v => v.Id == root.Id).State == "Succeeded");
        s.Clock.Now = s.Clock.Now.AddHours(1);
        await s.EnqueueAsync(new Operation { Id = "rerun", Kind = "Rerun", Target = root.Id, RerunMethod = "TOP" });
        await s.AdvanceUntilAsync(o => o.Single(v => v.Id == root.Id).State == "Succeeded" && o.Single(v => v.Id == root.Id).RestartedAt.HasValue);
        root = (await s.OccurrencesAsync()).Single(o => o.Id == root.Id);
        Assert.Equal("NOT", root.DelayMonitor!.Start);
        Assert.Equal(s.Clock.Now, root.RestartedAt);
        root.DelayMonitor.End = "NOT";
        Assert.False(DelayMonitoring.Evaluate(root, [root], s.Scope.Options, s.Clock.Now.AddDays(1)).End);
    }
}
