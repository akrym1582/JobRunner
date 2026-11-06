using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobRunner.Models;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>公式一覧の不足APIを実HTTPと保存済み結果で検証します。</summary>
public sealed class AdditionalHttpTests
{
    /// <summary>製品情報・監視の省略引数・状態変更と完了ログを確認します。</summary>
    /// <returns>実アプリによる契約検証です。</returns>
    [Fact]
    public async Task AdditionalApisReturnPersistedResults()
    {
        await using var scope = new StorageScope();
        scope.Options.SchedulerId = "AJSROOT1";
        string inputName = "api-list-" + Guid.NewGuid().ToString("N");
        scope.Options.DefinitionSources = [new DefinitionSource { BlobPath = inputName + "/jobs" }];
        var storage = new StorageRepository(scope.Options);
        await storage.InitializeAsync(CancellationToken.None);
        var input = storage.Blobs.GetBlobContainerClient(inputName);
        await input.CreateAsync();
        await input.GetBlobClient("jobs").UploadAsync(BinaryData.FromString("unit=net;{ty=n;sd=0,ud;unit=j;{ty=j;te=\"printf output; printf error >&2\";}}"));
        using var app = new AppProcess(scope.Options);
        try
        {
            await ReadyAsync(app);
            var version = await app.Client.GetFromJsonAsync<JsonElement>("/ajs/api/v1/version?manager=localhost");
            Assert.Equal("JobRunner", version.GetProperty("productName").GetString());
            Assert.True(version.GetProperty("productVersionNumber").GetInt32() > 0);
            using var protocol = await app.Client.GetAsync("/ajs/api/v1/protocolVersion?manager=localhost");
            Assert.Equal(HttpStatusCode.NotImplemented, protocol.StatusCode);
            using var missingManager = await app.Client.GetAsync("/ajs/api/v1/version");
            Assert.Equal("KNAK3201-E", (await missingManager.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messageID").GetString());
            string register = "/ajs/api/v1/objects/definitions/%2Fnet/actions/registerImmediateExec/invoke";
            using var held = await app.Client.PostAsJsonAsync(register, new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", holding = true } });
            Assert.Equal(HttpStatusCode.OK, held.StatusCode);
            string heldId = (await held.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("execID").GetString()!;
            string rootResource = Resource("/net", heldId);
            using var delay = await app.Client.PostAsJsonAsync(rootResource + "/actions/changeDelayMonitor/invoke", new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", delayedStart = "OWN", delayedStartTime = "00:01", delayedEnd = "NOT", delayedEndTime = 999 } });
            Assert.Equal(HttpStatusCode.OK, delay.StatusCode);
            var state = await app.Client.GetFromJsonAsync<JsonElement>(rootResource + Query());
            Assert.Equal("OWN", state.GetProperty("unitStatus").GetProperty("delayedStart").GetString());
            Assert.Equal("00:01", state.GetProperty("unitStatus").GetProperty("delayedStartTime").GetString());
            foreach (object unused in new object[] { "invalid", 42 })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, rootResource + "/actions/changeDelayMonitor/invoke") { Content = JsonContent.Create(new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", delayedEnd = "NOT", delayedEndTime = unused } }) };
                request.Headers.Add("X-JobRunner-Request-Id", "same-no-monitor");
                using var same = await app.Client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, same.StatusCode);
            }

            using var alreadyDelayed = await app.Client.PostAsJsonAsync(rootResource + "/actions/changeDelayMonitor/invoke", new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", delayedStart = "ABSOLUTE", delayedStartTime = "00:00" } });
            Assert.Equal(HttpStatusCode.OK, alreadyDelayed.StatusCode);
            var delayed = await app.Client.GetFromJsonAsync<JsonElement>("/ajs/api/v1/objects/statuses?mode=search&manager=localhost&serviceName=AJSROOT1&location=%2F&searchLowerUnits=YES&generation=EXECID&execID=" + Uri.EscapeDataString(heldId) + "&delayStatus=START");
            Assert.Contains(delayed.GetProperty("statuses").EnumerateArray(), x => x.GetProperty("unitStatus").GetProperty("execID").GetString() == heldId);
            using var badDelay = await app.Client.PostAsJsonAsync(rootResource + "/actions/changeDelayMonitor/invoke", new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", delayedStart = "ROOT", delayedStartTime = "00:01" } });
            Assert.Equal(HttpStatusCode.Conflict, badDelay.StatusCode);
            string heldJob = Resource("/net/j", heldId);
            using var change = await app.Client.PostAsJsonAsync(heldJob + "/actions/changeStatus/invoke", new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", newStatus = "RETURNCODE", newReturnCode = "4" } });
            Assert.Equal(HttpStatusCode.OK, change.StatusCode);
            state = await app.Client.GetFromJsonAsync<JsonElement>(heldJob + Query());
            Assert.Equal("ABNORMAL", state.GetProperty("unitStatus").GetProperty("status").GetString());
            Assert.Equal("4", state.GetProperty("unitStatus").GetProperty("retCode").GetString());
            using var invalid = await app.Client.PostAsJsonAsync(heldJob + "/actions/changeStatus/invoke", new { parameters = new { manager = "localhost", serviceName = "AJSROOT1", newReturnCode = 4 } });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var heldInterrupt = await app.Client.PostAsJsonAsync(rootResource + "/actions/interrupt/invoke", new { parameters = new { manager = "localhost", serviceName = "AJSROOT1" } });
            Assert.Equal(HttpStatusCode.Conflict, heldInterrupt.StatusCode);

            using var started = await app.Client.PostAsJsonAsync(register, new { parameters = new { manager = "localhost", serviceName = "AJSROOT1" } });
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
            string execId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("execID").GetString()!;
            string jobResource = Resource("/net/j", execId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                state = await app.Client.GetFromJsonAsync<JsonElement>(jobResource + Query(), timeout.Token);
                if (state.GetProperty("unitStatus").GetProperty("status").GetString() == "NORMAL")
                {
                    break;
                }

                await Task.Delay(50, timeout.Token);
            }

            var details = await app.Client.GetFromJsonAsync<JsonElement>(jobResource + "/actions/execResultDetails/invoke" + Query());
            Assert.True(details.GetProperty("all").GetBoolean());
            Assert.Contains("[stdout]\noutput", details.GetProperty("execResultDetails").GetString());
            Assert.Contains("[stderr]\nerror", details.GetProperty("execResultDetails").GetString());
            var rows = (await storage.ListAsync("occurrence:", CancellationToken.None)).Select(StorageRepository.Read<Occurrence>).ToList();
            var root = rows.Single(o => o.Jp1ExecutionId == execId);
            var job = rows.Single(o => o.RootId == root.Id && o.UnitPath == "/net/j");
            await storage.Container.GetBlobClient($"logs/{job.Id}/{job.AttemptId}/stdout/00000000.txt").UploadAsync(BinaryData.FromString(new string('x', (5 * 1024 * 1024) + 10)), overwrite: true);
            details = await app.Client.GetFromJsonAsync<JsonElement>(jobResource + "/actions/execResultDetails/invoke" + Query());
            Assert.False(details.GetProperty("all").GetBoolean());
            Assert.Equal(5 * 1024 * 1024, details.GetProperty("execResultDetails").GetString()!.Length);
            while (true)
            {
                var rootState = await app.Client.GetFromJsonAsync<JsonElement>(Resource("/net", execId) + Query(), timeout.Token);
                if (rootState.GetProperty("unitStatus").GetProperty("status").GetString() == "NORMAL")
                {
                    break;
                }

                await Task.Delay(50, timeout.Token);
            }

            var rootRow = (await storage.GetAsync("occurrence:" + root.Id, CancellationToken.None))!;
            root = StorageRepository.Read<Occurrence>(rootRow);
            var jobRow = (await storage.GetAsync("occurrence:" + job.Id, CancellationToken.None))!;
            var attemptRow = (await storage.GetAsync("attempt:" + job.AttemptId, CancellationToken.None))!;
            var archive = new HistoryArchive { RootId = root.Id, ArchivedAt = DateTimeOffset.UtcNow, Rows = new[] { rootRow, jobRow, attemptRow }.Select(r => new ArchivedRow(r.RowKey, r.ETag.ToString(), r.GetString("Json"))).ToList() };
            root.ArchivePath = "history-test/" + root.Id + ".json";
            await storage.Container.GetBlobClient(root.ArchivePath).UploadAsync(BinaryData.FromObjectAsJson(archive));
            root.State = "Archived";
            await storage.Table.UpdateEntityAsync(storage.Entity(rootRow.RowKey, root), rootRow.ETag, Azure.Data.Tables.TableUpdateMode.Replace);
            await storage.Table.DeleteEntityAsync(jobRow.PartitionKey, jobRow.RowKey, jobRow.ETag);
            await storage.Table.DeleteEntityAsync(attemptRow.PartitionKey, attemptRow.RowKey, attemptRow.ETag);
            details = await app.Client.GetFromJsonAsync<JsonElement>(jobResource + "/actions/execResultDetails/invoke" + Query());
            Assert.False(details.GetProperty("all").GetBoolean());
            Assert.Equal(5 * 1024 * 1024, details.GetProperty("execResultDetails").GetString()!.Length);
        }
        finally
        {
            await input.DeleteIfExistsAsync();
        }
    }

    /// <summary>ユニット完全名と実行IDを一度だけ符号化します。</summary>
    /// <param name="path">完全名です。</param>
    /// <param name="id">実行IDです。</param>
    /// <returns>状態リソースです。</returns>
    private static string Resource(string path, string id) => "/ajs/api/v1/objects/statuses/" + Uri.EscapeDataString(path + ":" + id);

    /// <summary>ローカルの接続先を明示します。</summary>
    /// <returns>参照クエリです。</returns>
    private static string Query() => "?manager=localhost&serviceName=AJSROOT1";

    /// <summary>実行器の初期化完了を有限時間待ちます。</summary>
    /// <param name="app">実アプリです。</param>
    /// <returns>起動確認です。</returns>
    private static async Task ReadyAsync(AppProcess app)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                var status = await app.Client.GetFromJsonAsync<JsonElement>("/api/jobrunner/status", timeout.Token);
                if (status.GetProperty("isLeader").GetBoolean())
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // 待受前の接続失敗だけを再試行します。
            }

            await Task.Delay(100, timeout.Token);
        }
    }
}
