using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobRunner.Controllers.Ui;
using JobRunner.Models;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>試行を固定したログ読取の境界と退避履歴を確認します。</summary>
public sealed class UiLogTests
{
    /// <summary>再実行待ち・履歴退避後も過去試行を読み、所属外のログは読みません。</summary>
    /// <returns>実HTTP・Blobの検証です。</returns>
    [Fact]
    public async Task LogsKeepAttemptsAndBoundPagesAcrossArchiving()
    {
        await using var scope = new StorageScope();
        string inputName = "ui-logs-" + Guid.NewGuid().ToString("N");
        scope.Options.DefinitionSources = [new DefinitionSource { BlobPath = inputName + "/jobs" }];
        await scope.Repository.InitializeAsync(CancellationToken.None);
        var input = scope.Repository.Blobs.GetBlobContainerClient(inputName);
        await input.CreateAsync();
        await input.GetBlobClient("jobs").UploadAsync(BinaryData.FromString("unit=net;{ty=n;sd=0,ud;el=job,j,+80 +48;unit=job;{ty=j;te=\"true\";}}"));
        await TestDefinitions.SeedHttpAsync(scope.Repository, scope.Options);
        using var app = new AppProcess(scope.Options);
        try
        {
            Catalog? catalog = null;
            for (int i = 0; i < 100 && catalog is null; i++)
            {
                try
                {
                    using var response = await app.Client.GetAsync("/api/jobrunner/ui/jobnets");
                    if (response.IsSuccessStatusCode)
                    {
                        catalog = await response.Content.ReadFromJsonAsync<Catalog>();
                    }
                }
                catch (HttpRequestException)
                {
                    // HTTPリスナーの起動を待ちます。
                }

                if (catalog is null)
                {
                    await Task.Delay(100);
                }
            }

            Assert.NotNull(catalog);
            var root = new Occurrence { Id = "log-root", RootId = "log-root", Namespace = catalog.Namespace, RootPath = "/net", UnitPath = "/net", State = "Unknown", Started = true, DefinitionHash = catalog.DefinitionHash };
            var child = new Occurrence { Id = "log-job", RootId = root.Id, Namespace = root.Namespace, RootPath = "/net", UnitPath = "/net/job", State = "Unknown", Started = true, AttemptId = null };
            var older = new JobAttempt { Id = "old", OccurrenceId = child.Id, State = "Failed", StartedAt = DateTimeOffset.Parse("2030-01-01T00:00:00Z"), FinishedAt = DateTimeOffset.Parse("2030-01-01T00:01:00Z"), ExitCode = 1, LogsComplete = true };
            var newer = new JobAttempt { Id = "new", OccurrenceId = child.Id, State = "Unknown", StartedAt = older.StartedAt.AddDays(1), LogsComplete = false };
            foreach (var occurrence in new[] { root, child })
            {
                await scope.Repository.Table.AddEntityAsync(scope.Repository.Entity("occurrence:" + occurrence.Id, occurrence));
            }

            foreach (var attempt in new[] { older, newer })
            {
                await scope.Repository.Table.AddEntityAsync(scope.Repository.Entity("attempt:" + attempt.Id, attempt));
            }

            string query = "?rootId=log-root&occurrenceId=log-job";
            string logs = "/api/jobrunner/ui/logs" + query + "&attemptId=old&stream=stdout";
            var attempts = await app.Client.GetFromJsonAsync<AttemptPage>("/api/jobrunner/ui/attempts" + query);
            Assert.Equal(new[] { "new", "old" }, attempts!.Items.Select(a => a.Id));
            Assert.Null(attempts.CurrentAttemptId);
            var empty = await app.Client.GetFromJsonAsync<LogPage>(logs);
            Assert.Empty(empty!.Chunks);
            Assert.True(empty.LogsComplete);
            Assert.False((await app.Client.GetFromJsonAsync<LogPage>(logs.Replace("attemptId=old", "attemptId=new", StringComparison.Ordinal)))!.LogsComplete);
            for (int i = 0; i < 40; i++)
            {
                await scope.Repository.Container.GetBlobClient($"logs/log-job/old/stdout/{i:D8}.txt").UploadAsync(BinaryData.FromString($"日本語 {i} <script>alert(1)</script>\n"));
            }

            await scope.Repository.Container.GetBlobClient("logs/log-job/old/stderr/00000000.txt").UploadAsync(BinaryData.FromString("エラー情報"));
            var tail = await app.Client.GetFromJsonAsync<LogPage>(logs);
            Assert.Equal(Enumerable.Range(24, 16), tail!.Chunks.Select(c => c.Sequence));
            Assert.Equal("before:24", tail.PreviousCursor);
            Assert.Equal("after:39", tail.NextCursor);
            Assert.False(tail.HasMore);
            var previous = await app.Client.GetFromJsonAsync<LogPage>(logs + "&cursor=" + tail.PreviousCursor);
            Assert.Equal(Enumerable.Range(8, 16), previous!.Chunks.Select(c => c.Sequence));
            var first = await app.Client.GetFromJsonAsync<LogPage>(logs + "&cursor=before:8");
            Assert.Equal(8, first!.Chunks.Count);
            Assert.Null(first.PreviousCursor);
            Assert.True(first.HasMore);
            var after = await app.Client.GetFromJsonAsync<LogPage>(logs + "&cursor=after:0");
            Assert.Equal(Enumerable.Range(1, 16), after!.Chunks.Select(c => c.Sequence));
            Assert.True(after.HasMore);
            Assert.Empty((await app.Client.GetFromJsonAsync<LogPage>(logs + "&cursor=after:39"))!.Chunks);
            Assert.Equal("エラー情報", Assert.Single((await app.Client.GetFromJsonAsync<LogPage>(logs.Replace("stdout", "stderr", StringComparison.Ordinal)))!.Chunks).Text);
            foreach (string invalid in new[] { "&cursor=after:-1", "&cursor=bad", "&cursor=before:2147483648" })
            {
                Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync(logs + invalid)).StatusCode);
            }

            Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync(logs.Replace("stdout", "../stderr", StringComparison.Ordinal))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync(logs.Replace("attemptId=old", "attemptId=missing", StringComparison.Ordinal))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync(logs.Replace("occurrenceId=log-job", "occurrenceId=log-root", StringComparison.Ordinal))).StatusCode);
            Assert.Empty((await app.Client.GetFromJsonAsync<AttemptPage>("/api/jobrunner/ui/attempts?rootId=log-root&occurrenceId=log-root"))!.Items);
            var foreign = new Occurrence { Id = "foreign", RootId = "foreign", Namespace = "old-namespace" };
            await scope.Repository.Table.AddEntityAsync(scope.Repository.Entity("occurrence:foreign", foreign));
            Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync(logs.Replace("rootId=log-root", "rootId=foreign", StringComparison.Ordinal))).StatusCode);
            var wrong = new Occurrence { Id = "wrong", RootId = "elsewhere", Namespace = root.Namespace };
            await scope.Repository.Table.AddEntityAsync(scope.Repository.Entity("occurrence:wrong", wrong));
            Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync(logs.Replace("occurrenceId=log-job", "occurrenceId=wrong", StringComparison.Ordinal))).StatusCode);
            var archive = new HistoryArchive { RootId = root.Id, Rows = [new ArchivedRow("occurrence:" + root.Id, string.Empty, JsonSerializer.Serialize(root)), new ArchivedRow("occurrence:" + child.Id, string.Empty, JsonSerializer.Serialize(child)), new ArchivedRow("attempt:old", string.Empty, JsonSerializer.Serialize(older)), new ArchivedRow("attempt:new", string.Empty, JsonSerializer.Serialize(newer))] };
            root.ArchivePath = "history/log-test.json";
            await scope.Repository.Container.GetBlobClient(root.ArchivePath).UploadAsync(BinaryData.FromObjectAsJson(archive));
            await scope.Repository.Table.UpsertEntityAsync(scope.Repository.Entity("occurrence:" + root.Id, root));
            foreach (string key in new[] { "occurrence:log-job", "attempt:old", "attempt:new" })
            {
                await scope.Repository.Table.DeleteEntityAsync(scope.Repository.Partition, key);
            }

            attempts = await app.Client.GetFromJsonAsync<AttemptPage>("/api/jobrunner/ui/attempts" + query);
            Assert.True(attempts!.Archived);
            Assert.Equal(2, attempts.Items.Count);
            Assert.Equal(16, (await app.Client.GetFromJsonAsync<LogPage>(logs))!.Chunks.Count);
            await scope.Repository.Container.GetBlobClient("logs/log-job/old/stdout/00000040.txt").UploadAsync(BinaryData.FromString(new string('x', 32769)));
            Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync(logs + "&cursor=after:39")).StatusCode);
        }
        finally
        {
            await input.DeleteAsync();
        }
    }
}
