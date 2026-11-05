using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobRunner.Models;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>実HTTP・バックグラウンド処理・Azurite・安全な実プロセスを一体で検証します。</summary>
public sealed class IntegrationTests
{
    /// <summary>待機系APIからの操作が一度だけ実行され、停止後も履歴と登録を維持することを確認します。</summary>
    /// <returns>二つのアプリプロセスとStorageを片付けるまでのタスクです。</returns>
    [Fact]
    public async Task RunsRealProcessesThroughStandbyApiAndRecovers()
    {
        string suffix = Guid.NewGuid().ToString("N");
        var options = new RunnerOptions
        {
            ManagementContainer = "test-" + suffix,
            TableName = "Test" + suffix,
            SchedulerId = suffix,
            DefinitionSources = [new DefinitionSource { BlobPath = "input-" + suffix + "/jobs", TargetPath = "/業務" }, new DefinitionSource { BlobPath = "input-" + suffix + "/calendar" }],
        };
        var storage = new StorageRepository(options);
        var input = storage.Blobs.GetBlobContainerClient("input-" + suffix);
        await input.CreateAsync();
        const string jobSource = "unit=net;{ty=n;sd=0,ud;ar=(f=a,t=b,seq);unit=a;{ty=j;te=\"printf first; printf diagnostic >&2\";}unit=b;{ty=j;ha=y;te=\"printf second\";}}unit=longnet;{ty=n;sd=0,ud;ar=(f=slow,t=after,seq);unit=slow;{ty=j;te=\"sleep 120\";}unit=after;{ty=j;te=\"printf after\";}}";
        const string calendarSource = "unit=calendar;{ty=g;cl=su;}";
        await input.GetBlobClient("jobs").UploadAsync(BinaryData.FromString(jobSource));
        await input.GetBlobClient("calendar").UploadAsync(BinaryData.FromString(calendarSource));
        using var first = new AppProcess(options);
        using var second = new AppProcess(options);
        try
        {
            await WaitAsync(async () => await IsReadyAsync(first.Client) && await IsReadyAsync(second.Client), 30);
            await WaitAsync(async () => await IsLeaderAsync(first.Client) != await IsLeaderAsync(second.Client), 30);
            var leader = await IsLeaderAsync(first.Client) ? first : second;
            var standby = leader == first ? second : first;
            var sources = new List<SourceEvidence>
            {
                new() { Mapping = options.DefinitionSources[0], Base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(jobSource)) },
                new() { Mapping = options.DefinitionSources[1], Base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(calendarSource)) },
            };
            Assert.Equal(HttpStatusCode.OK, (await standby.Client.PostAsJsonAsync("/api/jobrunner/definitions/validate", sources)).StatusCode);
            var imported = await standby.Client.PostAsJsonAsync("/api/jobrunner/definitions/import", sources);
            Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
            string version = (await imported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString()!;
            var preview = await standby.Client.PostAsJsonAsync("/api/jobrunner/schedules/preview", new PreviewRequest(version, "/業務/net", new(2026, 11, 5), new(2026, 11, 5), new(2026, 12, 5)));
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Empty((await preview.Content.ReadFromJsonAsync<List<JsonElement>>())!);
            var request = new Operation { Id = "run-once", Kind = "Register", Target = "/業務/net", RegistrationKind = "Immediate", Due = DateTimeOffset.UtcNow };
            var response = await standby.Client.PostAsJsonAsync("/api/jobrunner/operations", request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var duplicate = await standby.Client.PostAsJsonAsync("/api/jobrunner/operations", request);
            Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
            request.Target = "/different";
            Assert.Equal(HttpStatusCode.Conflict, (await standby.Client.PostAsJsonAsync("/api/jobrunner/operations", request)).StatusCode);
            await WaitAsync(async () => (await ExecutionsAsync(standby.Client)).Any(o => o.State == "Succeeded"), 30);
            var held = Assert.Single(await ExecutionsAsync(standby.Client), o => o.State == "Held");
            await standby.Client.PostAsJsonAsync("/api/jobrunner/operations", new Operation { Id = "release", Kind = "Release", Target = held.Id, ExpectedState = "Held" });
            await WaitAsync(async () => (await ExecutionsAsync(standby.Client)).Count(o => o.State == "Succeeded") == 3, 30);
            var completed = await ExecutionsAsync(standby.Client);
            var job = Assert.Single(completed, o => o.UnitPath.EndsWith("/a", StringComparison.Ordinal));
            var attempts = await standby.Client.GetFromJsonAsync<List<JobAttempt>>($"/api/jobrunner/executions/{job.Id}/attempts");
            var attempt = Assert.Single(attempts!);
            Assert.Equal(0, attempt.ExitCode);
            Assert.True(attempt.LogsComplete);
            var logs = await standby.Client.GetFromJsonAsync<List<string>>($"/api/jobrunner/executions/{job.Id}/logs");
            Assert.Equal(2, logs!.Count);
            string stdout = await standby.Client.GetStringAsync("/api/jobrunner/logs?path=" + Uri.EscapeDataString(Assert.Single(logs, p => p.Contains("stdout", StringComparison.Ordinal))));
            Assert.Equal("first", stdout);
            string stateNamespace = completed[0].Namespace;
            await standby.Client.PostAsJsonAsync("/api/jobrunner/operations", new Operation { Id = "long-run", Kind = "Register", Target = "/業務/longnet", RegistrationKind = "Immediate", Due = DateTimeOffset.UtcNow });
            await WaitAsync(async () => (await ExecutionsAsync(standby.Client)).Any(o => o.UnitPath.EndsWith("/slow", StringComparison.Ordinal) && o.State == "Running"), 30);
            leader.Kill();
            await WaitAsync(() => IsLeaderAsync(standby.Client), 85);
            Assert.All(await ExecutionsAsync(standby.Client), o => Assert.Equal(stateNamespace, o.Namespace));
            Assert.Equal(3, (await ExecutionsAsync(standby.Client)).Count(o => o.State == "Succeeded"));
            var after = await standby.Client.GetFromJsonAsync<List<JobAttempt>>($"/api/jobrunner/executions/{job.Id}/attempts");
            Assert.Single(after!);
            var unknown = Assert.Single(await ExecutionsAsync(standby.Client), o => o.State == "Unknown");
            Assert.Single(await ExecutionsAsync(standby.Client), o => o.UnitPath.EndsWith("/after", StringComparison.Ordinal) && o.State == "Waiting");
            var resolve = new Operation { Id = "resolve", ExpectedState = "Unknown", AttemptId = unknown.AttemptId, Reason = "テスト用sleepプロセスの停止を確認。業務更新なし。", Resolution = "Succeeded" };
            Assert.Equal(HttpStatusCode.Accepted, (await standby.Client.PostAsJsonAsync($"/api/jobrunner/executions/{unknown.Id}/resolve", resolve)).StatusCode);
            await WaitAsync(async () => (await ExecutionsAsync(standby.Client)).Count(o => o.State == "Succeeded") == 6, 30);
        }
        finally
        {
            first.Kill();
            second.Kill();
            await input.DeleteIfExistsAsync();
            await storage.Container.DeleteIfExistsAsync();
            await storage.Table.DeleteAsync();
        }
    }

    /// <summary>HTTP接続準備とStorage到達を同時に確認します。</summary>
    /// <param name="client">対象プロセスのクライアントです。</param>
    /// <returns>受付可能ならtrueです。</returns>
    private static async Task<bool> IsReadyAsync(HttpClient client)
    {
        try
        {
            return (await client.GetAsync("/health/ready")).IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>HTTP上の状態から初期化完了した稼働系を判定します。</summary>
    /// <param name="client">対象プロセスのクライアントです。</param>
    /// <returns>稼働系であればtrueです。</returns>
    private static async Task<bool> IsLeaderAsync(HttpClient client)
    {
        var status = await client.GetFromJsonAsync<JsonElement>("/api/jobrunner/status");
        return status.GetProperty("isLeader").GetBoolean();
    }

    /// <summary>APIと永続化経路を通して世代一覧を取得します。</summary>
    /// <param name="client">任意の稼働中インスタンスです。</param>
    /// <returns>保存済みの世代一覧です。</returns>
    private static async Task<List<Occurrence>> ExecutionsAsync(HttpClient client) => (await client.GetFromJsonAsync<List<Occurrence>>("/api/jobrunner/executions"))!;

    /// <summary>固定睡眠ではなく観測条件を期限付きで待ちます。</summary>
    /// <param name="condition">満たす必要がある外部観測です。</param>
    /// <param name="seconds">失敗と判定する最大秒数です。</param>
    /// <returns>条件成立で完了し、期限切れはテストを失敗させます。</returns>
    private static async Task WaitAsync(Func<Task<bool>> condition, int seconds)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"外部観測条件が{seconds}秒以内に成立しませんでした。");
    }
}
