using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobRunner.Models;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>実HTTPで予定変更・一覧・単体取得・エラー分類を確認します。</summary>
public sealed class Jp1ReadAndScheduleTests
{
    /// <summary>相対前倒しの既定ADD/NOTSHIFTと再送、単体取得を検証します。</summary>
    /// <returns>HTTPとStorageに保存された結果を確認します。</returns>
    [Fact]
    public async Task RelativeAddAndStatusReadHaveStableContracts()
    {
        await using var scope = new StorageScope();
        scope.Options.SchedulerId = "AJSROOT1";
        string inputName = "http-input-" + Guid.NewGuid().ToString("N");
        scope.Options.DefinitionSources = [new DefinitionSource { BlobPath = inputName + "/jobs" }];
        var storage = new StorageRepository(scope.Options);
        await storage.InitializeAsync(CancellationToken.None);
        var input = storage.Blobs.GetBlobContainerClient(inputName);
        await input.CreateAsync();
        await input.GetBlobClient("jobs").UploadAsync(BinaryData.FromString("unit=net;{ty=n;sd=0,ud;unit=j;{ty=j;te=\"true\";}}"));
        using var app = new AppProcess(scope.Options);
        try
        {
            await WaitReadyAsync(app);
            string register = "/ajs/api/v1/objects/definitions/%2Fnet/actions/registerFixedExec/invoke";
            string time = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddDays(2), TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo")).ToString("yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            using var response = await app.Client.PostAsJsonAsync(register, new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", startTime = time, holding = true } });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            string execId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("execID").GetString()!;
            string resource = "/ajs/api/v1/objects/statuses/" + Uri.EscapeDataString("/net:" + execId);
            var body = new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", kindTime = "RELATIVETIME", relativeSign = "-", relativeDay = 0, relativeTime = "00:30" } };
            for (int i = 0; i < 2; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, resource + "/actions/changeStartTime/invoke") { Content = JsonContent.Create(body) };
                request.Headers.Add("X-JobRunner-Request-Id", "same-relative-add");
                using var changed = await app.Client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            }

            var roots = (await storage.ListAsync("occurrence:", CancellationToken.None)).Select(StorageRepository.Read<Occurrence>).Where(o => o.Id == o.RootId).ToList();
            Assert.Equal(2, roots.Count);
            var original = roots.Single(o => o.Jp1ExecutionId == execId);
            Assert.Equal(original.OriginalDue.AddMinutes(-30), original.Due);
            var single = await app.Client.GetFromJsonAsync<JsonElement>(resource + "?manager=localhost&serviceName=AJSROOT1");
            Assert.Equal("HOLDING", single.GetProperty("unitStatus").GetProperty("status").GetString());
            Assert.Equal("TIME", single.GetProperty("unitStatus").GetProperty("changeType").GetString());
            Assert.Equal(execId, single.GetProperty("unitStatus").GetProperty("execID").GetString());
            var list = await app.Client.GetFromJsonAsync<JsonElement>("/ajs/api/v1/objects/statuses?mode=search&manager=localhost&serviceName=AJSROOT1&location=%2F&searchLowerUnits=YES&generation=NO");
            Assert.True(list.GetProperty("all").GetBoolean());
            Assert.Equal(4, list.GetProperty("statuses").GetArrayLength());
            using var conflict = new HttpRequestMessage(HttpMethod.Post, resource + "/actions/changeStartTime/invoke") { Content = JsonContent.Create(new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", kindTime = "RELATIVETIME", relativeSign = "-", relativeDay = 0, relativeTime = "00:31" } }) };
            conflict.Headers.Add("X-JobRunner-Request-Id", "same-relative-add");
            using var conflictResponse = await app.Client.SendAsync(conflict);
            Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
            using var restore = await app.Client.PostAsJsonAsync(resource + "/actions/restoreChangedSchedule/invoke", new { parameters = new { manager = "localhost", serviceName = "AJSROOT1" } });
            Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
            Assert.All((await storage.ListAsync("occurrence:", CancellationToken.None)).Select(StorageRepository.Read<Occurrence>).Where(o => o.RootId != original.Id), o => Assert.Equal("Cancelled", o.State));

            using var noName = await app.Client.GetAsync("/ajs/api/v1/objects/statuses?mode=search&manager=localhost&serviceName=AJSROOT1&location=%2F&unitNameMatchMethods=EQ");
            Assert.Equal("KNAK3207-E", (await noName.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messageID").GetString());

            foreach (var (parameters, expected) in new (object, string)[]
            {
                (new { manager = "localhost" }, "KNAK3201-E"),
                (new { manager = new string('x', 256), serviceName = "AJSROOT1" }, "KNAK3202-E"),
                (new { manager = "localhost\n", serviceName = "AJSROOT1" }, "KNAK3204-E"),
                (new { manager = 1, serviceName = "AJSROOT1" }, "KNAK3206-E"),
                (new { manager = "localhost", serviceName = "AJSROOT1", futureGeneration = 100 }, "KNAK3203-E"),
                (new { manager = "localhost", serviceName = "AJSROOT1", futureGeneration = "1" }, "KNAK3206-E"),
                ("not an object", "KNAK3206-E"),
                (new { manager = "localhost", serviceName = "AJSROOT1", startTime = time, futureGeneration = 1 }, "KNAK3209-E"),
            })
            {
                using var invalid = await app.Client.PostAsJsonAsync(register, new { parameters });
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                Assert.Equal(expected, (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messageID").GetString());
            }
        }
        finally
        {
            app.Kill();
            await input.DeleteIfExistsAsync();
        }
    }

    /// <summary>定義検索の上限は部分返却と完全性で表し、検索後に判定します。</summary>
    /// <returns>1000件境界と無関係な世代引数の無視を確認します。</returns>
    [Fact]
    public async Task DefinitionSearchReturnsStablePartialResults()
    {
        await using var scope = new StorageScope();
        scope.Options.SchedulerId = "AJSROOT1";
        string inputName = "limit-input-" + Guid.NewGuid().ToString("N");
        scope.Options.DefinitionSources = [new DefinitionSource { BlobPath = inputName + "/jobs" }];
        var storage = new StorageRepository(scope.Options);
        await storage.InitializeAsync(CancellationToken.None);
        var input = storage.Blobs.GetBlobContainerClient(inputName);
        await input.CreateAsync();
        string jobs = string.Concat(Enumerable.Range(0, 1001).Select(i => $"unit=j{i};{{ty=j;te=\"true\";}}"));
        await input.GetBlobClient("jobs").UploadAsync(BinaryData.FromString("unit=net;{ty=n;sd=0,ud;" + jobs + "}"));
        using var app = new AppProcess(scope.Options);
        try
        {
            await WaitReadyAsync(app);
            string query = "/ajs/api/v1/objects/statuses?mode=search&manager=localhost&serviceName=AJSROOT1&location=%2Fnet&searchTarget=DEFINITION&generation=ignored";
            using var over = await app.Client.GetAsync(query);
            Assert.Equal(HttpStatusCode.OK, over.StatusCode);
            var partial = await over.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(partial.GetProperty("all").GetBoolean());
            var names = partial.GetProperty("statuses").EnumerateArray().Select(v => v.GetProperty("definition").GetProperty("simpleUnitName").GetString()).ToArray();
            Assert.Equal(Enumerable.Range(0, 1001).Select(i => "j" + i).Order(StringComparer.Ordinal).Take(1000), names);
            var repeated = await app.Client.GetFromJsonAsync<JsonElement>(query);
            Assert.Equal(partial.ToString(), repeated.ToString());
            var fewer = await app.Client.GetFromJsonAsync<JsonElement>(query + "&unitNameMatchMethods=NC&unitName=j100");
            Assert.Equal(999, fewer.GetProperty("statuses").GetArrayLength());
            Assert.True(fewer.GetProperty("all").GetBoolean());
            var empty = await app.Client.GetFromJsonAsync<JsonElement>(query + "&unitNameMatchMethods=RE&unitName=missing*");
            Assert.Equal(0, empty.GetProperty("statuses").GetArrayLength());
            Assert.True(empty.GetProperty("all").GetBoolean());
            var wildcard = await app.Client.GetFromJsonAsync<JsonElement>(query + "&unitNameMatchMethods=RE&unitName=j%3F");
            Assert.Equal(10, wildcard.GetProperty("statuses").GetArrayLength());
            var exact = await app.Client.GetFromJsonAsync<JsonElement>(query + "&unitNameMatchMethods=NE&unitName=j1000");
            Assert.Equal(1000, exact.GetProperty("statuses").GetArrayLength());
            Assert.True(exact.GetProperty("all").GetBoolean());
            using var invalidPattern = await app.Client.GetAsync(query + "&unitNameMatchMethods=RE&unitName=missing%5C");
            Assert.Equal(HttpStatusCode.BadRequest, invalidPattern.StatusCode);
            Assert.Equal("KNAK3206-E", (await invalidPattern.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messageID").GetString());
        }
        finally
        {
            app.Kill();
            await input.DeleteIfExistsAsync();
        }
    }

    /// <summary>期限内に実アプリの稼働系初期化を待ちます。</summary>
    /// <param name="app">起動したアプリです。</param>
    /// <returns>API利用開始までの待機です。</returns>
    private static async Task WaitReadyAsync(AppProcess app)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                var result = await app.Client.GetFromJsonAsync<JsonElement>("/api/jobrunner/status", timeout.Token);
                if (result.GetProperty("isLeader").GetBoolean())
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // HTTP待受開始前の接続失敗だけ再試行します。
            }

            await Task.Delay(100, timeout.Token);
        }
    }
}
