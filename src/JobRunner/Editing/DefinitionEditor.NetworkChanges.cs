using System.Text.Json;
using JobRunner.Controllers.Ui;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;

namespace JobRunner.Editing;

/// <summary>同じ継承条件のルート移動と、接続を維持する子ネットの種別変更です。</summary>
public static partial class DefinitionEditor
{
    private static Dictionary<string, string> NetworkPaths(EditorDraft draft)
    {
        var groups = (draft.GroupChanges ?? []).ToDictionary(c => c.Id, StringComparer.Ordinal);
        return TimingPaths(draft).Select(p => (p.Key, Path: MapGroupPath(p.Value, groups))).Where(p => p.Path is not null)
            .ToDictionary(p => p.Key, p => p.Path!, StringComparer.Ordinal);
    }

    private static string MapNetworkPath(EditorDraft draft, string path)
    {
        if (draft.EditorVersion != 8)
        {
            return path;
        }

        var paths = NetworkPaths(draft);
        var groups = (draft.GroupChanges ?? []).ToDictionary(c => c.Id, StringComparer.Ordinal);
        foreach (var change in draft.NetworkChanges ?? [])
        {
            if (change.ParentPath is not null && paths.TryGetValue(change.Id, out string? old) && Within(path, old))
            {
                string parent = MapGroupPath(change.ParentPath, groups) ?? throw new ArgumentException("移動先グループが削除されています。");
                return parent.TrimEnd('/') + old[old.LastIndexOf('/')..] + path[old.Length..];
            }
        }

        return path;
    }

    private static List<SourceEvidence> RenderNetworkChanges(DefinitionBundle bundle, EditorDraft draft)
    {
        var changes = draft.NetworkChanges ?? throw new ArgumentException("ネット操作の投影がありません。");
        if (changes.Count > 1000 || changes.Any(c => c is null || string.IsNullOrEmpty(c.Id) || (c.ParentPath is null && c.Type is null)
            || (c.Type is not null && c.Type is not ("n" or "rn"))
            || (c.ParentPath is not null && (c.ParentPath.Length > 1024 || c.ParentPath.Any(char.IsControl) || Jp1DefinitionParser.NormalizePath(c.ParentPath) != c.ParentPath)))
            || changes.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != changes.Count)
        {
            throw new ArgumentException("ネット操作は重複なしの1000件までです。正規の配置先またはn/rnを指定してください。");
        }

        var sources = Render(bundle, draft with { EditorVersion = 7 });
        if (changes.Count == 0)
        {
            return sources;
        }

        var documents = sources.Select(s => new Document(s)).ToList();
        var locations = documents.SelectMany((d, i) => d.Syntax.Select(s => (s.Key, Document: d, Syntax: s.Value, Index: i))).ToDictionary(s => s.Key, StringComparer.Ordinal);
        var parsed = new DefinitionBundle { Sources = sources, Units = sources.SelectMany(s => Jp1DefinitionParser.Parse(Decode(s).TrimStart('\uFEFF'), s.Mapping)).ToList() };
        CompleteEditorParents(parsed);
        var units = parsed.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        var paths = NetworkPaths(draft);
        var groups = (draft.GroupChanges ?? []).ToDictionary(c => c.Id, StringComparer.Ordinal);
        var moves = new Dictionary<string, string>(StringComparer.Ordinal);
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (!paths.TryGetValue(change.Id, out string? path) || !locations.ContainsKey(path))
            {
                throw new ArgumentException($"{change.Id}: 操作するネットが存在しません。");
            }

            var unit = units[path];
            string parentPath = ScheduleCalculator.Parent(path);
            units.TryGetValue(parentPath, out var parent);
            if (change.Type is not null && change.Type != unit.Type)
            {
                if (parent?.IsNetwork() != true)
                {
                    throw new ArgumentException("種別切替は同じ親内の子ネットn/rnだけが対象です。");
                }

                var incoming = DefinitionValidator.Edges(parent).Where(e => e.To == path).ToList();
                if (incoming.Any(e => e.Conditional) || (change.Type == "rn" && incoming.Count == 0))
                {
                    throw new ArgumentException("条件接続を持つネットの切替、先行のないリカバリー化はできません。先に接続を編集してください。");
                }

                locations[path].Document.SetValues(locations[path].Syntax, "ty", [change.Type], false);
                types.Add(path, change.Type);
            }

            if (change.ParentPath is null)
            {
                continue;
            }

            string target = MapGroupPath(change.ParentPath, groups) ?? throw new ArgumentException("移動先グループが削除されています。");
            if (parent?.IsNetwork() == true || unit.Type != "n" || (change.Type is not null && change.Type != "n"))
            {
                throw new ArgumentException("移動は通常ルートネットのグループ間だけに対応します。子ネットの別親移動・昇格・降格はできません。");
            }

            var source = sources[locations[path].Index];
            if ((target != source.Mapping.TargetPath && (!locations.TryGetValue(target, out var destination) || destination.Index != locations[path].Index))
                || (units.TryGetValue(target, out var targetUnit) && targetUnit.Type != "g"))
            {
                throw new ArgumentException("移動先は同じ原文ファイルのグループまたは配置先にしてください。ファイルをまたぐ移動はできません。");
            }

            if (target == parentPath)
            {
                continue;
            }

            if (sources.Any(s => Within(s.Mapping.TargetPath, path)))
            {
                throw new ArgumentException("独立ファイルの配置先に使われるネットは移動できません。");
            }

            if (InheritanceSignature(parentPath, units) != InheritanceSignature(target, units))
            {
                throw new ArgumentException("移動元と先のカレンダー・実行先・所有者等の継承条件が異なります。継承を変える移動は未対応です。");
            }

            string next = target.TrimEnd('/') + "/" + unit.Name;
            if (units.Keys.Any(p => Within(p, path) && (next.Length + p.Length - path.Length > 1024 || units.ContainsKey(next + p[path.Length..]))))
            {
                throw new ArgumentException("移動後の完全名が既存ユニットと重複するか1024文字を超えます。名前交換・削除名再利用はできません。");
            }

            moves.Add(path, target);
        }

        foreach (var parent in types.Keys.Select(ScheduleCalculator.Parent).Distinct(StringComparer.Ordinal))
        {
            var values = (units[parent].Attributes.GetValueOrDefault("el") ?? []).Select(value =>
            {
                string child = parent + "/" + ElementName(value);
                if (!types.TryGetValue(child, out string? type))
                {
                    return value;
                }

                string[] parts = value.Split(',', 3);
                if (parts.Length != 3 || parts[1] != units[child].Type)
                {
                    throw new ArgumentException("種別切替のel表示参照を解決できません。先に原文を整理してください。");
                }

                return parts[0] + "," + type + "," + parts[2];
            }).ToList();
            locations[parent].Document.SetValues(locations[parent].Syntax, "el", values, false, ordered: true);
        }

        // 種別の置換を先に確定し、その後にブロックを移します。重なった編集範囲を作りません。
        sources = documents.Select(d => d.Render()).ToList();
        documents = sources.Select(s => new Document(s)).ToList();
        locations = documents.SelectMany((d, i) => d.Syntax.Select(s => (s.Key, Document: d, Syntax: s.Value, Index: i))).ToDictionary(s => s.Key, StringComparer.Ordinal);
        var elements = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string> Elements(string path)
        {
            if (!elements.TryGetValue(path, out var values))
            {
                values = units[path].Attributes.GetValueOrDefault("el")?.ToList() ?? [];
                elements.Add(path, values);
            }

            return values;
        }

        var destinations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, target) in moves)
        {
            string name = units[path].Name;
            if (!destinations.Add(target.TrimEnd('/') + "/" + name))
            {
                throw new ArgumentException("複数の移動先でネット名が重複しています。");
            }

            string parent = ScheduleCalculator.Parent(path);
            if (locations.ContainsKey(parent))
            {
                var old = Elements(parent);
                var transfer = old.Where(v => ElementName(v) == name).ToList();
                if (transfer.Count > 0)
                {
                    if (locations[parent].Index != locations[path].Index || !locations.TryGetValue(target, out var targetLocation) || targetLocation.Index != locations[path].Index
                        || transfer.Any(v => !v.Contains(',', StringComparison.Ordinal)) || Elements(target).Any(v => ElementName(v) == name))
                    {
                        throw new ArgumentException("移動先で親のel表示情報を保持できません。表示参照を先に整理してください。");
                    }

                    old.RemoveAll(v => ElementName(v) == name);
                    Elements(target).AddRange(transfer);
                }
            }

            var location = locations[path];

            // 配置先の親が独立ファイルにあっても、移す原文は自分のファイル末尾です。
            int position = locations.TryGetValue(target, out var container) && container.Index == location.Index ? container.Syntax.End - 1 : location.Document.Text.Length;
            string block = location.Document.Text[location.Syntax.Start..location.Syntax.End];
            location.Document.Replace(location.Syntax.Start, location.Syntax.End, string.Empty);
            location.Document.Replace(position, position, location.Document.Newline + block + location.Document.Newline);
        }

        foreach (var (path, values) in elements)
        {
            locations[path].Document.SetValues(locations[path].Syntax, "el", values, false, ordered: true);
        }

        var rendered = documents.Select(d => d.Render()).ToList();
        EditorLocalFiles.ValidateSources(rendered);
        return rendered;
    }

    private static string InheritanceSignature(string path, Dictionary<string, UnitDefinition> units)
    {
        List<string> chain = [];
        while (units.TryGetValue(path, out var unit))
        {
            var attributes = unit.Attributes.Where(a => CalendarKeys.Contains(a.Key) || a.Key == "ex").OrderBy(a => a.Key, StringComparer.Ordinal).ToDictionary(a => a.Key, a => a.Value);
            if (attributes.Count > 0 || unit.OwnerName is not null || unit.PermissionMode is not null || unit.ResourceGroup is not null)
            {
                chain.Add(JsonSerializer.Serialize(new { attributes, unit.OwnerName, unit.PermissionMode, unit.ResourceGroup }));
            }

            path = ScheduleCalculator.Parent(path);
        }

        return JsonSerializer.Serialize(chain);
    }
}
