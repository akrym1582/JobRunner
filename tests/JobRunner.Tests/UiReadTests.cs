using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobRunner.Controllers.Ui;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>監視APIの固定定義と退避済み結果の契約を実HTTPで確認します。</summary>
public sealed class UiReadTests
{
    /// <summary>現在の定義を更新しても過去世代の図とUnknownを変更しません。</summary>
    /// <returns>HTTPと保存済みデータの検証です。</returns>
    [Fact]
    public async Task GraphUsesFixedVersionAndArchivedResults()
    {
        await using var scope = new StorageScope();
        string inputName = "ui-" + Guid.NewGuid().ToString("N");
        scope.Options.DefinitionSources = [new DefinitionSource { BlobPath = inputName + "/jobs" }];
        await scope.Repository.InitializeAsync(CancellationToken.None);
        var container = scope.Repository.Blobs.GetBlobContainerClient(inputName);
        await container.CreateAsync();
        await container.GetBlobClient("jobs").UploadAsync(BinaryData.FromString("unit=net;{ty=n;sd=0,ud;el=current,j,+80 +48;unit=current;{ty=j;te=\"true\";}}"));
        await TestDefinitions.SeedHttpAsync(scope.Repository, scope.Options);
        using var app = new AppProcess(scope.Options);
        try
        {
            var catalog = await WaitForCatalogAsync(app);
            var definitions = new DefinitionService(scope.Repository, scope.Options);
            var old = definitions.Validate([new SourceEvidence { Mapping = scope.Options.DefinitionSources[0], Base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("unit=net;{ty=n;sd=0,ud;el=old,j,+400 +144;unit=old;{ty=j;te=\"true\";}}")) }]);
            await definitions.SaveAsync(old, CancellationToken.None);
            var root = new Occurrence { Id = "ui-root", RootId = "ui-root", Namespace = catalog.Namespace, RootPath = "/net", UnitPath = "/net", DefinitionHash = old.Hash, State = "Unknown", Started = true, Due = DateTimeOffset.Parse("2030-06-30T15:00:00Z"), BusinessDate = new DateOnly(2030, 6, 30) };
            var child = new Occurrence { Id = "ui-child", RootId = root.Id, Namespace = catalog.Namespace, RootPath = "/net", UnitPath = "/net/old", DefinitionHash = old.Hash, State = "Unknown", Started = true, Due = DateTimeOffset.Parse("2030-06-30T15:00:00Z"), BusinessDate = new DateOnly(2030, 6, 30) };
            await scope.Repository.Table.AddEntityAsync(scope.Repository.Entity("occurrence:" + root.Id, root));
            await scope.Repository.Table.AddEntityAsync(scope.Repository.Entity("occurrence:" + child.Id, child));
            var graph = await app.Client.GetFromJsonAsync<Graph>("/api/jobrunner/ui/graph?unitPath=%2Fnet&rootId=ui-root");
            Assert.Equal(old.Hash, graph!.DefinitionHash);
            Assert.Equal("UNKNOWN", Assert.Single(graph.Nodes).Status);
            Assert.False(graph.CanChange);
            Assert.Equal(400, Assert.Single(graph.Nodes).X);
            Assert.Equal(144, Assert.Single(graph.Nodes).Y);
            var archive = new HistoryArchive { RootId = root.Id, Rows = [new ArchivedRow("occurrence:" + root.Id, string.Empty, JsonSerializer.Serialize(root)), new ArchivedRow("occurrence:" + child.Id, string.Empty, JsonSerializer.Serialize(child))] };
            root.ArchivePath = "history/ui-test.json";
            await scope.Repository.Container.GetBlobClient(root.ArchivePath).UploadAsync(BinaryData.FromObjectAsJson(archive));
            root.State = "Archived";
            await scope.Repository.Table.UpsertEntityAsync(scope.Repository.Entity("occurrence:" + root.Id, root));
            await scope.Repository.Table.DeleteEntityAsync(scope.Repository.Partition, "occurrence:" + child.Id);
            graph = await app.Client.GetFromJsonAsync<Graph>("/api/jobrunner/ui/graph?unitPath=%2Fnet&rootId=ui-root");
            Assert.Equal("UNKNOWN", Assert.Single(graph!.Nodes).Status);
            Assert.Equal("Unknown", graph.Root!.State);
            Assert.False(graph.CanRestore);
            var june = await app.Client.GetFromJsonAsync<MonthlySchedule>("/api/jobrunner/ui/monthly?month=2030-06&rootPath=%2Fnet");
            Assert.Empty(june!.Entries);
            var july = await app.Client.GetFromJsonAsync<MonthlySchedule>("/api/jobrunner/ui/monthly?month=2030-07&rootPath=%2Fnet");
            Assert.Equal(2, july!.Entries.Count);
            Assert.All(july.Entries, e => Assert.Equal("2030-07-01", e.CalendarDate));
            Assert.All(july.Entries, e => Assert.Equal(new DateOnly(2030, 6, 30), e.Execution.BusinessDate));
            Assert.Contains(july.Entries, e => e.Status == "UNKNOWN" && e.Execution.UnitPath == "/net/old");
            Assert.Contains(july.Units, u => u.Path == "/net/old");
            Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync("/api/jobrunner/ui/monthly?month=2030-13&rootPath=%2Fnet")).StatusCode);
            var current = await app.Client.GetFromJsonAsync<Graph>("/api/jobrunner/ui/graph?unitPath=%2Fnet");
            Assert.Equal("current", Assert.Single(current!.Nodes).Name);
            Assert.Null(current.Root);
            Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/does-not-exist")).StatusCode);
        }
        finally
        {
            await container.DeleteAsync();
        }
    }

    /// <summary>運用版から削除したネットも旧図と実行回の両方を参照できます。</summary>
    /// <returns>旧定義の案内と読み取りモデルの確認です。</returns>
    [Fact]
    public async Task DeletedNetworkCanBeOpenedBeforeSelectingItsHistory()
    {
        await using var scope = new StorageScope();
        using var app = new AppProcess(scope.Options);
        await WaitForCatalogAsync(app);
        var definitions = new DefinitionService(scope.Repository, scope.Options);
        var current = definitions.Validate([new SourceEvidence { Mapping = new DefinitionSource { BlobPath = "current.ajs" }, Base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("unit=current;{ty=n;sd=0,ud;}")) }]);
        var previous = definitions.Validate([new SourceEvidence { Mapping = new DefinitionSource { BlobPath = "old.ajs" }, Base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("unit=removed;{ty=n;sd=0,ud;el=job,j,+80 +48;unit=job;{ty=j;te=\"true\";}}")) }]);
        await definitions.SaveAsync(current, CancellationToken.None);
        await definitions.SaveAsync(previous, CancellationToken.None);
        var operation = new Operation { Id = "first", Kind = "Activate", Target = current.Hash };
        await app.Client.PostAsJsonAsync("/api/jobrunner/operations", operation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while ((await app.Client.GetFromJsonAsync<Operation>("/api/jobrunner/operations/first", deadline.Token))!.Status == "Pending")
        {
            await Task.Delay(100, deadline.Token);
        }

        var catalog = (await app.Client.GetFromJsonAsync<Catalog>("/api/jobrunner/ui/jobnets"))!;
        var root = new Occurrence { Id = "retired", RootId = "retired", Namespace = catalog.Namespace, RootPath = "/removed", UnitPath = "/removed", DefinitionHash = previous.Hash, State = "Unknown", Started = true, Materialized = true, Due = DateTimeOffset.Parse("2030-01-02T00:00:00Z") };
        var child = new Occurrence { Id = "retired-child", RootId = root.Id, Namespace = catalog.Namespace, RootPath = root.RootPath, UnitPath = "/removed/job", DefinitionHash = previous.Hash, State = "Unknown", Started = true, Due = root.Due };
        await scope.Repository.Table.AddEntityAsync(scope.Repository.Entity("occurrence:" + root.Id, root));
        await scope.Repository.Table.AddEntityAsync(scope.Repository.Entity("occurrence:" + child.Id, child));
        catalog = (await app.Client.GetFromJsonAsync<Catalog>("/api/jobrunner/ui/jobnets"))!;
        Assert.Contains(catalog.Networks, n => n.Path == "/removed");
        var graph = (await app.Client.GetFromJsonAsync<Graph>("/api/jobrunner/ui/graph?unitPath=%2Fremoved"))!;
        Assert.Equal(previous.Hash, graph.DefinitionHash);
        Assert.Null(graph.Root);
        Assert.False(graph.CanChange);
        Assert.Equal("job", Assert.Single(graph.Nodes).Name);
        graph = (await app.Client.GetFromJsonAsync<Graph>("/api/jobrunner/ui/graph?unitPath=%2Fremoved&rootId=retired"))!;
        Assert.Equal("Unknown", graph.Root!.State);
        Assert.Equal(previous.Hash, graph.DefinitionHash);
        var monthly = (await app.Client.GetFromJsonAsync<MonthlySchedule>("/api/jobrunner/ui/monthly?month=2030-01&rootPath=%2Fremoved"))!;
        Assert.Equal(2, monthly.Entries.Count);
        Assert.Contains(monthly.Units, u => u.Path == "/removed/job");
    }

    /// <summary>同じパスの複数世代・試行を分離し、子だけが月をまたぐ場合も最新値を読みます。</summary>
    /// <returns>要求間で古い状態を再利用しない実HTTP検証です。</returns>
    [Fact]
    public async Task MonthlySeparatesRootsAndRefreshesChangedDates()
    {
        await using var scope = new StorageScope();
        string inputName = "ui-month-" + Guid.NewGuid().ToString("N");
        scope.Options.DefinitionSources = [new DefinitionSource { BlobPath = inputName + "/jobs" }];
        await scope.Repository.InitializeAsync(CancellationToken.None);
        var container = scope.Repository.Blobs.GetBlobContainerClient(inputName);
        await container.CreateAsync();
        await container.GetBlobClient("jobs").UploadAsync(BinaryData.FromString("unit=net;{ty=n;sd=0,ud;el=job,j,+80 +48;unit=job;{ty=j;te=\"true\";}}"));
        await TestDefinitions.SeedHttpAsync(scope.Repository, scope.Options);
        using var app = new AppProcess(scope.Options);
        try
        {
            var catalog = await WaitForCatalogAsync(app);
            foreach (string id in new[] { "a", "b", "foreign" })
            {
                var root = new Occurrence { Id = id, RootId = id, Namespace = id == "foreign" ? "old-namespace" : catalog.Namespace, RootPath = "/net", UnitPath = "/net", DefinitionHash = catalog.DefinitionHash, State = "Unknown", Started = true, Due = DateTimeOffset.Parse("2030-06-29T00:00:00Z") };
                var child = new Occurrence { Id = id + "-job", RootId = id, Namespace = root.Namespace, RootPath = "/net", UnitPath = "/net/job", DefinitionHash = catalog.DefinitionHash, State = "Unknown", Started = true, Due = DateTimeOffset.Parse("2030-06-30T15:00:00Z"), AttemptId = id + "-attempt", BusinessDate = new DateOnly(2030, 6, 30) };
                var attempt = new JobAttempt { Id = child.AttemptId, OccurrenceId = child.Id, State = "Unknown", ExitCode = id == "a" ? 5 : 9 };
                await scope.Repository.Table.AddEntityAsync(scope.Repository.Entity("occurrence:" + root.Id, root));
                await scope.Repository.Table.AddEntityAsync(scope.Repository.Entity("occurrence:" + child.Id, child));
                await scope.Repository.Table.AddEntityAsync(scope.Repository.Entity("attempt:" + attempt.Id, attempt));
            }

            var july = await app.Client.GetFromJsonAsync<MonthlySchedule>("/api/jobrunner/ui/monthly?month=2030-07&rootPath=%2Fnet");
            Assert.Equal(2, july!.Entries.Count);
            Assert.Equal(5, july.Entries.Single(e => e.Execution.RootId == "a").ExitCode);
            Assert.Equal(9, july.Entries.Single(e => e.Execution.RootId == "b").ExitCode);
            Assert.All(july.Entries, e => Assert.Equal("2030-07-01", e.CalendarDate));
            var row = await scope.Repository.GetAsync("occurrence:a-job", CancellationToken.None);
            var moved = StorageRepository.Read<Occurrence>(row!);
            moved.Due = DateTimeOffset.Parse("2030-07-01T15:00:00Z");
            moved.ManualExitCode = 42;
            await scope.Repository.Table.UpsertEntityAsync(scope.Repository.Entity("occurrence:" + moved.Id, moved));
            july = await app.Client.GetFromJsonAsync<MonthlySchedule>("/api/jobrunner/ui/monthly?month=2030-07&rootPath=%2Fnet");
            var changed = july!.Entries.Single(e => e.Execution.RootId == "a");
            Assert.Equal("2030-07-02", changed.CalendarDate);
            Assert.Equal(42, changed.ExitCode);
            Assert.Equal(new DateOnly(2030, 6, 30), changed.Execution.BusinessDate);
            Assert.Empty((await app.Client.GetFromJsonAsync<MonthlySchedule>("/api/jobrunner/ui/monthly?month=2030-08&rootPath=%2Fnet"))!.Entries);
        }
        finally
        {
            await container.DeleteAsync();
        }
    }

    private static async Task<Catalog> WaitForCatalogAsync(AppProcess app)
    {
        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var response = await app.Client.GetAsync("/api/jobrunner/ui/jobnets");
                if (response.IsSuccessStatusCode)
                {
                    return (await response.Content.ReadFromJsonAsync<Catalog>())!;
                }
            }
            catch (HttpRequestException)
            {
                // 実HTTPリスナーの起動完了を待ちます。
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("監視APIが起動しませんでした。");
    }
}
