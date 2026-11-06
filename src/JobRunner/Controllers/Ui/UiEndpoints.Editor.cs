using System.IO.Compression;
using System.Text.Json;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;

namespace JobRunner.Controllers.Ui;

/// <summary>下書きの検証とダウンロードです。運用データの保存APIは呼びません。</summary>
public static partial class UiEndpoints
{
    private static void MapEditor(RouteGroupBuilder group)
    {
        group.MapGet("/definition-editor", async (StorageRepository storage, DefinitionService definitions, CancellationToken token) =>
        {
            var active = await ActiveAsync(storage, token);
            var bundle = await definitions.ReadSourcesAsync(active.DefinitionHash, token);
            return new EditorDocument(DefinitionEditor.Project(bundle), DefinitionEditor.Describe(bundle.Sources, bundle.Sources));
        });
        group.MapPost("/definition-editor/preview", async (EditorDraft draft, DefinitionService definitions, CancellationToken token) =>
        {
            var bundle = await definitions.ReadSourcesAsync(draft.DefinitionHash, token);
            return CompileDraft(bundle, draft, definitions).Preview;
        });
        group.MapPost("/definition-editor/export", async (EditorDraft draft, DefinitionService definitions, CancellationToken token) =>
        {
            var bundle = await definitions.ReadSourcesAsync(draft.DefinitionHash, token);
            var compiled = CompileDraft(bundle, draft, definitions);
            if (!compiled.Preview.Valid)
            {
                return Results.Problem(string.Join("\n", compiled.Preview.Errors), statusCode: 400);
            }

            if (compiled.Sources.Count == 1)
            {
                var source = compiled.Sources[0];
                return Results.File(Convert.FromBase64String(source.Base64), "application/octet-stream", source.Mapping.BlobPath.Split('/').Last());
            }

            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                var manifest = new List<object>();
                for (int i = 0; i < compiled.Sources.Count; i++)
                {
                    var source = compiled.Sources[i];

                    // 同名・相対パスをZIP名へ持ち込まず、manifestから元の配置を復元します。
                    string name = $"sources/{i + 1:D3}/{Path.GetFileName(source.Mapping.BlobPath.Replace('\\', '/'))}";
                    using var entry = archive.CreateEntry(name).Open();
                    await entry.WriteAsync(Convert.FromBase64String(source.Base64), token);
                    manifest.Add(new { File = name, source.Mapping });
                }

                using var metadata = archive.CreateEntry("manifest.json").Open();
                await JsonSerializer.SerializeAsync(metadata, new { FormatVersion = 1, BaseDefinitionHash = draft.DefinitionHash, Sources = manifest }, cancellationToken: token);
            }

            return Results.File(stream.ToArray(), "application/zip", "jobrunner-definitions.zip");
        }).Produces(200, contentType: "application/octet-stream");
    }

    private static (EditorPreview Preview, List<SourceEvidence> Sources) CompileDraft(DefinitionBundle bundle, EditorDraft draft, DefinitionService definitions)
    {
        List<SourceEvidence> sources = [];
        try
        {
            sources = DefinitionEditor.Render(bundle, draft);
            var validated = definitions.Validate(sources);
            var layoutErrors = new List<string>();
            foreach (var network in validated.Units.Flatten().Where(u => u.IsNetwork()))
            {
                try
                {
                    DefinitionLayout.Read(network);
                }
                catch (InvalidDataException error)
                {
                    layoutErrors.Add(error.Message);
                }
            }

            return (new EditorPreview(true, [], layoutErrors, DefinitionEditor.Describe(sources, bundle.Sources)), sources);
        }
        catch (Exception error) when (error is FormatException or ArgumentException or InvalidDataException)
        {
            return (new EditorPreview(false, [error.Message], [], sources.Count == 0 ? [] : DefinitionEditor.Describe(sources, bundle.Sources)), sources);
        }
    }
}
