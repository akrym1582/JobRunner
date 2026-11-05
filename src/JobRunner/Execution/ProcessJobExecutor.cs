using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using JobRunner.Models;
using JobRunner.Storage;

namespace JobRunner.Execution;

/// <summary>ローカルプロセスの出力を並行収集し、上限付きチャンクでBlobへ保存します。</summary>
/// <param name="storage">試行ごとの出力チャンクを保存する管理Storageです。</param>
public sealed class ProcessJobExecutor(StorageRepository storage)
{
    /// <summary>起動直前の権限を確認してジョブを実行し、終了結果を返します。</summary>
    /// <param name="unit">登録時に固定されたジョブ定義です。</param>
    /// <param name="attempt">起動前に永続化済みの試行です。</param>
    /// <param name="ensureLeader">OS起動直前に呼び出すリース検査です。</param>
    /// <param name="started">PIDを取得してRunningを保存する処理です。</param>
    /// <param name="cancellationToken">停止・リース喪失の通知です。</param>
    /// <returns>終了コードとログ保存状態を反映した試行です。</returns>
    public async Task<JobAttempt> ExecuteAsync(UnitDefinition unit, JobAttempt attempt, Action ensureLeader, Func<int, Task> started, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = unit.Get("wkp", Environment.CurrentDirectory),
        };
        string command = unit.Get("te");
        if (command.Length > 0)
        {
            info.FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
            info.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
            info.ArgumentList.Add(command);
        }
        else
        {
            info.FileName = unit.Get("sc");
            info.Arguments = unit.Get("prm");
        }

        foreach (string environment in unit.Attributes.GetValueOrDefault("env") ?? [])
        {
            int equal = environment.IndexOf('=');
            info.Environment[environment[..equal]] = environment[(equal + 1)..];
        }

        string outputDirectory = Path.Combine(Path.GetTempPath(), "jobrunner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(outputDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string outputFile = Path.Combine(outputDirectory, "outputs.json");
        info.Environment["JOBRUNNER_OUTPUT_FILE"] = outputFile;
        info.Environment["JOBRUNNER_EXECUTION_ID"] = attempt.OccurrenceId;
        info.Environment["JOBRUNNER_ATTEMPT_ID"] = attempt.Id;
        using var timeout = new CancellationTokenSource();
        if (unit.Get("etm") is { Length: > 0 } minutes)
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(int.Parse(minutes, CultureInfo.InvariantCulture)));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var process = new Process { StartInfo = info };
        ensureLeader();
        linked.Token.ThrowIfCancellationRequested();
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            attempt.State = "Failed";
            attempt.StatusDetail = "FAIL";
            Directory.Delete(outputDirectory, true);
            return attempt;
        }

        process.StandardInput.Close();
        attempt.ProcessId = process.Id;
        Task stdout = PumpAsync(process.StandardOutput, attempt, "stdout", linked);
        Task stderr = PumpAsync(process.StandardError, attempt, "stderr", linked);
        try
        {
            await started(process.Id);
            await process.WaitForExitAsync(linked.Token);
            await Task.WhenAll(stdout, stderr);
            attempt.ExitCode = process.ExitCode;
            attempt.LogsComplete = true;
            try
            {
                attempt.OutputVariables = JobOutput.Read(outputFile);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or IOException or UnauthorizedAccessException)
            {
                attempt.OutputError = ex.Message;
            }

            int abnormal = int.Parse(unit.Get("tho", "0"), CultureInfo.InvariantCulture);
            int warning = int.Parse(unit.Get("wth", abnormal.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
            attempt.State = unit.Get("jd", "cod") switch
            {
                "nm" => "Succeeded",
                "ab" => "Failed",
                _ => process.ExitCode > abnormal || process.ExitCode < 0 ? "Failed" : process.ExitCode > warning ? "Warning" : "Succeeded",
            };
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // 終了との競合は正常です。PIDだけで別のプロセスを取得し直しません。
            }

            await linked.CancelAsync();
            try
            {
                await Task.WhenAll(stdout, stderr);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or Azure.RequestFailedException)
            {
                attempt.LogsComplete = false;
            }

            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // 終了を確認できた場合だけ強制終了と表示し、確認不能はUnknownのままにします。
                using var confirmation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(confirmation.Token);
                attempt.State = "TimedOut";
                return attempt;
            }

            throw;
        }
        finally
        {
            try
            {
                Directory.Delete(outputDirectory, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 出力を再読込せず、後片付け失敗で業務結果を失わせません。
            }
        }

        if (attempt.OutputError is not null)
        {
            attempt.State = "Failed";
            attempt.StatusDetail = "ABNORMAL";
        }

        return attempt;
    }

    /// <summary>長い行にも依存せず、固定長バッファで出力を永続化します。</summary>
    /// <param name="reader">プロセスの一方の出力ストリームです。</param>
    /// <param name="attempt">出力の所有試行です。</param>
    /// <param name="stream">stdoutまたはstderrです。</param>
    /// <param name="cancellation">出力保存の障害時にはプロセス待機にも停止を通知します。</param>
    /// <returns>EOFまでの保存完了を表します。</returns>
    private async Task PumpAsync(StreamReader reader, JobAttempt attempt, string stream, CancellationTokenSource cancellation)
    {
        try
        {
            char[] buffer = new char[8192];
            int sequence = 0;
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation.Token)) > 0)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(buffer, 0, count);
                await storage.PutImmutableAsync($"logs/{attempt.OccurrenceId}/{attempt.Id}/{stream}/{sequence++:D8}.txt", new BinaryData(bytes), cancellation.Token);
            }
        }
        catch
        {
            // 出力の片側が失敗したら、もう片側と終了待機も止めます。パイプ詰まりによる永久待機を防ぎます。
            await cancellation.CancelAsync();
            throw;
        }
    }
}
