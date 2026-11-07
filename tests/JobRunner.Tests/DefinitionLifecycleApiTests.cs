using System.Net;
using System.Net.Http.Json;
using System.Text;
using JobRunner.Controllers.Ui;
using JobRunner.Models;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>定義未登録から共有保存・明示反映・停止再開までのHTTP契約です。</summary>
public sealed class DefinitionLifecycleApiTests
{
    /// <summary>空状態で下書きを保存でき、同時保存と確認後の反映競合を拒否します。</summary>
    /// <returns>実HTTPとBlob/Tableの保存結果です。</returns>
    [Fact]
    public async Task EmptyInstallationSupportsSharedDraftAndConfirmedPublication()
    {
        await using var scope = new StorageScope();
        using var app = new AppProcess(scope.Options);
        var client = app.Client;
        await WaitAsync(async () =>
        {
            try
            {
                return (await client.GetAsync("/api/jobrunner/ui/runtime")).IsSuccessStatusCode;
            }
            catch (HttpRequestException)
            {
                return false;
            }
        });
        var initial = await client.GetFromJsonAsync<RuntimeStatus>("/api/jobrunner/ui/runtime");
        Assert.Null(initial!.Active);
        Assert.True(initial.Control.Paused);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/jobrunner/ui/definition-editor/shared")).StatusCode);
        var source = new SourceEvidence { Mapping = new DefinitionSource { BlobPath = "local.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=net;{ty=n;sd=0,ud;el=job,j,+80 +48;unit=job;{ty=j;te=\"true\";}}")) };
        var document = (await (await client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/local", new EditorLocalRequest([source]))).Content.ReadFromJsonAsync<EditorDocument>())!;
        var invalid = document.Draft with { Schedules = [new("/net", new() { ["sd"] = ["bad-value"] })] };
        var invalidSave = new SharedEditorDraft(0, 0, document, invalid);
        var candidates = await Task.WhenAll(client.PutAsJsonAsync("/api/jobrunner/ui/definition-editor/shared", invalidSave), client.PutAsJsonAsync("/api/jobrunner/ui/definition-editor/shared", new SharedEditorDraft(0, 0, document, document.Draft)));
        Assert.Single(candidates, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(candidates, r => r.StatusCode == HttpStatusCode.Conflict);
        var saved = (await client.GetFromJsonAsync<SharedEditorDraft>("/api/jobrunner/ui/definition-editor/shared"))!;
        Assert.Equal(1, saved.Revision);
        var savedInvalid = await client.PutAsJsonAsync("/api/jobrunner/ui/definition-editor/shared", invalidSave with { Revision = 1 });
        Assert.Equal(HttpStatusCode.OK, savedInvalid.StatusCode);
        var repeatedSave = await client.PutAsJsonAsync("/api/jobrunner/ui/definition-editor/shared", invalidSave with { Revision = 1 });
        Assert.Equal(HttpStatusCode.OK, repeatedSave.StatusCode);
        Assert.Equal(2, (await repeatedSave.Content.ReadFromJsonAsync<SharedEditorDraft>())!.Revision);
        Assert.Equal(2, (await client.GetFromJsonAsync<SharedEditorDraft>("/api/jobrunner/ui/definition-editor/shared"))!.Revision);
        var check = await (await client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/preview", invalid)).Content.ReadFromJsonAsync<EditorPreview>();
        Assert.False(check!.Valid);
        Assert.Null(await scope.Repository.GetAsync("definition-active", CancellationToken.None));
        var preview = (await (await client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/publication-preview", document.Draft)).Content.ReadFromJsonAsync<PublicationPreview>())!;
        Assert.Equal(["/net"], preview.AddedRoots);
        var operation = new Operation { Id = "first-publication", Target = preview.DefinitionHash, ExpectedDefinitionRevision = preview.ExpectedRevision, ExpectedImpactHash = preview.ImpactHash };
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/publish", operation)).StatusCode);
        await WaitAsync(async () => (await client.GetFromJsonAsync<Operation>("/api/jobrunner/operations/first-publication"))!.Status != "Pending");
        Assert.Equal("Completed", (await client.GetFromJsonAsync<Operation>("/api/jobrunner/operations/first-publication"))!.Status);
        var running = (await client.GetFromJsonAsync<RuntimeStatus>("/api/jobrunner/ui/runtime"))!;
        Assert.Equal(1, running.Active!.Revision);
        Assert.True(running.Control.Paused);
        Assert.Empty(await scope.Repository.ListAsync("attempt:", CancellationToken.None));
        operation.Id = "stale-preview";
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/publish", operation)).StatusCode);
        await WaitAsync(async () => (await client.GetFromJsonAsync<Operation>("/api/jobrunner/operations/stale-preview"))!.Status != "Pending");
        Assert.Equal("Rejected", (await client.GetFromJsonAsync<Operation>("/api/jobrunner/operations/stale-preview"))!.Status);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/jobrunner/ui/definition-editor/publish", WithId("first-publication"))).StatusCode);

        Operation WithId(string id) => new() { Id = id, Target = preview.DefinitionHash, ExpectedDefinitionRevision = preview.ExpectedRevision, ExpectedImpactHash = preview.ImpactHash };
    }

    private static async Task WaitAsync(Func<Task<bool>> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await ready())
        {
            await Task.Delay(100, deadline.Token);
        }
    }
}
