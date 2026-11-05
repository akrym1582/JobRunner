using System.Diagnostics;
using System.Text;
using System.Text.Json;
using JobRunner.Execution;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using JobRunner.Workers;
using Xunit;
using Xunit.Abstractions;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>末端ジョブ1000件の集中投入を、実Storageと実プロセスで測定します。</summary>
/// <param name="output">測定値をTRXへ保存するxUnit出力です。</param>
[Collection("Scale")]
public sealed class ScaleTests(ITestOutputHelper output)
{
    /// <summary>1000個の安全なプロセスが一度ずつ完了することと開始遅延・要求数・メモリを測定します。</summary>
    /// <returns>実行結果の検証と測定値の保存を表します。</returns>
    [Fact]
    public async Task ExecutesOneThousandLeafJobsUnderConcentratedLoad()
    {
        await using var scope = new StorageScope();
        scope.Options.ConfirmedHorizonDays = 1;
        scope.Options.MaxConcurrency = 32;
        string inputName = "scale-input-" + Guid.NewGuid().ToString("N");
        scope.Options.DefinitionSources = [new DefinitionSource { BlobPath = inputName + "/jobs" }];
        var storage = scope.Repository;
        await storage.InitializeAsync(CancellationToken.None);
        var input = storage.Blobs.GetBlobContainerClient(inputName);
        await input.CreateAsync();
        var source = new StringBuilder("unit=net;{ty=n;sd=0,ud;");
        for (int i = 0; i < 1000; i++)
        {
            source.Append($"unit=j{i:D4};{{ty=j;te=\"true\";}}");
        }

        source.Append('}');
        await input.GetBlobClient("jobs").UploadAsync(BinaryData.FromString(source.ToString()));
        await using var leader = new LeaderCoordinator(storage, TimeProvider.System);
        Assert.True(await leader.AcquireAsync(CancellationToken.None));
        var engine = new SchedulerEngine(storage, new DefinitionService(storage, scope.Options), new ProcessJobExecutor(storage), scope.Options, TimeProvider.System);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await engine.InitializeAsync(leader, timeout.Token);
            var command = new Operation { Id = "scale", Kind = "Register", RegistrationKind = "Immediate", Target = "/net", Due = DateTimeOffset.UtcNow, StateNamespace = engine.Namespace! };
            await storage.Table.AddEntityAsync(storage.Entity("command:scale", command), timeout.Token);
            long initialRequests = storage.RequestCount;
            long peakManagedBytes = GC.GetTotalMemory(false);
            var watch = Stopwatch.StartNew();
            while (true)
            {
                await engine.TickAsync(timeout.Token);
                peakManagedBytes = Math.Max(peakManagedBytes, GC.GetTotalMemory(false));
                var occurrences = (await storage.ListAsync("occurrence:", timeout.Token)).Select(StorageRepository.Read<Occurrence>).ToList();
                if (occurrences.Count == 1001 && occurrences.All(o => o.State == "Succeeded"))
                {
                    break;
                }

                await Task.Delay(20, timeout.Token);
            }

            watch.Stop();
            var attempts = (await storage.ListAsync("attempt:", timeout.Token)).Select(StorageRepository.Read<JobAttempt>).ToList();
            Assert.Equal(1000, attempts.Count);
            Assert.Equal(1000, attempts.Select(a => a.OccurrenceId).Distinct().Count());
            Assert.All(attempts, a => Assert.Equal("Succeeded", a.State));
            var metrics = new
            {
                Jobs = attempts.Count,
                Distribution = "concentrated",
                MaxConcurrency = scope.Options.MaxConcurrency,
                ElapsedSeconds = watch.Elapsed.TotalSeconds,
                StorageRequests = storage.RequestCount - initialRequests,
                PeakManagedBytes = peakManagedBytes,
                MaxStartDelaySeconds = attempts.Max(a => (a.StartedAt - command.Due!.Value).TotalSeconds),
            };
            string json = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
            output.WriteLine(json);
            string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../TestResults"));
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "scale.json"), json, timeout.Token);
        }
        finally
        {
            await engine.StopAsync();
            await input.DeleteIfExistsAsync();
        }
    }
}
