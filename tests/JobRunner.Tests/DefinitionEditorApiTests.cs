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

        using var app = new AppProcess(scope.Options);
        try
        {
            var document = await WaitForEditorAsync(app);
            var originalBundle = await new DefinitionService(scope.Repository, scope.Options).ReadSourcesAsync(document.Draft.DefinitionHash, CancellationToken.None);
            var before = await OperationalStateAsync(scope.Repository);
            var exact = await app.Client.GetFromJsonAsync<EditorDocument>("/api/jobrunner/ui/definition-editor?definitionHash=" + document.Draft.DefinitionHash);
            Assert.Equal(2, exact!.Draft.EditorVersion);
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
