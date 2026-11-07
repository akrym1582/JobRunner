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
        group.MapPost("/definition-editor/move-selection", async (EditorMoveSelectionRequest request, DefinitionService definitions, RunnerOptions options, StorageRepository storage, CancellationToken token) =>
        {
            var bundle = await EditorSourcesAsync(request.Draft, definitions, token);
            request = request with { Draft = request.Draft with { OriginCatalogHash = request.Draft.OriginCatalogHash ?? await CurrentOriginAsync(storage, token) } };
            return DefinitionEditor.MoveSelection(bundle, request, definitions, options, token);
        });
        group.MapPost("/definition-editor/move", async (EditorMoveRequest request, DefinitionService definitions, RunnerOptions options, StorageRepository storage, CancellationToken token) =>
        {
            var bundle = await EditorSourcesAsync(request.Draft, definitions, token);
            var activeRow = await storage.GetAsync("definition-active", token);
            string catalog = activeRow is null ? string.Empty : StorageRepository.Read<Activation>(activeRow).GroupCatalogHash ?? string.Empty;
            request = request with { Draft = request.Draft with { OriginCatalogHash = request.Draft.OriginCatalogHash ?? catalog } };
            return DefinitionEditor.Move(bundle, request, definitions, options, token);
        });
        group.MapPost("/definition-editor/groups/export", async (EditorGroupPackageRequest request, DefinitionService definitions, CancellationToken token) =>
        {
            var bundle = await EditorSourcesAsync(request.Draft, definitions, token);
            return Results.File(DefinitionEditor.ExportGroup(bundle, request, definitions), "application/zip", "jobrunner-group.zip");
        }).Produces(200, contentType: "application/zip");
        group.MapPost("/definition-editor/groups/import", async (EditorGroupPackageRequest request, DefinitionService definitions, RunnerOptions options, CancellationToken token) =>
        {
            var bundle = await EditorSourcesAsync(request.Draft, definitions, token);
            return DefinitionEditor.ImportGroup(bundle, request, definitions, options);
        });
        group.MapPost("/definition-editor/local", async (EditorLocalRequest request, DefinitionService definitions, RunnerOptions options, StorageRepository storage, CancellationToken token) =>
        {
            var bundle = EditorLocalFiles.Read(request, definitions);
            return new EditorDocument(DefinitionEditor.Project(bundle, 9) with { LocalSources = bundle.Sources, OriginCatalogHash = await CurrentOriginAsync(storage, token) }, DefinitionEditor.Describe(bundle.Sources, bundle.Sources), DefinitionEditor.Containers(bundle), options.TimeZone, options.ScheduleTimeMode);
        });
        group.MapPost("/definition-editor/local/new", async (EditorFileCreation request, DefinitionService definitions, RunnerOptions options, StorageRepository storage, CancellationToken token) =>
        {
            var bundle = EditorLocalFiles.Read(new([DefinitionEditor.CreateFile(request)]), definitions);
            return new EditorDocument(DefinitionEditor.Project(bundle, 9) with { LocalSources = bundle.Sources, OriginCatalogHash = await CurrentOriginAsync(storage, token) }, DefinitionEditor.Describe(bundle.Sources, bundle.Sources), DefinitionEditor.Containers(bundle), options.TimeZone, options.ScheduleTimeMode);
        });
        group.MapGet("/definition-editor", async (string? definitionHash, StorageRepository storage, DefinitionService definitions, RunnerOptions options, CancellationToken token) =>
        {
            var activeRow = await storage.GetAsync("definition-active", token);
            var active = activeRow is null ? null : StorageRepository.Read<Activation>(activeRow);
            if (definitionHash is null)
            {
                active = await ActiveAsync(storage, token);
            }

            string hash = definitionHash ?? active!.DefinitionHash;
            var bundle = await definitions.ReadSourcesAsync(hash, token);
            return new EditorDocument(DefinitionEditor.Project(bundle, 9) with { OriginCatalogHash = active?.DefinitionHash == hash ? active.GroupCatalogHash : null }, DefinitionEditor.Describe(bundle.Sources, bundle.Sources), DefinitionEditor.Containers(bundle), options.TimeZone, options.ScheduleTimeMode);
        });
        group.MapPost("/definition-editor/preview", async (EditorDraft draft, DefinitionService definitions, CancellationToken token) =>
        {
            var bundle = await EditorSourcesAsync(draft, definitions, token);
            return CompileDraft(bundle, draft, definitions).Preview;
        });
        group.MapPost("/definition-editor/schedules", async (EditorScheduleRequest request, DefinitionService definitions, RunnerOptions options, CancellationToken token) =>
        {
            var bundle = await EditorSourcesAsync(request.Draft, definitions, token);
            var compiled = definitions.Validate(DefinitionEditor.Render(bundle, request.Draft));
            return DefinitionEditor.PreviewSchedule(compiled, request, options, token);
        });
        group.MapPost("/definition-editor/nested-schedules", async (EditorScheduleRequest request, DefinitionService definitions, RunnerOptions options, CancellationToken token) =>
        {
            var bundle = await EditorSourcesAsync(request.Draft, definitions, token);
            var compiled = definitions.Validate(DefinitionEditor.Render(bundle, request.Draft));
            return DefinitionEditor.PreviewNestedSchedule(compiled, request, options, token);
        });
        group.MapPost("/definition-editor/paste", async (EditorPasteRequest request, DefinitionService definitions, RunnerOptions options, CancellationToken token) =>
        {
            var bundle = await EditorSourcesAsync(request.Draft, definitions, token);
            return DefinitionEditor.Paste(bundle, request, definitions, options);
        });
        group.MapPost("/definition-editor/calendar", async (EditorCalendarRequest request, DefinitionService definitions, CancellationToken token) =>
        {
            var bundle = await EditorSourcesAsync(request.Draft, definitions, token);
            var compiled = definitions.Validate(DefinitionEditor.Render(bundle, request.Draft));
            return DefinitionEditor.CalendarMonth(compiled, request);
        });
        group.MapPost("/definition-editor/export", async (EditorDraft draft, DefinitionService definitions, CancellationToken token) =>
        {
            var bundle = await EditorSourcesAsync(draft, definitions, token);
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

    private static async Task<string> CurrentOriginAsync(StorageRepository storage, CancellationToken token)
    {
        var row = await storage.GetAsync("definition-active", token);
        return row is null ? string.Empty : StorageRepository.Read<Activation>(row).GroupCatalogHash ?? string.Empty;
    }

    private static Task<DefinitionBundle> EditorSourcesAsync(EditorDraft draft, DefinitionService definitions, CancellationToken token)
    {
        // ローカル原文は毎回検証し、Storageへ保存せず既存の編集経路へ渡します。
        if (draft.LocalSources is null)
        {
            return definitions.ReadSourcesAsync(draft.DefinitionHash, token);
        }

        var bundle = EditorLocalFiles.Read(new(draft.LocalSources), definitions, true);
        if (bundle.Hash != draft.DefinitionHash)
        {
            throw new ArgumentException("ローカル原文と下書きの基点版が一致しません。");
        }

        return Task.FromResult(bundle);
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
