using Azure;
using Azure.Data.Tables;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using JobRunner.Workers;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Controllers.Ui;

/// <summary>共有下書きと、確認した版の運用反映を提供します。</summary>
public static partial class UiEndpoints
{
    private static void MapLifecycle(RouteGroupBuilder group)
    {
        group.MapGet("/runtime", async (StorageRepository storage, SchedulerWorker worker, CancellationToken token) =>
        {
            var controlRow = await storage.GetAsync("runtime-control", token) ?? throw new InvalidDataException("停止状態を準備中です。再取得してください。");
            var activeRow = await storage.GetAsync("definition-active", token);
            var pending = (await storage.ListAsync("command:", token)).Select(StorageRepository.Read<Operation>).Where(o => o.Status == "Pending" && o.Kind is "Activate" or "PauseAll" or "ResumeAll" or "PauseGroup" or "ResumeGroup").ToList();
            return new RuntimeStatus(StorageRepository.Read<RuntimeControl>(controlRow), activeRow is null ? null : StorageRepository.Read<Activation>(activeRow), worker.Phase, pending);
        });
        group.MapGet("/definition-editor/shared", async (StorageRepository storage, CancellationToken token) =>
        {
            var row = await storage.GetAsync("editor-shared", token);
            if (row is null)
            {
                return Results.NoContent();
            }

            var pointer = StorageRepository.Read<SharedDraftPointer>(row);
            var blob = await storage.Container.GetBlobClient(pointer.Path).DownloadContentAsync(token);
            var saved = blob.Value.Content.ToObjectFromJson<SharedEditorDraft>() ?? throw new InvalidDataException("共有下書きが破損しています。");
            if (saved.Revision != pointer.Revision || !pointer.Path.EndsWith(StorageRepository.Hash(blob.Value.Content.ToArray()) + ".json", StringComparison.Ordinal))
            {
                throw new InvalidDataException("共有下書きの版または本文が一致しません。");
            }

            return Results.Ok(saved);
        }).Produces<SharedEditorDraft>().Produces(204);
        group.MapPut("/definition-editor/shared", async (SharedEditorDraft request, StorageRepository storage, DefinitionService definitions, CancellationToken token) =>
        {
            if (request.Base?.Draft is null || request.Draft is null || request.Revision < 0 || request.BaseRevision < 0 || request.Base.Draft.DefinitionHash != request.Draft.DefinitionHash || request.Draft.EditorVersion is < 1 or > 9 || request.Draft.Networks is null)
            {
                throw new ArgumentException("共有下書きの構造または基点が不正です。");
            }

            // 編集値は未完成でも保存します。基点原文の存在と安全な形式だけを検証します。
            await EditorSourcesAsync(request.Base.Draft, definitions, token);
            var row = await storage.GetAsync("editor-shared", token);
            long revision = row is null ? 0 : StorageRepository.Read<SharedDraftPointer>(row).Revision;
            if (request.Revision != revision)
            {
                // 保存成功後に応答だけ失った場合は、同じ本文の再送を新しい保存にしません。
                var repeated = request with { Revision = request.Revision + 1 };
                var pointer = row is null ? null : StorageRepository.Read<SharedDraftPointer>(row);
                if (pointer?.Revision == repeated.Revision && pointer.Path.EndsWith(StorageRepository.Hash(BinaryData.FromObjectAsJson(repeated).ToArray()) + ".json", StringComparison.Ordinal))
                {
                    return Results.Ok(repeated);
                }

                return Results.Conflict(new { error = "別の画面が共有下書きを保存しました。現在の編集を保持して読み直してください。" });
            }

            var saved = request with { Revision = revision + 1 };
            var data = BinaryData.FromObjectAsJson(saved);
            if (data.ToMemory().Length > 12 * 1024 * 1024)
            {
                throw new ArgumentException("共有下書きは12MiBまでです。");
            }

            string path = $"editor-drafts/{storage.Partition}/{StorageRepository.Hash(data.ToArray())}.json";
            await storage.PutImmutableAsync(path, data, token);
            var entity = storage.Entity("editor-shared", new SharedDraftPointer(saved.Revision, path));
            try
            {
                if (row is null)
                {
                    await storage.Table.AddEntityAsync(entity, token);
                }
                else
                {
                    await storage.Table.UpdateEntityAsync(entity, row.ETag, TableUpdateMode.Replace, token);
                }
            }
            catch (RequestFailedException error) when (error.Status is 409 or 412)
            {
                return Results.Conflict(new { error = "共有下書きが同時に更新されました。現在の編集は保持されています。" });
            }

            return Results.Ok(saved);
        }).Produces<SharedEditorDraft>().Produces(409);
        group.MapPost("/definition-editor/publication-preview", async (EditorDraft draft, DefinitionService definitions, StorageRepository storage, TimeProvider clock, CancellationToken token) =>
        {
            var source = await EditorSourcesAsync(draft, definitions, token);
            var next = definitions.Validate(DefinitionEditor.Render(source, draft));
            DefinitionPublication.ValidateCapacity(next);
            await definitions.SaveAsync(next, token);
            var row = await storage.GetAsync("definition-active", token);
            var active = row is null ? null : await ActiveAsync(storage, token);
            if (draft.OriginCatalogHash is not null && draft.OriginCatalogHash != (active?.GroupCatalogHash ?? string.Empty))
            {
                throw new FormatException("運用定義の所属が変わりました。下書きは保持しています。必要な編集を保存・出力し、最新の運用定義を開き直して移動してください。");
            }

            var old = active is null ? null : await definitions.ReadSourcesAsync(active.DefinitionHash, token);
            var registrations = active is null ? [] : await storage.ListAsync($"registration:{active.Id}:", token);
            var occurrences = (await storage.ListAsync("occurrence:", token)).Where(r => StorageRepository.Read<Occurrence>(r).Namespace == active?.Id).ToList();
            var oldRoots = old is null ? [] : DefinitionPublication.Roots(old).Keys.ToList();
            var newRoots = DefinitionPublication.Roots(next).Keys.ToList();
            var registrationMap = registrations.Select(StorageRepository.Read<Registration>).ToDictionary(r => r.Id, StringComparer.Ordinal);
            var now = clock.GetUtcNow();
            var generations = occurrences.Select(StorageRepository.Read<Occurrence>).GroupBy(o => o.RootId).Select(g =>
            {
                var root = g.Single(o => o.Id == o.RootId);
                var registration = registrationMap[root.RegistrationId];
                return new PublicationGeneration(root.Id, root.RootPath, root.Due, registration.Disabled ? "登録解除済み" : registration.Kind == "Immediate" ? "即時登録" : DefinitionPublication.Protection(g, now));
            }).ToList();
            var oldGroups = old?.Units.Flatten().Where(u => u.Type == "g").Select(u => u.Path).ToHashSet(StringComparer.Ordinal) ?? [];
            var origins = DefinitionEditor.GroupOrigins(source, draft).Where(p => oldGroups.Contains(p.Value)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            var changes = next.Units.Flatten().Where(u => u.Type == "g").Select(u => !origins.TryGetValue(u.Path, out string? origin) ? $"{u.Path}: 新規グループは停止状態で反映" : origin != u.Path ? $"{origin} → {u.Path}: 固定IDと停止状態を維持" : null).Where(v => v is not null).Select(v => v!).ToList();
            changes.AddRange(newRoots.Except(oldRoots, StringComparer.Ordinal).Select(p => $"{p}: 新しい配置先の祖先グループの停止状態を適用。開始済み旧世代の所属は変更しません。"));
            var oldRootPaths = oldRoots.ToHashSet(StringComparer.Ordinal);
            var rootOrigins = DefinitionEditor.RootOrigins(source, draft).Where(p => oldRootPaths.Contains(p.Value) && newRoots.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            var movedRoots = rootOrigins.Where(p => p.Key != p.Value).Select(p => p.Value).ToHashSet(StringComparer.Ordinal);
            changes.AddRange(registrationMap.Values.Where(r => r.Kind == "Confirmed" && !r.Disabled && !r.ReplenishmentStopped && r.Id != StorageRepository.Hash(r.RootPath) && movedRoots.Contains(r.RootPath))
                .Select(r => $"{r.RootPath}: API個別確定登録が残るため反映不可。登録条件を確認・解除し、移動後に再登録してください。"));
            return new PublicationPreview(next.Hash, active?.Revision ?? 0, DefinitionPublication.ImpactHash(row, registrations, occurrences, now, await storage.ListAsync("group-control:", token)), newRoots.Except(oldRoots, StringComparer.Ordinal).ToList(), oldRoots.Except(newRoots, StringComparer.Ordinal).ToList(), generations, DefinitionEditor.Describe(next.Sources, old?.Sources ?? []), old is null ? [] : DefinitionEditor.Describe(old.Sources, old.Sources), origins, changes, rootOrigins);
        });
        group.MapPost("/definition-editor/publish", (Operation operation, StorageRepository storage, CancellationToken token) =>
        {
            if (operation.ExpectedDefinitionRevision is null || string.IsNullOrEmpty(operation.ExpectedImpactHash))
            {
                return Task.FromResult(Results.Problem("差分と反映影響を確認してから反映してください。", statusCode: 400));
            }

            operation.Kind = "Activate";
            return Program.EnqueueAsync(operation, storage, token);
        }).Produces<Operation>(202).Produces<Operation>(200).Produces(409);
    }
}
