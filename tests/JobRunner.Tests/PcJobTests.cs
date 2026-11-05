using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>Linux上のPCジョブを、原文の採用から実プロセスの結果保存まで検証します。</summary>
public sealed class PcJobTests
{
    /// <summary>PCジョブのシェルと直接実行を受理し、引数・環境・出力・終了状態を保持します。</summary>
    /// <param name="mode">teはシェル、scは実行ファイルと引数の指定です。</param>
    /// <param name="code">実コマンドが返す終了コードです。</param>
    /// <param name="expected">世代と試行に保存すべき状態です。</param>
    /// <returns>Blob原文から実Storageに保存した結果までの検証です。</returns>
    [Theory]
    [InlineData("te", 0, "Succeeded")]
    [InlineData("te", 7, "Failed")]
    [InlineData("sc", 0, "Succeeded")]
    public async Task RunsPcJobCommandsOnLinux(string mode, int code, string expected)
    {
        Assert.True(OperatingSystem.IsLinux(), "Linux向け統合テストです。");
        await using var scenario = new EngineScenario();
        string command = $"printf '%s|%s' \"$JOBRUNNER_PC_VALUE\" \"$PWD\"; printf 'pc-error' >&2; exit {code}";
        string execution = mode == "te"
            ? "te=\"" + command.Replace("\"", "#\"", StringComparison.Ordinal) + "\";"
            : "sc=\"/usr/bin/printf\";prm=\"#\"%s#\" #\"binary argument with spaces#\"\";";
        await scenario.StartAsync("unit=net;{ty=n;sd=2026/11/5;st=09:00;sh=no;unit=pc;{ty=pj;wkp=\"/tmp\";env=\"JOBRUNNER_PC_VALUE=pc environment value\";" + execution + "}}");
        await scenario.AdvanceUntilAsync(rows => rows.All(o => o.State == expected));
        var storage = scenario.Scope.Repository;
        var attempt = StorageRepository.Read<JobAttempt>(Assert.Single(await storage.ListAsync("attempt:", CancellationToken.None)));
        Assert.Equal(expected, attempt.State);
        Assert.Equal(code, attempt.ExitCode);
        Assert.True(attempt.LogsComplete);
        Assert.NotNull(attempt.ProcessId);
        var stdout = await storage.Container.GetBlobClient($"logs/{attempt.OccurrenceId}/{attempt.Id}/stdout/00000000.txt").DownloadContentAsync();
        Assert.Equal(mode == "te" ? "pc environment value|/tmp" : "binary argument with spaces", stdout.Value.Content.ToString());
        if (mode == "te")
        {
            var stderr = await storage.Container.GetBlobClient($"logs/{attempt.OccurrenceId}/{attempt.Id}/stderr/00000000.txt").DownloadContentAsync();
            Assert.Equal("pc-error", stderr.Value.Content.ToString());
        }
    }

    /// <summary>PCジョブのOS制限を緩和しても、未対応の実行属性を黙って無視しません。</summary>
    /// <param name="attribute">拒否すべき未対応属性です。</param>
    [Theory]
    [InlineData("un=other;")]
    [InlineData("ex=remote;")]
    [InlineData("unknown=y;")]
    public void RejectsUnsupportedPcJobExecutionAttributes(string attribute)
    {
        var units = Jp1DefinitionParser.Parse("unit=pc;{ty=pj;te=\"true\";" + attribute + "}", new DefinitionSource());
        Assert.Throws<FormatException>(() => DefinitionValidator.Validate(units, new RunnerOptions()));
    }
}
