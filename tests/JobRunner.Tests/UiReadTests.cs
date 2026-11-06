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
        using var app = new AppProcess(scope.Options);
        try
        {
            Catalog? catalog = null;
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    var response = await app.Client.GetAsync("/api/jobrunner/ui/jobnets");
                    if (response.IsSuccessStatusCode)
                    {
                        catalog = await response.Content.ReadFromJsonAsync<Catalog>();
                        break;
                    }
                }
                catch (HttpRequestException)
                {
                    // 実HTTPリスナーの起動完了を待ちます。
                }

                await Task.Delay(100);
            }

            Assert.NotNull(catalog);
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
}
