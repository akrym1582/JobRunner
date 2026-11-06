using System.Text.Json;
using Azure.Data.Tables;
using JobRunner.Controllers.Jp1;
using JobRunner.Execution;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>予定変更・変数判定・リカバリーの業務上の結果を検証します。</summary>
public sealed class CompatibilityExtensionTests
{
    /// <summary>前倒し元を残し、独立ネストを動かさず、再起動後にも復元できます。</summary>
    /// <returns>追加実行IDと配下の時刻を検証します。</returns>
    [Fact]
    public async Task AddNotShiftRestoresRelatedGenerationsAcrossRestart()
    {
        await using var s = new EngineScenario();
        await s.StartAsync("unit=net;{ty=n;ha=y;sd=2026/11/5;st=10:00;sh=no;unit=job;{ty=j;te=\"true\";}unit=nested;{ty=n;de=n;sd=1,en;st=12:00;sh=no;unit=j;{ty=j;te=\"true\";}}}");
        var before = await s.OccurrencesAsync();
        var root = Assert.Single(before, o => o.Id == o.RootId);
        await s.EnqueueAsync(new Operation { Id = "advance", Kind = "Move", Target = root.Id, RelativeMinutes = -30, ChangeLower = "NOTSHIFT", PushedAhead = "ADD" });
        await s.Engine.TickAsync(CancellationToken.None);
        var rows = await s.OccurrencesAsync();
        Assert.Equal(8, rows.Count);
        Assert.Equal(2, rows.Where(o => o.Id == o.RootId).Select(o => o.Jp1ExecutionId).Distinct().Count());
        Assert.Equal(root.Due.AddMinutes(-30), rows.Single(o => o.Id == root.Id).Due);
        Assert.Equal(root.Due.AddMinutes(-30), rows.Single(o => o.RootId == root.Id && o.UnitPath == "/net/job").Due);
        Assert.Equal(before.Single(o => o.UnitPath == "/net/nested").Due, rows.Single(o => o.RootId == root.Id && o.UnitPath == "/net/nested").Due);
        await s.EnqueueAsync(new Operation { Id = "restore-nested", Kind = "Restore", Target = rows.Single(o => o.RootId == root.Id && o.UnitPath == "/net/nested").Id });
        await s.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Held", (await s.OccurrencesAsync()).Single(o => o.Id == o.RootId && o.Id != root.Id).State);
        var addition = (await s.OccurrencesAsync()).Single(o => o.Id == o.RootId && o.Id != root.Id);
        await s.EnqueueAsync(new Operation { Id = "advance-addition", Kind = "Move", Target = addition.Id, RelativeMinutes = -10, PushedAhead = "ADD" });
        await s.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(3, (await s.OccurrencesAsync()).Count(o => o.Id == o.RootId));
        await s.RestartAsync();
        await s.EnqueueAsync(new Operation { Id = "restore", Kind = "Restore", Target = root.Id });
        await s.Engine.TickAsync(CancellationToken.None);
        rows = await s.OccurrencesAsync();
        Assert.All(rows.Where(o => o.RootId == root.Id), o => Assert.Equal(before.Single(b => b.Id == o.Id).Due, o.Due));
        Assert.All(rows.Where(o => o.RootId != root.Id), o => Assert.Equal("Cancelled", o.State));
        Assert.Empty(await s.Scope.Repository.ListAsync("attempt:", CancellationToken.None));
    }

    /// <summary>開始した追加世代を含む変更解除は一切適用しません。</summary>
    /// <returns>競合結果と元予定の維持を確認します。</returns>
    [Fact]
    public async Task RestoreRejectsStartedAdditionWithoutPartialChanges()
    {
        await using var s = new EngineScenario();
        await s.StartAsync("unit=net;{ty=n;ha=y;sd=2026/11/5;st=10:00;sh=no;}");
        var root = Assert.Single(await s.OccurrencesAsync());
        await s.EnqueueAsync(new Operation { Id = "add", Kind = "Move", Target = root.Id, RelativeMinutes = -20, PushedAhead = "ADD" });
        await s.Engine.TickAsync(CancellationToken.None);
        var extra = Assert.Single(await s.OccurrencesAsync(), o => o.Id != root.Id);
        var extraRow = (await s.Scope.Repository.GetAsync("occurrence:" + extra.Id, CancellationToken.None))!;
        extra.Started = true;
        extra.State = "Succeeded";
        await s.Scope.Repository.Table.UpdateEntityAsync(s.Scope.Repository.Entity(extraRow.RowKey, extra), extraRow.ETag, TableUpdateMode.Replace);
        await s.EnqueueAsync(new Operation { Id = "restore", Kind = "Restore", Target = root.Id });
        await s.Engine.TickAsync(CancellationToken.None);
        var operation = StorageRepository.Read<Operation>((await s.Scope.Repository.GetAsync("command:" + StorageRepository.Hash("restore"), CancellationToken.None))!);
        Assert.Equal("Rejected", operation.Status);
        Assert.Equal("JOBRUNNER_STATE_CONFLICT", operation.ErrorCode);
        Assert.Equal(root.Due.AddMinutes(-20), (await s.OccurrencesAsync()).Single(o => o.Id == root.Id).Due);
    }

    /// <summary>実行中止を解除すると保留と時刻が戻ります。</summary>
    /// <returns>取消前状態を確認します。</returns>
    [Fact]
    public async Task RestoreUndoesCancel()
    {
        await using var s = new EngineScenario();
        await s.StartAsync("unit=net;{ty=n;ha=y;sd=2026/11/5;st=10:00;sh=no;}");
        var root = Assert.Single(await s.OccurrencesAsync());
        await s.EnqueueAsync(new Operation { Id = "cancel", Kind = "Cancel", Target = root.Id });
        await s.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Cancelled", Assert.Single(await s.OccurrencesAsync()).State);
        await s.EnqueueAsync(new Operation { Id = "restore", Kind = "Restore", Target = root.Id });
        await s.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Held", Assert.Single(await s.OccurrencesAsync()).State);
    }

    /// <summary>解除された期間を件数補充と復旧が復活させません。</summary>
    /// <param name="cancelBeforeUnregister">予定取消後に登録解除する場合です。</param>
    /// <returns>取消と新しい未開始枠を確認します。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PeriodUnregisterSurvivesCountReplenishment(bool cancelBeforeUnregister)
    {
        await using var s = new EngineScenario();
        s.Scope.Options.ConfirmedFutureGenerations = 2;
        await s.StartAsync("unit=net;{ty=n;ha=y;sd=2026/11/5;st=10:00;cy=(1,d);sh=no;}");
        var first = (await s.OccurrencesAsync()).OrderBy(o => o.Due).First();
        await s.EnqueueAsync(new Operation { Id = "move-before-unregister", Kind = "Move", Target = first.Id, RelativeMinutes = 5 });
        await s.Engine.TickAsync(CancellationToken.None);
        if (cancelBeforeUnregister)
        {
            await s.EnqueueAsync(new Operation { Id = "cancel-before-unregister", Kind = "Cancel", Target = first.Id });
            await s.Engine.TickAsync(CancellationToken.None);
        }

        var foreign = JsonSerializer.Deserialize<Occurrence>(JsonSerializer.Serialize(first))!;
        foreign.Id = "old-root";
        foreign.RootId = foreign.Id;
        foreign.Namespace = "previous-activation";
        await s.Scope.Repository.Table.AddEntityAsync(s.Scope.Repository.Entity("occurrence:" + foreign.Id, foreign));
        await s.EnqueueAsync(new Operation { Id = "exclude", Kind = "UnregisterRoot", Target = "/net", DateType = "EXECUTION_DATE", Start = new DateOnly(2026, 11, 5), End = new DateOnly(2026, 11, 6) });
        await s.Engine.TickAsync(CancellationToken.None);
        await s.RestartAsync();
        await s.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Held", (await s.OccurrencesAsync()).Single(o => o.Id == foreign.Id).State);
        var rows = (await s.OccurrencesAsync()).Where(o => o.Namespace == s.Engine.Namespace).ToList();
        Assert.All(rows.Where(o => o.BusinessDate <= new DateOnly(2026, 11, 6)), o => Assert.Equal("Cancelled", o.State));
        Assert.Equal(2, rows.Count(o => o.State == "Held"));
        Assert.All(rows.Where(o => o.State == "Held"), o => Assert.True(o.BusinessDate > new DateOnly(2026, 11, 6)));
        await s.EnqueueAsync(new Operation { Id = "restore-unregistered", Kind = "Restore", Target = first.Id });
        await s.Engine.TickAsync(CancellationToken.None);
        Assert.Equal("Cancelled", (await s.OccurrencesAsync()).Single(o => o.Id == first.Id).State);
        Assert.Equal("Rejected", StorageRepository.Read<Operation>((await s.Scope.Repository.GetAsync("command:" + StorageRepository.Hash("restore-unregistered"), CancellationToken.None))!).Status);
    }

    /// <summary>先行の専用出力から判定し、真偽で従属の起動を分けます。</summary>
    /// <param name="condition">変数の比較条件です。</param>
    /// <param name="expected">比較結果です。</param>
    /// <returns>永続変数と分岐の試行数を確認します。</returns>
    [Theory]
    [InlineData("ej=vge;eji=7;", true)]
    [InlineData("ej=vgt;eji=7;", false)]
    [InlineData("ej=sce;ejt=\"7\";", true)]
    [InlineData("ej=snn;", true)]
    [InlineData("ej=snl;", false)]
    public async Task VariablesArePersistedBeforeBranching(string condition, bool expected)
    {
        await using var s = new EngineScenario();
        string source = """
            unit=net;{ty=n;sd=2026/11/5;st=09:00;sh=no;ar=(f=a,t=judge);ar=(f=judge,t=b,con);
            unit=a;{ty=j;te="printf '%s' '{#"AJS2RESULT#":#"7#"}' > #"$JOBRUNNER_OUTPUT_FILE#"";}
            unit=judge;{ty=jdj;ejv=?AJS2RESULT?;CONDITION}
            unit=b;{ty=j;te="true";}}
            """;
        await s.StartAsync(source.Replace("CONDITION", condition, StringComparison.Ordinal));
        await s.AdvanceUntilAsync(rows => rows.Any(o => o.UnitPath == "/net" && o.State == "Succeeded"));
        var rows = await s.OccurrencesAsync();
        Assert.Equal(expected, rows.Single(o => o.UnitPath == "/net/judge").ConditionMatched);
        var attempts = (await s.Scope.Repository.ListAsync("attempt:", CancellationToken.None)).Select(StorageRepository.Read<JobAttempt>).ToList();
        Assert.Equal(expected ? 2 : 1, attempts.Count);
        Assert.Equal("7", attempts.Single(a => a.OccurrenceId == rows.Single(o => o.UnitPath == "/net/a").Id).OutputVariables!["AJS2RESULT"]);
    }

    /// <summary>リカバリーネットから判定と後続を実行しても元の異常は残ります。</summary>
    /// <returns>正常リカバリーの連鎖を確認します。</returns>
    [Fact]
    public async Task RecoveryNetworkAndJudgmentChainPreserveOriginalFailure()
    {
        await using var s = new EngineScenario();
        await s.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=09:00;sh=no;ar=(f=a,t=rn);ar=(f=a,t=r);ar=(f=r,t=judge);ar=(f=judge,t=after,con);unit=a;{ty=j;te=\"exit 1\";}unit=rn;{ty=rn;unit=j;{ty=j;te=\"true\";}}unit=r;{ty=rj;te=\"true\";}unit=judge;{ty=rjdj;ej=eq;ejc=0;}unit=after;{ty=rj;te=\"true\";}}");
        await s.AdvanceUntilAsync(rows => rows.Any(o => o.UnitPath == "/net" && o.State == "Failed"));
        var rows = await s.OccurrencesAsync();
        Assert.Equal("Succeeded", rows.Single(o => o.UnitPath == "/net/rn").State);
        Assert.Equal("Succeeded", rows.Single(o => o.UnitPath == "/net/after").State);
        Assert.Equal(4, (await s.Scope.Repository.ListAsync("attempt:", CancellationToken.None)).Count);
    }

    /// <summary>複数先行の一つだけの異常ではリカバリーを開始しません。</summary>
    /// <returns>正常・異常混在の合流を確認します。</returns>
    [Fact]
    public async Task RecoveryRequiresEveryNormalPredecessorToFail()
    {
        await using var s = new EngineScenario();
        await s.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=09:00;sh=no;ar=(f=a,t=r);ar=(f=b,t=r);unit=a;{ty=j;te=\"exit 1\";}unit=b;{ty=j;te=\"true\";}unit=r;{ty=rj;te=\"true\";}}");
        await s.AdvanceUntilAsync(rows => rows.Any(o => o.UnitPath == "/net" && o.State == "Failed"));
        Assert.Equal("NotScheduled", (await s.OccurrencesAsync()).Single(o => o.UnitPath == "/net/r").State);
        Assert.Equal(2, (await s.Scope.Repository.ListAsync("attempt:", CancellationToken.None)).Count);
    }

    /// <summary>保存したファイル判定を再起動後にやり直しません。</summary>
    /// <returns>ファイル変更後も元の分岐結果を保持します。</returns>
    [Fact]
    public async Task FilePredicateIsDurableAcrossRestart()
    {
        string path = Path.GetTempFileName();
        try
        {
            await using var s = new EngineScenario();
            await s.StartAsync($"unit=net;{{ty=n;sd=2026/11/5;st=09:00;sh=no;ar=(f=a,t=judge);ar=(f=judge,t=b,con);unit=a;{{ty=j;te=\"true\";}}unit=judge;{{ty=jdj;ej=ef;ejf=\"{path}\";}}unit=b;{{ty=j;ha=y;te=\"true\";}}}}");
            await s.AdvanceUntilAsync(rows => rows.Any(o => o.UnitPath == "/net/judge" && o.ConditionMatched == true));
            File.Delete(path);
            await s.RestartAsync();
            await s.Engine.TickAsync(CancellationToken.None);
            Assert.True((await s.OccurrencesAsync()).Single(o => o.UnitPath == "/net/judge").ConditionMatched);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>アクセス拒否を不存在条件の成立へ変換しません。</summary>
    /// <returns>判定不能の異常終了を確認します。</returns>
    [Fact]
    public async Task FileAccessFailureIsNotAbsence()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), "judge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "file");
        File.WriteAllText(path, "present");
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.None);
            await using var s = new EngineScenario();
            await s.StartAsync($"unit=net;{{ty=n;sd=2026/11/5;st=09:00;sh=no;ar=(f=a,t=judge);unit=a;{{ty=j;te=\"true\";}}unit=judge;{{ty=jdj;ej=nf;ejf=\"{path}\";}}}}");
            await s.AdvanceUntilAsync(rows => rows.Any(o => o.UnitPath == "/net" && o.State == "Failed"));
            var judge = (await s.OccurrencesAsync()).Single(o => o.UnitPath == "/net/judge");
            Assert.Equal("Failed", judge.State);
            Assert.Null(judge.ConditionMatched);
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(directory, true);
        }
    }

    /// <summary>未定義・空文字と数値化不能を区別します。</summary>
    /// <param name="output">先行が出力するJSONです。</param>
    /// <param name="condition">判定条件です。</param>
    /// <param name="state">判定結果の状態です。</param>
    /// <returns>判定不能を偽として保存しないことを確認します。</returns>
    [Theory]
    [InlineData("{}", "ej=snl;", "Succeeded")]
    [InlineData("{\"AJS2X\":\"\"}", "ej=snl;", "Succeeded")]
    [InlineData("{\"AJS2X\":\"bad\"}", "ej=vgt;eji=0;", "Failed")]
    [InlineData("{}", "ej=vgt;eji=0;", "Failed")]
    public async Task MissingAndNonNumericValuesDoNotSilentlyBecomeFalse(string output, string condition, string state)
    {
        await using var s = new EngineScenario();
        string escaped = output.Replace("\"", "#\"", StringComparison.Ordinal);
        await s.StartAsync($"unit=net;{{ty=n;sd=2026/11/5;st=09:00;sh=no;ar=(f=a,t=judge);unit=a;{{ty=j;te=\"printf '%s' '{escaped}' > #\"$JOBRUNNER_OUTPUT_FILE#\"\";}}unit=judge;{{ty=jdj;ejv=?AJS2X?;{condition}}}}}");
        await s.AdvanceUntilAsync(rows => rows.Any(o => o.UnitPath == "/net" && o.State is "Succeeded" or "Failed"));
        var judge = (await s.OccurrencesAsync()).Single(o => o.UnitPath == "/net/judge");
        Assert.Equal(state, judge.State);
        if (state == "Failed")
        {
            Assert.Null(judge.ConditionMatched);
        }
        else
        {
            Assert.True(judge.ConditionMatched);
        }
    }

    /// <summary>再実行の判定は新しい試行の出力だけを参照します。</summary>
    /// <returns>古い変数を再利用しないことを確認します。</returns>
    [Fact]
    public async Task RerunUsesFreshVariablesAndPreservesPreviousAttempt()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"AJS2X\":\"yes\"}");
            await using var s = new EngineScenario();
            await s.StartAsync($"unit=net;{{ty=n;sd=2026/11/5;st=09:00;sh=no;ar=(f=a,t=judge);ar=(f=judge,t=b,con);unit=a;{{ty=j;te=\"cat '{path}' > #\"$JOBRUNNER_OUTPUT_FILE#\"\";}}unit=judge;{{ty=jdj;ejv=?AJS2X?;ej=sce;ejt=\"yes\";}}unit=b;{{ty=j;te=\"true\";}}}}");
            await s.AdvanceUntilAsync(rows => rows.Any(o => o.UnitPath == "/net" && o.State == "Succeeded"));
            File.WriteAllText(path, "{\"AJS2X\":\"no\"}");
            string rootId = (await s.OccurrencesAsync()).Single(o => o.Id == o.RootId).Id;
            await s.EnqueueAsync(new Operation { Id = "rerun", Kind = "Rerun", Target = rootId, RerunMethod = "TOP" });
            await s.Engine.TickAsync(CancellationToken.None);
            await s.AdvanceUntilAsync(rows => rows.Any(o => o.UnitPath == "/net" && o.State == "Succeeded"));
            Assert.False((await s.OccurrencesAsync()).Single(o => o.UnitPath == "/net/judge").ConditionMatched);
            Assert.Equal(3, (await s.Scope.Repository.ListAsync("attempt:", CancellationToken.None)).Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>不正な出力を部分的に受理せず、明確な失敗として扱います。</summary>
    /// <param name="json">試行の出力です。</param>
    [Theory]
    [InlineData("{\"AJS2X\":\"a\",\"AJS2X\":\"b\"}")]
    [InlineData("{\"AJS2X\":1}")]
    [InlineData("[]")]
    public void RejectsInvalidVariableOutput(string json)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            Assert.Throws<FormatException>(() => JobOutput.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>OS起動前の失敗を、実行結果不明と混同しません。</summary>
    /// <returns>起動失敗の保存とHTTP状態を確認します。</returns>
    [Fact]
    public async Task FailedProcessStartIsReportedAsFail()
    {
        await using var s = new EngineScenario();
        await s.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=09:00;sh=no;unit=j;{ty=j;sc=\"/jobrunner-nonexistent-executable\";}}");
        await s.AdvanceUntilAsync(rows => rows.Any(o => o.UnitPath == "/net" && o.State == "Failed"));
        var job = (await s.OccurrencesAsync()).Single(o => o.UnitPath == "/net/j");
        Assert.Equal("FAIL", job.StatusDetail);
        Assert.Equal("Failed", job.State);
    }

    /// <summary>出力のバイト長と件数の境界を検証します。</summary>
    [Fact]
    public void VariableOutputLimitsAreEnforced()
    {
        string path = Path.GetTempFileName();
        try
        {
            var values = Enumerable.Range(0, 32).ToDictionary(i => "AJS2X" + i, _ => new string('a', 255));
            File.WriteAllText(path, JsonSerializer.Serialize(values));
            Assert.Equal(32, JobOutput.Read(path).Count);
            values["AJS2X0"] += "a";
            File.WriteAllText(path, JsonSerializer.Serialize(values));
            Assert.Throws<FormatException>(() => JobOutput.Read(path));
            values["AJS2X0"] = string.Empty;
            values["AJS2EXTRA"] = string.Empty;
            File.WriteAllText(path, JsonSerializer.Serialize(values));
            Assert.Throws<FormatException>(() => JobOutput.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>終了状態不明と正常終了偽をそれぞれのJP1状態で返します。</summary>
    [Fact]
    public void StatusProjectionPreservesUnknownAndFalse()
    {
        var units = new Dictionary<string, UnitDefinition> { ["/net/j"] = new UnitDefinition { Attributes = new() { ["ty"] = ["jdj"] } } };
        var value = new Occurrence { UnitPath = "/net/j", State = "Unknown" };
        Assert.Equal("UNKNOWN", Jp1Status.Map(value, units, [value], DateTimeOffset.UtcNow));
        value.State = "Succeeded";
        value.ConditionMatched = false;
        Assert.Equal("NORMALFALSE", Jp1Status.Map(value, units, [value], DateTimeOffset.UtcNow));
    }
}
