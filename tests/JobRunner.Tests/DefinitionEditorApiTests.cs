using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>実HTTPで原文ダウンロードと運用データへの非干渉を確認します。</summary>
public sealed class DefinitionEditorApiTests
{
    /// <summary>ローカル原文の検証・編集・試算・ZIP再入力はStorageへ保存しません。</summary>
    /// <returns>実HTTPの往復と永続状態の比較です。</returns>
    [Fact]
    public async Task LocalFilesRoundTripWithoutSavingDefinitions()
    {
        await using var scope = new StorageScope();
        scope.Options.LocalAgents = ["local"];
        string containerName = "local-" + Guid.NewGuid().ToString("N");
        scope.Options.DefinitionSources = [new() { BlobPath = containerName + "/active.ajs" }];
        await scope.Repository.InitializeAsync(CancellationToken.None);
        var container = scope.Repository.Blobs.GetBlobContainerClient(containerName);
        await container.CreateAsync();
        await container.GetBlobClient("active.ajs").UploadAsync(BinaryData.FromString("unit=active;{ty=n;sd=0,ud;}"));
        await TestDefinitions.SeedHttpAsync(scope.Repository, scope.Options);
        using var app = new AppProcess(scope.Options);
        try
        {
            await WaitForEditorAsync(app);
            var before = await OperationalStateAsync(scope.Repository);
            byte[] original = Encoding.UTF8.GetBytes("\uFEFF/* local */\r\nunit=local;{ty=n;sd=0,ud;unit=job;{ty=j;te=\"true\";}}\r\n");
            var input = new EditorLocalRequest([new() { Mapping = new() { BlobPath = "local.ajs" }, Base64 = Convert.ToBase64String(original) }]);
            var opened = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/local", input);
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
            var document = (await opened.Content.ReadFromJsonAsync<EditorDocument>())!;
            Assert.NotNull(document.Draft.LocalSources);
            var download = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/export", document.Draft);
            Assert.Equal(original, await download.Content.ReadAsByteArrayAsync());
            var forged = document.Draft with { DefinitionHash = "wrong" };
            Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/preview", forged)).StatusCode);
            document.Draft.Networks[0].Nodes[0].Attributes["te"] = ["echo local"];
            var jobExecution = document.Draft.Execution!.Single(e => e.Id == "/local/job").Attributes;
            jobExecution["ha"] = ["n"];
            jobExecution["jd"] = ["cod"];
            jobExecution["wth"] = ["1"];
            jobExecution["tho"] = ["5"];
            jobExecution["etm"] = ["1440"];
            jobExecution["ex"] = ["local"];
            var netExecution = document.Draft.Execution!.Single(e => e.Id == "/local").Attributes;
            netExecution["ha"] = ["n"];
            netExecution["mp"] = ["y"];
            netExecution["sy"] = ["1,09:00", "144,C30"];
            netExecution["ey"] = ["1,C60"];
            netExecution["fd"] = ["2879"];
            var preview = await (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/preview", document.Draft)).Content.ReadFromJsonAsync<EditorPreview>();
            Assert.True(preview!.Valid, string.Join("\n", preview.Errors));
            Assert.Contains("echo local", preview.Sources[0].Text, StringComparison.Ordinal);
            download = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/export", document.Draft);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            var editedSource = new SourceEvidence { Mapping = input.Sources![0].Mapping, Base64 = Convert.ToBase64String(await download.Content.ReadAsByteArrayAsync()) };
            var reimport = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/local", new EditorLocalRequest([editedSource]));
            Assert.Equal(HttpStatusCode.OK, reimport.StatusCode);
            var reimported = (await reimport.Content.ReadFromJsonAsync<EditorDocument>())!;
            Assert.Equal("1440", reimported.Draft.Execution!.Single(e => e.Id == "/local/job").Attributes["etm"][0]);
            Assert.Equal(["1,09:00", "144,C30"], reimported.Draft.Execution!.Single(e => e.Id == "/local").Attributes["sy"]);
            jobExecution["ex"] = ["unconfigured-remote"];
            Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/export", document.Draft)).StatusCode);
            jobExecution["ex"] = ["local"];
            var trial = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/schedules", new EditorScheduleRequest(document.Draft, "/local", new(2026, 1, 1), new(2026, 1, 1), new(2026, 1, 2)));
            Assert.Empty((await trial.Content.ReadFromJsonAsync<EditorSchedulePreview>())!.Points);
            input.Sources!.Add(new() { Mapping = new() { BlobPath = "other/cal.ajs", TargetPath = "/business" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=cal;{ty=g;cl=su;}")) });
            opened = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/local", input);
            document = (await opened.Content.ReadFromJsonAsync<EditorDocument>())!;
            download = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/export", document.Draft);
            string zip = Convert.ToBase64String(await download.Content.ReadAsByteArrayAsync());
            var restored = await (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/local", new EditorLocalRequest(ZipBase64: zip))).Content.ReadFromJsonAsync<EditorDocument>();
            Assert.Equal(document.Draft.DefinitionHash, restored!.Draft.DefinitionHash);
            Assert.Equal(document.Draft.LocalSources!.Select(s => s.Base64), restored.Draft.LocalSources!.Select(s => s.Base64));
            Assert.Equal("/business", restored.Sources.Single(s => s.FileName == "other/cal.ajs").TargetPath);
            opened = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/local/new", new EditorFileCreation("fresh.ajs", "/", "utf-8", true, "fresh"));
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
            var fresh = (await opened.Content.ReadFromJsonAsync<EditorDocument>())!;
            Assert.Empty(fresh.Draft.Networks);
            fresh.Draft.NewFiles!.Add(new("next.ajs", "/newarea/nested", "utf-16", true, "next"));
            fresh.Draft.NewGroups!.Add(new(1, "/newarea/nested/next", "child"));
            foreach (string path in new[] { "/newarea/nested/next", "/newarea/nested/next/child" })
            {
                fresh.Draft.Calendars!.Add(new(path, []));
                fresh.Draft.Execution!.Add(new(path, []));
            }

            preview = await (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/preview", fresh.Draft)).Content.ReadFromJsonAsync<EditorPreview>();
            Assert.True(preview!.Valid, string.Join("\n", preview.Errors));
            download = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/export", fresh.Draft);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            restored = await (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/local", new EditorLocalRequest(ZipBase64: Convert.ToBase64String(await download.Content.ReadAsByteArrayAsync())))).Content.ReadFromJsonAsync<EditorDocument>();
            Assert.Contains(restored!.Draft.Calendars!, c => c.Id == "/newarea/nested/next/child");
            Assert.Equal(fresh.Draft.LocalSources![0].Base64, restored.Draft.LocalSources!.Single(s => s.Mapping.BlobPath == "fresh.ajs").Base64);
            var standalone = new SourceEvidence { Mapping = new() { BlobPath = "standalone.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=solo;{ty=j;te=\"true\";}")) };
            var single = (await (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/local", new EditorLocalRequest([standalone]))).Content.ReadFromJsonAsync<EditorDocument>())!;
            download = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/export", single.Draft);
            Assert.Equal(Convert.FromBase64String(standalone.Base64), await download.Content.ReadAsByteArrayAsync());
            var groupedSource = new SourceEvidence { Mapping = new() { BlobPath = "groups.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=g;{ty=g;cl=su;unit=net;{ty=n;sd=0,ud;}}unit=keep;{ty=g;}unit=ref;{ty=n;sd=0,ud;jc=\"/g\";}")) };
            var grouped = (await (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/local", new EditorLocalRequest([groupedSource]))).Content.ReadFromJsonAsync<EditorDocument>())!;
            grouped.Draft.GroupChanges!.Add(new("/g", "renamed"));
            trial = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/schedules", new EditorScheduleRequest(grouped.Draft, "/g/net", new(2026, 1, 1), new(2026, 1, 1), new(2026, 1, 2)));
            Assert.Equal(HttpStatusCode.OK, trial.StatusCode);
            download = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/export", grouped.Draft);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            var groupOutput = new SourceEvidence { Mapping = groupedSource.Mapping, Base64 = Convert.ToBase64String(await download.Content.ReadAsByteArrayAsync()) };
            var groupImport = (await (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/local", new EditorLocalRequest([groupOutput]))).Content.ReadFromJsonAsync<EditorDocument>())!;
            Assert.Equal("/renamed", groupImport.Draft.Schedules!.Single(s => s.Id == "/ref").Attributes["jc"][0]);
            grouped.Draft.GroupChanges.Clear();
            grouped.Draft.GroupChanges.Add(new("/g", Delete: true));
            Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/export", grouped.Draft)).StatusCode);
            grouped.Draft.Schedules!.Single(s => s.Id == "/ref").Attributes["jc"] = ["/keep"];
            download = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/export", grouped.Draft);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.DoesNotContain("unit=g", await download.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var movableSource = new SourceEvidence { Mapping = new() { BlobPath = "movable.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=a;{ty=g;unit=net;{ty=n;sd=0,ud;ar=(f=job,t=child);unit=job;{ty=j;te=\"true\";}unit=child;{ty=n;}}}unit=b;{ty=g;}")) };
            var movable = (await (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/local", new EditorLocalRequest([movableSource]))).Content.ReadFromJsonAsync<EditorDocument>())!;
            movable.Draft.NetworkChanges!.AddRange([new("/a/net", "/b"), new("/a/net/child", Type: "rn")]);
            preview = await (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/preview", movable.Draft)).Content.ReadFromJsonAsync<EditorPreview>();
            Assert.True(preview!.Valid, string.Join("\n", preview.Errors));
            trial = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/schedules", new EditorScheduleRequest(movable.Draft, "/a/net", new(2026, 1, 1), new(2026, 1, 1), new(2026, 1, 2)));
            Assert.Equal(HttpStatusCode.OK, trial.StatusCode);
            download = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/export", movable.Draft);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            var moved = new SourceEvidence { Mapping = movableSource.Mapping, Base64 = Convert.ToBase64String(await download.Content.ReadAsByteArrayAsync()) };
            var movedImport = (await (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/local", new EditorLocalRequest([moved]))).Content.ReadFromJsonAsync<EditorDocument>())!;
            Assert.Equal("/b/net", Assert.Single(movedImport.Draft.Roots!).Node.Id);
            Assert.Equal("rn", movedImport.Draft.Networks.Single(n => n.Path == "/b/net").Nodes.Single(n => n.Name == "child").Type);
            movable.Draft.Calendars!.Single(c => c.Id == "/b").Attributes["cl"] = ["su"];
            Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/export", movable.Draft)).StatusCode);
            using var oversized = new StringContent(new string(' ', (12 * 1024 * 1024) + 1), Encoding.UTF8, "application/json");
            using var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/jobrunner/ui/definition-editor/local") { Content = oversized };
            oversizedRequest.Headers.ExpectContinue = true;
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await app.Client.SendAsync(oversizedRequest)).StatusCode);
            Assert.Equal(before, await OperationalStateAsync(scope.Repository));
        }
        finally
        {
            await container.DeleteAsync();
        }
    }

    /// <summary>元入力を更新しても保存版から編集し、ZIPまたは元バイト列を出力します。</summary>
    /// <param name="multiple">複数ファイル出力かどうかです。</param>
    /// <returns>実HTTPとStorageの検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportsSavedSourcesWithoutOperationalWrites(bool multiple)
    {
        await using var scope = new StorageScope();
        string containerName = "editor-" + Guid.NewGuid().ToString("N");
        scope.Options.DefinitionSources = [new DefinitionSource { BlobPath = containerName + "/jobs.ajs" }];
        if (multiple)
        {
            scope.Options.DefinitionSources.Add(new DefinitionSource { BlobPath = containerName + "/calendar.ajs", TargetPath = "/business" });
        }

        await scope.Repository.InitializeAsync(CancellationToken.None);
        var container = scope.Repository.Blobs.GetBlobContainerClient(containerName);
        await container.CreateAsync();
        byte[] original = Encoding.UTF8.GetBytes("\uFEFF/* 保存された原文 */\r\nunit=net;{ty=n;sd=0,ud;el=a,j,+80 +48;el=judge,jdj,+240 +48;ar=(f=a,t=judge);unit=a;{ty=j;te=\"true\";}unit=judge;{ty=jdj;ej=eq;ejc=7;ha=n;}}\r\n");
        await container.GetBlobClient("jobs.ajs").UploadAsync(new BinaryData(original));
        if (multiple)
        {
            await container.GetBlobClient("calendar.ajs").UploadAsync(BinaryData.FromString("unit=calendar;{ty=g;op=mo;}"));
        }

        await TestDefinitions.SeedHttpAsync(scope.Repository, scope.Options);
        using var app = new AppProcess(scope.Options);
        try
        {
            var document = await WaitForEditorAsync(app);
            var originalBundle = await new DefinitionService(scope.Repository, scope.Options).ReadSourcesAsync(document.Draft.DefinitionHash, CancellationToken.None);
            var before = await OperationalStateAsync(scope.Repository);
            var exact = await app.Client.GetFromJsonAsync<EditorDocument>("/api/jobrunner/ui/definition-editor?definitionHash=" + document.Draft.DefinitionHash);
            Assert.Equal(9, exact!.Draft.EditorVersion);
            Assert.Equal("7", exact.Draft.Networks[0].Nodes.Single(n => n.Type == "jdj").Attributes["ejc"][0]);
            var legacy = DefinitionEditor.Project(originalBundle, 1) with { EditorVersion = null };
            var legacyPreview = await (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/preview", new { legacy.DefinitionHash, legacy.Networks })).Content.ReadFromJsonAsync<EditorPreview>();
            Assert.True(legacyPreview!.Valid);
            Assert.Contains(legacyPreview.Sources, s => s.Text.Contains("ejc=7;", StringComparison.Ordinal));
            await container.GetBlobClient("jobs.ajs").UploadAsync(BinaryData.FromString("broken live input"), overwrite: true);
            var previewResponse = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/preview", document.Draft);
            var preview = await previewResponse.Content.ReadFromJsonAsync<EditorPreview>();
            Assert.True(preview!.Valid);
            Assert.All(preview.Sources, source => Assert.False(source.Changed));
            var response = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/export", document.Draft);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            byte[] downloaded = await response.Content.ReadAsByteArrayAsync();
            if (multiple)
            {
                using var stream = new MemoryStream(downloaded);
                using var archive = new ZipArchive(stream);
                var entry = Assert.Single(archive.Entries, e => e.Name == "jobs.ajs");
                using var content = entry.Open();
                using var bytes = new MemoryStream();
                await content.CopyToAsync(bytes);
                Assert.Equal(original, bytes.ToArray());
                using var manifest = JsonDocument.Parse(await new StreamReader(archive.GetEntry("manifest.json")!.Open()).ReadToEndAsync());
                var sources = manifest.RootElement.GetProperty("Sources").EnumerateArray().Select(source =>
                {
                    using var data = archive.GetEntry(source.GetProperty("File").GetString()!)!.Open();
                    using var file = new MemoryStream();
                    data.CopyTo(file);
                    return new SourceEvidence { Mapping = source.GetProperty("Mapping").Deserialize<DefinitionSource>()!, Base64 = Convert.ToBase64String(file.ToArray()) };
                }).ToList();
                Assert.Equal(document.Draft.DefinitionHash, new DefinitionService(scope.Repository, scope.Options).Validate(sources).Hash);
            }
            else
            {
                Assert.Equal(original, downloaded);
                Assert.Contains("jobs.ajs", response.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
            }

            var net = document.Draft.Networks[0];
            net.Nodes[0].Attributes["te"] = ["echo changed"];
            var judgment = net.Nodes.Single(n => n.Type == "jdj");
            judgment.Attributes["ejc"] = ["4294967295"];
            preview = await (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/preview", document.Draft)).Content.ReadFromJsonAsync<EditorPreview>();
            Assert.True(preview!.Valid);
            Assert.Contains(preview.Sources, s => s.Text.Contains("ejc=4294967295;ha=n;", StringComparison.Ordinal));
            response = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/export", document.Draft);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            document.Draft.Roots![0] = document.Draft.Roots[0] with { Node = document.Draft.Roots[0].Node with { Name = "renamed" } };
            document.Draft.Roots.Add(new(new("new:root", "newroot", "n", new(), null, null), 0, "/"));
            document.Draft.Networks.Add(new("new:root", [], []));
            net.Nodes.Add(new("new:nested", "nested", "rn", new(), 400, 48));
            document.Draft.Networks.Add(new("new:nested", [], []));
            document.Draft.Schedules!.Add(new("new:root", new() { ["sd"] = ["0,ud"] }));
            document.Draft.Schedules.Add(new("new:nested", new()));
            document.Draft.Execution!.Add(new("new:root", new() { ["ha"] = ["y"] }));
            document.Draft.Execution.Add(new("new:nested", new()));
            preview = await (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/preview", document.Draft)).Content.ReadFromJsonAsync<EditorPreview>();
            Assert.True(preview!.Valid);
            Assert.Contains(preview.Sources, s => s.Text.Contains("sd=0,ud;ha=y;", StringComparison.Ordinal));
            response = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/export", document.Draft);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            document.Draft.Schedules[0].Attributes["sd"] = ["1,2026/11/6", "2,2026/11/7"];
            document.Draft.Schedules[0].Attributes["st"] = ["1,09:00", "2,10:00"];
            document.Draft.Schedules[0].Attributes["sh"] = ["1,no", "2,no"];
            if (multiple)
            {
                document.Draft.Calendars![0].Attributes["cl"] = ["su", "2026/12/31"];
            }

            var trialRequest = new EditorScheduleRequest(document.Draft, net.Path, new(2026, 11, 6), new(2026, 11, 6), new(2026, 11, 8));
            var trialResponse = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/schedules", trialRequest);
            Assert.Equal(HttpStatusCode.OK, trialResponse.StatusCode);
            var trial = await trialResponse.Content.ReadFromJsonAsync<EditorSchedulePreview>();
            Assert.Equal(2, trial!.Points.Count);
            Assert.Equal(scope.Options.TimeZone, trial.TimeZone);
            Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/schedules", trialRequest with { NetworkId = "new:nested" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/export", document.Draft)).StatusCode);
            net.Edges.Add(new EditorEdge(net.Nodes[0].Id, net.Nodes[0].Id, false));
            preview = await (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/preview", document.Draft)).Content.ReadFromJsonAsync<EditorPreview>();
            Assert.False(preview!.Valid);
            Assert.NotEmpty(preview.Errors);
            response = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/export", document.Draft);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(before, await OperationalStateAsync(scope.Repository));

            var saved = await new DefinitionService(scope.Repository, scope.Options).ReadSourcesAsync(document.Draft.DefinitionHash, CancellationToken.None);
            await scope.Repository.Container.GetBlobClient($"definitions/{saved.Hash}/sources/{saved.Sources[0].ContentHash}").DeleteAsync();
            response = await app.Client.GetAsync("/api/jobrunner/ui/definition-editor");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("欠落", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await container.DeleteAsync();
        }
    }

    /// <summary>選択移動APIは候補とエラーだけを返し、Blob・採用・運用行を書き換えません。</summary>
    /// <returns>正常・不正要求と出力の実HTTP検証です。</returns>
    [Fact]
    public async Task MoveSelectionIsDraftOnlyOverHttp()
    {
        await using var scope = new StorageScope();
        string input = "child-move-" + Guid.NewGuid().ToString("N");
        scope.Options.DefinitionSources = [new() { BlobPath = input + "/active.ajs" }];
        await scope.Repository.InitializeAsync(CancellationToken.None);
        var container = scope.Repository.Blobs.GetBlobContainerClient(input);
        await container.CreateAsync();
        await container.GetBlobClient("active.ajs").UploadAsync(BinaryData.FromString("unit=active;{ty=n;sd=0,ud;}"));
        await TestDefinitions.SeedHttpAsync(scope.Repository, scope.Options);
        using var app = new AppProcess(scope.Options);
        try
        {
            await WaitForEditorAsync(app);
            var before = await OperationalStateAsync(scope.Repository);
            var opened = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/local", new EditorLocalRequest([new() { Mapping = new() { BlobPath = "local.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=a;{ty=n;sd=0,ud;unit=j;{ty=j;te=\"true\";}}unit=b;{ty=n;sd=0,ud;}")) }]));
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
            var document = (await opened.Content.ReadFromJsonAsync<EditorDocument>())!;
            var request = new EditorMoveSelectionRequest(document.Draft, ["/a/j"], "/b");
            var response = await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/move-selection", request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var moved = (await response.Content.ReadFromJsonAsync<EditorDocument>())!;
            Assert.Contains(moved.Draft.Networks.Single(n => n.Path == "/b").Nodes, n => n.Id == "/b/j");
            Assert.NotNull(moved.Draft.OriginCatalogHash);
            Assert.Equal(HttpStatusCode.OK, (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/export", moved.Draft)).StatusCode);
            foreach (var invalid in new[] { request with { Ids = [] }, request with { Ids = ["/a/j", "/a/j"] }, request with { TargetNetworkId = "/a" }, request with { TargetNetworkId = "/a/j" } })
            {
                Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/move-selection", invalid)).StatusCode);
            }

            Assert.Equal(before, await OperationalStateAsync(scope.Repository));
        }
        finally
        {
            await container.DeleteAsync();
        }
    }

    private static async Task<List<string>> OperationalStateAsync(StorageRepository storage)
    {
        var state = new List<string>();
        foreach (string prefix in new[] { "definition-active", "registration:", "occurrence:", "attempt:", "command:" })
        {
            state.AddRange((await storage.ListAsync(prefix, CancellationToken.None)).Select(row => row.RowKey + row.GetString("Json")));
        }

        await foreach (var blob in storage.Container.GetBlobsAsync(prefix: "definitions/"))
        {
            state.Add(blob.Name + blob.Properties.ETag);
        }

        return state.Order(StringComparer.Ordinal).ToList();
    }

    private static async Task<EditorDocument> WaitForEditorAsync(AppProcess app)
    {
        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var response = await app.Client.GetAsync("/api/jobrunner/ui/definition-editor");
                if (response.IsSuccessStatusCode)
                {
                    return (await response.Content.ReadFromJsonAsync<EditorDocument>())!;
                }
            }
            catch (HttpRequestException)
            {
                // 実HTTPリスナーの起動を待ちます。
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("定義編集APIが起動しませんでした。");
    }
}
