using System.Text.Json;
using Azure.Data.Tables;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Storage;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Services;

/// <summary>世代の固定所属を使い、階層の停止を実行開始へ適用します。</summary>
public sealed partial class SchedulerEngine
{
    private readonly Dictionary<string, GroupCatalog> groupCatalogs = new(StringComparer.Ordinal);
    private Dictionary<string, RuntimeControl> groupControls = new(StringComparer.Ordinal);

    private async Task<GroupCatalog> ReadGroupsAsync(string? hash, CancellationToken token)
    {
        if (hash is null)
        {
            throw new InvalidDataException("世代のグループ所属が欠損しています。自動起動はしません。");
        }

        if (!groupCatalogs.TryGetValue(hash, out var catalog))
        {
            catalog = await new GroupCatalogService(storage).ReadAsync(hash, token);
            groupCatalogs.Add(hash, catalog);
        }

        return catalog;
    }

    private async Task LoadGroupControlsAsync(CancellationToken token)
    {
        groupControls = (await storage.ListAsync("group-control:", token)).ToDictionary(r => r.RowKey["group-control:".Length..], StorageRepository.Read<RuntimeControl>, StringComparer.Ordinal);
    }

    private async Task<List<RuntimeControl>> MembershipControlsAsync(string? hash, string rootPath, CancellationToken token)
    {
        var catalog = await ReadGroupsAsync(hash, token);
        var result = new List<RuntimeControl>();
        string path = ScheduleCalculator.Parent(rootPath);
        while (path.Length > 0)
        {
            if (catalog.Groups.TryGetValue(path, out string? id))
            {
                result.Add(groupControls.GetValueOrDefault(id) ?? throw new InvalidDataException("グループ停止状態が欠損しています。自動再開はしません。"));
            }

            if (path == "/")
            {
                break;
            }

            path = ScheduleCalculator.Parent(path);
        }

        return result;
    }

    private async Task<bool> GroupPausedAsync(Occurrence occurrence, CancellationToken token) => (await MembershipControlsAsync(occurrence.GroupCatalogHash, occurrence.RootPath, token)).Any(c => c.Paused);

    private async Task<bool> GroupSkippedAsync(string? hash, string path, DateTimeOffset due, CancellationToken token) => (await MembershipControlsAsync(hash, path, token)).Any(c => c.SkipBefore is DateTimeOffset cutoff && due < cutoff);

    private async Task<(string GroupId, string RelativePath)> RootIdentityAsync(Occurrence root, CancellationToken token)
    {
        var catalog = await ReadGroupsAsync(root.GroupCatalogHash, token);
        if (catalog.Roots.TryGetValue(root.RootPath, out string? rootId))
        {
            return (rootId, string.Empty);
        }

        string path = ScheduleCalculator.Parent(root.RootPath);
        while (path.Length > 0)
        {
            if (catalog.Groups.TryGetValue(path, out string? id))
            {
                // パスを改名しても直近の所属IDとネット名は変わりません。
                // 削除後の同名グループは別IDのため、旧世代と同一視しません。
                return (id, root.RootPath[path.TrimEnd('/').Length..]);
            }

            if (path == "/")
            {
                break;
            }

            path = ScheduleCalculator.Parent(path);
        }

        return (string.Empty, root.RootPath);
    }

    private async Task<string> MapGroupRootAsync(Occurrence root, Dictionary<string, string> nextPaths, CancellationToken token)
    {
        var catalog = await ReadGroupsAsync(root.GroupCatalogHash, token);
        if (catalog.Roots.TryGetValue(root.RootPath, out string? rootId))
        {
            // 固定IDの消失は別ネットへの置換です。親が同じでも、その保護枠を引き継ぎません。
            // ID自体が記録されていない場合だけ、従来のグループ単位の補完へ進みます。
            return nextPaths.GetValueOrDefault(rootId) ?? root.RootPath;
        }

        string path = ScheduleCalculator.Parent(root.RootPath);
        while (path.Length > 0)
        {
            if (catalog.Groups.TryGetValue(path, out string? id) && nextPaths.TryGetValue(id, out string? next))
            {
                return next.TrimEnd('/') + root.RootPath[path.TrimEnd('/').Length..];
            }

            if (path == "/")
            {
                break;
            }

            path = ScheduleCalculator.Parent(path);
        }

        return root.RootPath;
    }

    private async Task ApplyGroupRuntimeAsync(TableEntity commandRow, Operation operation, CancellationToken token)
    {
        if (active is null || operation.StateNamespace != active.Id || operation.ExpectedDefinitionRevision != active.Revision)
        {
            throw new FormatException("運用定義の版が変わりました。グループを再確認してください。");
        }

        var catalog = await ReadGroupsAsync(active.GroupCatalogHash, token);
        if (!catalog.Groups.ContainsValue(operation.Target) && !(await new GroupCatalogService(storage).VisibleAsync(active, token)).ContainsKey(operation.Target))
        {
            throw new FormatException("現在のグループを指定してください。");
        }

        var row = await storage.GetAsync("group-control:" + operation.Target, token) ?? throw new InvalidDataException("グループ停止状態が欠損しています。");
        var control = StorageRepository.Read<RuntimeControl>(row);
        if (operation.ExpectedControlRevision != control.Revision)
        {
            throw new FormatException("停止状態の版が変わりました。状態を再確認してください。");
        }

        bool pause = operation.Kind == "PauseGroup";
        var plan = new OperationMutation { FormatVersion = 2, OperationId = operation.Id };
        if (control.Paused != pause)
        {
            control.Revision++;
            control.Paused = pause;
            var now = clock.GetUtcNow();
            if (pause)
            {
                control.PausedAt = now;
            }
            else
            {
                control.SkipBefore = control.SkipBefore > now ? control.SkipBefore : now;
                foreach (var occurrenceRow in await storage.ListAsync("occurrence:", token))
                {
                    var value = StorageRepository.Read<Occurrence>(occurrenceRow);
                    if (value.Namespace != active.Id || value.Id != value.RootId || value.Started || value.State is not ("Waiting" or "Held") || value.Due >= control.SkipBefore)
                    {
                        continue;
                    }

                    var membership = await ReadGroupsAsync(value.GroupCatalogHash, token);
                    if (membership.Groups.Any(p => p.Value == operation.Target && GroupCatalogService.Within(value.RootPath, p.Key)))
                    {
                        value.State = "Skipped";
                        value.FinishedAt = now;
                        value.Reason = "グループ再開時刻より前の未開始予定です。";
                        plan.Changes.Add(Change(occurrenceRow, value));
                    }
                }
            }
        }

        plan.Changes.Add(Change(row, control));
        operation.Result = pause ? "Paused" : "Running";
        await new OperationMutationService(storage, clock).ApplyAsync(commandRow, operation, plan, CommitAsync, leader!.Ensure, StopOwnedAsync, token);
        await LoadGroupControlsAsync(token);
    }

    private async Task PreparePublicationGroupsAsync(Activation updated, DefinitionBundle next, Operation operation, OperationMutation plan, CancellationToken token)
    {
        var previous = active?.GroupCatalogHash is null ? null : await ReadGroupsAsync(active.GroupCatalogHash, token);
        GroupCatalog catalog;
        try
        {
            catalog = GroupCatalogService.Create(next, updated, previous, operation.GroupOrigins, operation.RootOrigins);
        }
        catch (ArgumentException ex)
        {
            // 利用者の改名対応だけを拒否に変換します。Storage障害は再試行対象です。
            throw new FormatException(ex.Message, ex);
        }

        updated.GroupCatalogHash = await new GroupCatalogService(storage).SaveAsync(catalog, token);
        var previousIds = previous?.Groups.Values.ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (string id in catalog.Groups.Values)
        {
            if (previousIds.Contains(id))
            {
                continue;
            }

            var control = new RuntimeControl { Paused = true, PausedAt = clock.GetUtcNow() };
            plan.Changes.Add(new ArchivedRow("group-control:" + id, string.Empty, JsonSerializer.Serialize(control)));
        }
    }
}
