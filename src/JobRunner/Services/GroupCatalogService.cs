using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Storage;

namespace JobRunner.Services;

/// <summary>原文と独立した、世代ごとの不変な所属情報を保存します。</summary>
/// <param name="storage">カタログと停止状態の保存先です。</param>
public sealed class GroupCatalogService(StorageRepository storage)
{
    /// <summary>グループ自身と子孫だけを対象にします。</summary>
    /// <param name="path">対象パスです。</param>
    /// <param name="group">グループの完全名です。</param>
    /// <returns>区切りを含めて所属するかです。</returns>
    public static bool Within(string path, string group) => path == group || path.StartsWith(group.TrimEnd('/') + "/", StringComparison.Ordinal);

    /// <summary>改名を明示した対応だけ引き継ぎ、新規IDを決定的に作ります。</summary>
    /// <param name="next">反映先です。</param>
    /// <param name="activation">反映先の採用版です。</param>
    /// <param name="previous">現在の対応です。</param>
    /// <param name="origins">新完全名から旧完全名への対応です。</param>
    /// <param name="rootOrigins">移動前のルート完全名です。</param>
    /// <returns>保存前のカタログです。</returns>
    public static GroupCatalog Create(DefinitionBundle next, Activation activation, GroupCatalog? previous, Dictionary<string, string>? origins = null, Dictionary<string, string>? rootOrigins = null)
    {
        var result = new GroupCatalog { Namespace = activation.Id };
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in next.Units.Flatten().Where(u => u.Type == "g").OrderBy(u => u.Path, StringComparer.Ordinal))
        {
            string? origin = origins is null ? unit.Path : origins.GetValueOrDefault(unit.Path);
            string id = (origin is null ? null : previous?.Groups.GetValueOrDefault(origin)) ?? StorageRepository.Hash($"{activation.Id}:{activation.Revision}:{unit.Path}");
            if (!used.Add(id))
            {
                throw new ArgumentException("同じグループIDを複数の配置先へ引き継ぐことはできません。");
            }

            result.Groups.Add(unit.Path, id);
        }

        if (origins is not null && origins.Any(p => !result.Groups.ContainsKey(p.Key) || previous?.Groups.ContainsKey(p.Value) != true))
        {
            throw new ArgumentException("グループの改名元または反映先がありません。");
        }

        var previousGroupPaths = previous?.Groups.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal) ?? [];
        var usedRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in DefinitionPublication.Roots(next).Values)
        {
            string? origin = rootOrigins is null ? root.Path : rootOrigins.GetValueOrDefault(root.Path);
            if (rootOrigins is null)
            {
                string parent = JobRunner.Scheduling.ScheduleCalculator.Parent(root.Path);
                if (result.Groups.TryGetValue(parent, out string? groupId))
                {
                    string? oldParent = previousGroupPaths.GetValueOrDefault(groupId);
                    origin = oldParent is null ? null : oldParent.TrimEnd('/') + "/" + root.Name;
                }
            }

            string id = (origin is null ? null : previous?.Roots.GetValueOrDefault(origin)) ?? StorageRepository.Hash($"root:{activation.Id}:{activation.Revision}:{root.Path}");
            if (!usedRoots.Add(id))
            {
                throw new ArgumentException("同じルートを複数の配置先へ引き継げません。");
            }

            result.Roots.Add(root.Path, id);
        }

        if (rootOrigins is not null && rootOrigins.Any(p => !result.Roots.ContainsKey(p.Key) || previous?.Roots.ContainsKey(p.Value) != true))
        {
            throw new ArgumentException("ルートの移動元または反映先がありません。");
        }

        return result;
    }

    /// <summary>削除済みでも残る実行回の停止を解除できるよう、旧所属も照会します。</summary>
    /// <param name="active">現在の採用情報です。</param>
    /// <param name="token">中断通知です。</param>
    /// <returns>固定IDと、現在または旧世代の所属です。</returns>
    public async Task<Dictionary<string, (string Path, GroupCatalog Catalog, bool Retired)>> VisibleAsync(Activation active, CancellationToken token)
    {
        var current = await ReadAsync(active.GroupCatalogHash ?? throw new InvalidDataException("グループ所属が欠損しています。"), token);
        var result = current.Groups.ToDictionary(p => p.Value, p => (Path: p.Key, Catalog: current, Retired: false), StringComparer.Ordinal);
        var roots = (await storage.ListAsync("occurrence:", token)).Select(StorageRepository.Read<Occurrence>).Where(o => o.Namespace == active.Id && o.State is "Waiting" or "Held" or "Running" or "Starting" or "Unknown" or "Interrupted").GroupBy(o => o.RootId).Select(g => g.First());
        foreach (var group in roots.Where(o => o.GroupCatalogHash != active.GroupCatalogHash).GroupBy(o => o.GroupCatalogHash))
        {
            var catalog = await ReadAsync(group.Key ?? throw new InvalidDataException("旧世代の所属が欠損しています。"), token);
            foreach (var root in group)
            {
                string path = JobRunner.Scheduling.ScheduleCalculator.Parent(root.RootPath);
                while (path.Length > 0)
                {
                    if (catalog.Groups.TryGetValue(path, out string? id))
                    {
                        result.TryAdd(id, (path, catalog, true));
                    }

                    if (path == "/")
                    {
                        break;
                    }

                    path = JobRunner.Scheduling.ScheduleCalculator.Parent(path);
                }
            }
        }

        return result;
    }

    /// <summary>保存済み内容をハッシュで検証します。</summary>
    /// <param name="hash">保存時のハッシュです。</param>
    /// <param name="token">中断通知です。</param>
    /// <returns>検証したカタログです。</returns>
    public async Task<GroupCatalog> ReadAsync(string hash, CancellationToken token)
    {
        if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)))
        {
            throw new InvalidDataException("グループ所属の参照が不正です。");
        }

        var data = (await storage.Container.GetBlobClient($"groups/{storage.Partition}/{hash}.json").DownloadContentAsync(token)).Value.Content;
        var value = data.ToObjectFromJson<GroupCatalog>();
        if (StorageRepository.Hash(data.ToArray()) != hash || value?.FormatVersion != 2 || value.Groups.Values.Distinct(StringComparer.Ordinal).Count() != value.Groups.Count || value.Roots.Values.Distinct(StringComparer.Ordinal).Count() != value.Roots.Count)
        {
            throw new InvalidDataException("グループ所属が破損しています。自動再開はしません。");
        }

        return value;
    }

    /// <summary>内容から決まる不変Blobへ保存します。</summary>
    /// <param name="value">カタログです。</param>
    /// <param name="token">中断通知です。</param>
    /// <returns>参照用ハッシュです。</returns>
    public async Task<string> SaveAsync(GroupCatalog value, CancellationToken token)
    {
        var data = BinaryData.FromObjectAsJson(value);
        string hash = StorageRepository.Hash(data.ToArray());
        await storage.PutImmutableAsync($"groups/{storage.Partition}/{hash}.json", data, token);
        return hash;
    }
}
