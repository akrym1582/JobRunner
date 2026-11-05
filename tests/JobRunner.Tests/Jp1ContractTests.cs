using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobRunner.Models;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>V12の即時登録と保留変更のHTTP書式を実アプリに対して検証します。</summary>
public sealed class Jp1ContractTests
{
    /// <summary>公式URL・parameters・execID・同期200・保留解除の空ボディを確認します。</summary>
    /// <returns>実HTTP、Storage、ローカルジョブを通した契約テストです。</returns>
    [Fact]
    public async Task ImmediateRegistrationAndHoldUseV12Contract()
    {
        await using var scope = new StorageScope();
        scope.Options.SchedulerId = "AJSROOT1";
        var storage = new StorageRepository(scope.Options);
        string inputName = "jp1-input-" + Guid.NewGuid().ToString("N");
        scope.Options.DefinitionSources = [new DefinitionSource { BlobPath = inputName + "/jobs", TargetPath = "/業務" }];
        await storage.InitializeAsync(CancellationToken.None);
        var input = storage.Blobs.GetBlobContainerClient(inputName);
        await input.CreateAsync();
        await input.GetBlobClient("jobs").UploadAsync(BinaryData.FromString("unit=net;{ty=n;sd=0,ud;unit=j;{ty=j;te=\"printf jp1-contract\";}}"));
        using var app = new AppProcess(scope.Options);
        try
        {
            await WaitAsync(async () =>
            {
                try
                {
                    var status = await app.Client.GetFromJsonAsync<JsonElement>("/api/jobrunner/status");
                    return status.GetProperty("isLeader").GetBoolean();
                }
                catch (HttpRequestException)
                {
                    return false;
                }
            });
            string unit = Uri.EscapeDataString("/業務/net");
            string register = $"/ajs/api/v1/objects/definitions/{unit}/actions/registerImmediateExec/invoke";
            using var missing = await app.Client.PostAsJsonAsync(register, new { parameters = new { manager = "localhost" } });
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            var error = await missing.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ajs", error.GetProperty("application").GetString());
            Assert.Equal("KNAK3201-E", error.GetProperty("messageID").GetString());
            Assert.Equal("serviceName", error.GetProperty("extensions")[2].GetProperty("value").GetString());
            using var unsupported = await app.Client.PostAsJsonAsync(register, new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", startCondition = new { times = 1 } } });
            Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
            Assert.Empty(await storage.ListAsync("occurrence:", CancellationToken.None));
            var body = new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", holding = true } };
            using var firstRequest = new HttpRequestMessage(HttpMethod.Post, register) { Content = JsonContent.Create(body) };
            firstRequest.Headers.Add("X-JobRunner-Request-Id", "same-request");
            using var first = await app.Client.SendAsync(firstRequest);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.True(first.Headers.CacheControl!.NoStore);
            string id = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("execID").GetString()!;
            Assert.Matches("^@[0-9]{0,4}[A-Z][0-9]{4}$", id);
            using var duplicateRequest = new HttpRequestMessage(HttpMethod.Post, register) { Content = JsonContent.Create(body) };
            duplicateRequest.Headers.Add("X-JobRunner-Request-Id", "same-request");
            using var duplicate = await app.Client.SendAsync(duplicateRequest);
            Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
            Assert.Equal(id, (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("execID").GetString());
            var before = (await storage.ListAsync("occurrence:", CancellationToken.None)).Select(StorageRepository.Read<Occurrence>).ToList();
            Assert.Equal(2, before.Count);
            Assert.Equal("Held", Assert.Single(before, o => o.Id == o.RootId).State);
            string resource = Uri.EscapeDataString("/業務/net:" + id);
            using var released = await app.Client.PostAsJsonAsync($"/ajs/api/v1/objects/statuses/{resource}/actions/changeHold/invoke", new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", holdAttr = "RELEASE" } });
            Assert.Equal(HttpStatusCode.OK, released.StatusCode);
            Assert.Empty(await released.Content.ReadAsByteArrayAsync());
            await WaitAsync(async () => (await storage.ListAsync("occurrence:", CancellationToken.None)).Select(StorageRepository.Read<Occurrence>).All(o => o.State == "Succeeded"));
            Assert.Single(await storage.ListAsync("attempt:", CancellationToken.None));
        }
        finally
        {
            app.Kill();
            await input.DeleteIfExistsAsync();
        }
    }

    /// <summary>未来世代数指定の確定登録を同期200・空本文で返し、再送で世代を増やしません。</summary>
    /// <returns>実HTTPから確定した2世代と初回だけの保留を確認します。</returns>
    [Fact]
    public async Task FixedRegistrationMaintainsRequestedFutureGenerations()
    {
        await using var scope = new StorageScope();
        scope.Options.SchedulerId = "AJSROOT1";
        var storage = new StorageRepository(scope.Options);
        string inputName = "fixed-input-" + Guid.NewGuid().ToString("N");
        scope.Options.DefinitionSources = [new DefinitionSource { BlobPath = inputName + "/jobs" }];
        await storage.InitializeAsync(CancellationToken.None);
        var input = storage.Blobs.GetBlobContainerClient(inputName);
        await input.CreateAsync();
        string day = DateTimeOffset.UtcNow.AddDays(2).ToString("yyyy/M/d", System.Globalization.CultureInfo.InvariantCulture);
        await input.GetBlobClient("jobs").UploadAsync(BinaryData.FromString($"unit=net;{{ty=n;sd={day};st=10:00;cy=(1,d);sh=no;}}"));
        using var app = new AppProcess(scope.Options);
        try
        {
            await WaitAsync(async () =>
            {
                try
                {
                    var status = await app.Client.GetFromJsonAsync<JsonElement>("/api/jobrunner/status");
                    return status.GetProperty("isLeader").GetBoolean();
                }
                catch (HttpRequestException)
                {
                    return false;
                }
            });
            string url = "/ajs/api/v1/objects/definitions/%2Fnet/actions/registerFixedExec/invoke";
            using var bad = await app.Client.PostAsJsonAsync(url, new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", futureGeneration = 100, passedDaemonStarts = "NEXTTIME" } });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            for (int i = 0; i < 2; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", futureGeneration = 2, passedDaemonStarts = "NEXTTIME", holding = true } }),
                };
                request.Headers.Add("X-JobRunner-Request-Id", "fixed-count");
                using var response = await app.Client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            }

            var registration = Assert.Single((await storage.ListAsync("registration:", CancellationToken.None)).Select(StorageRepository.Read<Registration>), r => r.FutureGenerations == 2);
            var roots = (await storage.ListAsync("occurrence:", CancellationToken.None)).Select(StorageRepository.Read<Occurrence>).Where(o => o.RegistrationId == registration.Id).OrderBy(o => o.Due).ToList();
            Assert.Equal(2, roots.Count);
            Assert.Equal("Held", roots[0].State);
            Assert.Equal("Waiting", roots[1].State);
            string from = DateTimeOffset.UtcNow.AddDays(10).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            string to = DateTimeOffset.UtcNow.AddDays(12).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            using var period = await app.Client.PostAsJsonAsync(url, new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", fixedScheduleFrom = from, fixedScheduleTo = to, passedDaemonStarts = "NEXTTIME", holding = true } });
            Assert.Equal(HttpStatusCode.OK, period.StatusCode);
            Assert.Empty(await period.Content.ReadAsByteArrayAsync());
            var periodRegistration = Assert.Single((await storage.ListAsync("registration:", CancellationToken.None)).Select(StorageRepository.Read<Registration>), r => r.Start is not null);
            Assert.Equal(3, (await storage.ListAsync("occurrence:", CancellationToken.None)).Select(StorageRepository.Read<Occurrence>).Count(o => o.RegistrationId == periodRegistration.Id));
            using var dated = await app.Client.PostAsJsonAsync(url, new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", startTime = from + "T10:00", holding = true } });
            Assert.Equal(HttpStatusCode.OK, dated.StatusCode);
            string executionId = (await dated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("execID").GetString()!;
            string statusUrl = "/ajs/api/v1/objects/statuses/" + Uri.EscapeDataString("/net:" + executionId) + "/actions/";
            using var moved = await app.Client.PostAsJsonAsync(statusUrl + "changeStartTime/invoke", new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", kindTime = "ABSOLUTETIME", absoluteTime = to + "T11:00", changeLower = "SHIFT", pushedAhead = "MOVE" } });
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
            foreach (string action in new[] { "restoreChangedSchedule", "cancelExecSchedule" })
            {
                using var operated = await app.Client.PostAsJsonAsync(statusUrl + action + "/invoke", new { parameters = new { manager = "localhost", serviceName = "AJSROOT1" } });
                Assert.Equal(HttpStatusCode.OK, operated.StatusCode);
                Assert.Empty(await operated.Content.ReadAsByteArrayAsync());
            }

            using var rerun = await app.Client.PostAsJsonAsync(statusUrl + "rerun/invoke", new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", rerunMethod = "TOP", rerunOptions = new[] { "HOLD" } } });
            Assert.Equal(HttpStatusCode.OK, rerun.StatusCode);
            using var unregistered = await app.Client.PostAsJsonAsync("/ajs/api/v1/objects/definitions/%2Fnet/actions/cancelRegistration/invoke", new { parameters = new { manager = "localhost", serviceName = "AJSROOT1" } });
            Assert.Equal(HttpStatusCode.OK, unregistered.StatusCode);
            Assert.All((await storage.ListAsync("registration:", CancellationToken.None)).Select(StorageRepository.Read<Registration>), r => Assert.True(r.Disabled));
        }
        finally
        {
            app.Kill();
            await input.DeleteIfExistsAsync();
        }
    }

    /// <summary>固定待機せず、実HTTPの起動と完了を期限付きで観測します。</summary>
    /// <param name="condition">成立が必要な観測条件です。</param>
    /// <returns>条件成立までの非同期タスクです。</returns>
    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100, timeout.Token);
        }

        Assert.Fail("HTTPの観測条件が期限までに成立しませんでした。");
    }
}
