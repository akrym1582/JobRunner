using System.Text.RegularExpressions;
using JobRunner.Controllers.Ui;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;

namespace JobRunner.Editing;

/// <summary>安定IDと原文所属を確認してネット構造だけを局所編集します。</summary>
public static partial class DefinitionEditor
{
    /// <summary>既存の配置先と同一原文内のグループだけを追加先として返します。</summary>
    /// <param name="bundle">保存済みの原文です。</param>
    /// <returns>ファイルと親グループの組です。</returns>
    public static List<EditorContainer> Containers(DefinitionBundle bundle)
    {
        var units = bundle.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        var result = new List<EditorContainer>();
        for (int i = 0; i < bundle.Sources.Count; i++)
        {
            var source = bundle.Sources[i];
            if (!units.TryGetValue(source.Mapping.TargetPath, out var parent) || parent.Type == "g")
            {
                result.Add(new(i, source.Mapping.TargetPath));
            }

            var document = new Document(source);
            result.AddRange(document.Syntax.Keys.Where(p => units[p].Type == "g").Select(p => new EditorContainer(i, p)));
        }

        return result.Distinct().ToList();
    }

    private static List<EditorRoot> ProjectRoots(DefinitionBundle bundle)
    {
        var all = bundle.Units.Flatten().ToList();
        var nested = all.Where(u => u.IsNetwork()).SelectMany(u => u.Children).Select(u => u.Path).ToHashSet(StringComparer.Ordinal);
        var files = bundle.Sources.Select((s, i) => (Document: new Document(s), Index: i)).SelectMany(d => d.Document.Syntax.Keys.Select(p => (Path: p, d.Index))).ToDictionary(p => p.Path, p => p.Index, StringComparer.Ordinal);
        return all.Where(u => u.IsNetwork() && !nested.Contains(u.Path)).Select(u => new EditorRoot(
            new EditorNode(u.Path, u.Name, u.Type, u.Attributes.Where(a => NetworkKeys.Contains(a.Key)).ToDictionary(a => a.Key, a => a.Value.ToList(), StringComparer.Ordinal), null, null),
            files[u.Path],
            ScheduleCalculator.Parent(u.Path))).ToList();
    }

    private static List<SourceEvidence> RenderHierarchy(DefinitionBundle bundle, EditorDraft draft)
    {
        var baseline = Project(bundle);
        var roots = draft.Roots ?? throw new ArgumentException("ネット階層のルート情報がありません。");
        var originalRoots = baseline.Roots!.ToDictionary(r => r.Node.Id, StringComparer.Ordinal);
        var networks = draft.Networks.ToDictionary(n => n.Path, StringComparer.Ordinal);
        var oldNetworks = baseline.Networks.ToDictionary(n => n.Path, StringComparer.Ordinal);
        var units = bundle.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        var documents = bundle.Sources.Select(s => new Document(s)).ToList();
        var locations = documents.SelectMany((d, i) => d.Syntax.Select(s => (s.Key, Document: d, Syntax: s.Value, Index: i))).ToDictionary(s => s.Key, StringComparer.Ordinal);
        var allNodes = roots.Select(r => r.Node).Concat(draft.Networks.SelectMany(n => n.Nodes)).ToList();
        if (allNodes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != allNodes.Count)
        {
            throw new ArgumentException("ネット階層のノードIDが重複しています。");
        }

        var currentNodes = allNodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var originalNodes = baseline.Roots!.Select(r => r.Node).Concat(baseline.Networks.SelectMany(n => n.Nodes)).ToDictionary(n => n.Id, StringComparer.Ordinal);
        var owners = draft.Networks.SelectMany(n => n.Nodes.Select(node => (node.Id, Parent: n.Path))).ToDictionary(p => p.Id, p => p.Parent, StringComparer.Ordinal);
        var oldOwners = baseline.Networks.SelectMany(n => n.Nodes.Select(node => (node.Id, Parent: n.Path))).ToDictionary(p => p.Id, p => p.Parent, StringComparer.Ordinal);
        var nodes = allNodes.Where(n => n.Type is "n" or "rn").ToDictionary(n => n.Id, StringComparer.Ordinal);
        if (!nodes.Keys.Order(StringComparer.Ordinal).SequenceEqual(networks.Keys.Order(StringComparer.Ordinal)))
        {
            throw new ArgumentException("ネットのノードと下位の編集内容が一致しません。");
        }

        var containers = Containers(bundle);
        foreach (var root in roots)
        {
            if (root.Node.Type is not ("n" or "rn") || root.Node.X is not null || root.Node.Y is not null)
            {
                throw new ArgumentException("ルートにはネットだけを配置してください。");
            }

            if (originalRoots.TryGetValue(root.Node.Id, out var original))
            {
                if (root.ParentPath != original.ParentPath || root.SourceIndex != original.SourceIndex)
                {
                    throw new ArgumentException("既存ルートの階層・ファイル所属は変更できません。");
                }
            }
            else if (root.Node.Type != "n" || !containers.Contains(new(root.SourceIndex, root.ParentPath)))
            {
                throw new ArgumentException("新規ルートは既存ファイルのグループに通常ネットとして配置してください。");
            }
        }

        foreach (var node in nodes.Values)
        {
            if (units.TryGetValue(node.Id, out var original))
            {
                if (!original.IsNetwork() || node.Type != original.Type || oldOwners.GetValueOrDefault(node.Id) != owners.GetValueOrDefault(node.Id))
                {
                    throw new ArgumentException("既存ネットの種別変更・階層移動はできません。");
                }
            }
            else if (!node.Id.StartsWith("new:", StringComparison.Ordinal))
            {
                throw new ArgumentException("新規ネットにはnew:で始まるIDを指定してください。");
            }

            if (original?.Name != node.Name && !ValidName(node.Name))
            {
                throw new ArgumentException("ネット名に空白だけの名前や構文の区切り文字は使用できません。");
            }

            if (node.Attributes.Any(a => !NetworkKeys.Contains(a.Key) || a.Value.Count > 1))
            {
                throw new ArgumentException("ネットで編集できる属性はコメントです。");
            }
        }

        // 先に所有関係を確定し、循環した下書きから再帰やパスの補完を始めません。
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var sources = new Dictionary<string, int>(StringComparer.Ordinal);
        void Walk(string id, string parent, int source, HashSet<string> visiting)
        {
            if (!visiting.Add(id) || paths.ContainsKey(id))
            {
                throw new ArgumentException("ネット階層が循環または重複しています。");
            }

            paths.Add(id, parent.TrimEnd('/') + "/" + nodes[id].Name);
            sources.Add(id, source);
            foreach (var child in networks[id].Nodes.Where(n => n.Type is "n" or "rn"))
            {
                Walk(child.Id, paths[id], source, visiting);
            }

            visiting.Remove(id);
        }

        foreach (var root in roots)
        {
            Walk(root.Node.Id, root.ParentPath, root.SourceIndex, []);
        }

        if (paths.Count != networks.Count || paths.Values.Distinct(StringComparer.Ordinal).Count() != paths.Count)
        {
            throw new ArgumentException("親のないネット、循環または完全名の重複があります。");
        }

        var removed = oldNetworks.Keys.Where(p => !networks.ContainsKey(p)).ToHashSet(StringComparer.Ordinal);
        foreach (string old in oldNetworks.Keys)
        {
            if ((!paths.TryGetValue(old, out string? next) || old != next) && bundle.Sources.Any(s => s.Mapping.TargetPath == old || s.Mapping.TargetPath.StartsWith(old + "/", StringComparison.Ordinal)))
            {
                throw new ArgumentException($"{old}: 別原文の配置先に使われているネットは改名・削除できません。");
            }
        }

        foreach (string id in removed.Where(id => !removed.Any(parent => id.StartsWith(parent + "/", StringComparison.Ordinal))))
        {
            var location = locations[id];
            location.Document.Replace(location.Syntax.Start, location.Syntax.End, string.Empty);
        }

        foreach (var node in nodes.Values.Where(n => oldNetworks.ContainsKey(n.Id)))
        {
            var location = locations[node.Id];
            if (units[node.Id].Name != node.Name)
            {
                RenameUnit(location.Document, location.Syntax, node.Name);
            }

            location.Document.SetValues(location.Syntax, "cm", node.Attributes.GetValueOrDefault("cm") ?? [], true);
        }

        // ネットとジョブの名前交換・削除名再利用も、同じ構造変更で確定します。
        // 一方だけを改名して再解析すると中間状態にだけ存在する重複で拒否してしまいます。
        foreach (var old in originalNodes.Values.Where(n => n.Type is not ("n" or "rn") && networks.ContainsKey(oldOwners[n.Id])))
        {
            var location = locations[old.Id];
            if (!currentNodes.TryGetValue(old.Id, out var current))
            {
                location.Document.Replace(location.Syntax.Start, location.Syntax.End, string.Empty);
            }
            else if (old.Name != current.Name)
            {
                if (!ValidName(current.Name))
                {
                    throw new ArgumentException("ジョブ名に空白だけの名前や構文の区切り文字は使用できません。");
                }

                RenameUnit(location.Document, location.Syntax, current.Name);
            }
        }

        // ar/elだけを構文単位で更新します。コマンド中のパスやjcは変更しません。
        foreach (var unit in units.Values.Where(u => locations.ContainsKey(u.Path) && !removed.Any(p => u.Path == p || u.Path.StartsWith(p + "/", StringComparison.Ordinal))))
        {
            var changes = originalNodes.Values.Where(n => ScheduleCalculator.Parent(n.Id) == unit.Path && (!currentNodes.TryGetValue(n.Id, out var next) || next.Name != n.Name)).ToDictionary(n => n.Name, n => currentNodes.GetValueOrDefault(n.Id)?.Name, StringComparer.Ordinal);
            if (changes.Count == 0)
            {
                continue;
            }

            var location = locations[unit.Path];
            var elements = (unit.Attributes.GetValueOrDefault("el") ?? []).Select(value =>
            {
                string name = ElementName(value);
                return !changes.TryGetValue(name, out string? changed) ? value : changed is null ? null : changed + value[value.IndexOf(',')..];
            }).Where(v => v is not null).Select(v => v!).ToList();
            location.Document.SetValues(location.Syntax, "el", elements, false);
            if (unit.IsNetwork())
            {
                var edges = (unit.Attributes.GetValueOrDefault("ar") ?? []).Select(value =>
                {
                    var match = Regex.Match(value, @"^\(f=([^,]+),t=([^,\)]+)(.*)\)$", RegexOptions.CultureInvariant);
                    string? from = changes.GetValueOrDefault(match.Groups[1].Value, match.Groups[1].Value);
                    string? to = changes.GetValueOrDefault(match.Groups[2].Value, match.Groups[2].Value);
                    return from is null || to is null ? null : $"(f={from},t={to}{match.Groups[3].Value})";
                }).Where(v => v is not null).Select(v => v!).ToList();
                location.Document.SetValues(location.Syntax, "ar", edges, false);
            }
        }

        string Shell(string id, bool root)
        {
            var node = nodes[id];
            string newline = documents[sources[id]].Newline;
            string attributes = string.Concat(node.Attributes.SelectMany(a => a.Value.Select(v => $"{a.Key}={Quote(v)};")));
            string children = string.Concat(networks[id].Nodes.Where(n => n.Type is "n" or "rn").Select(n => Shell(n.Id, false)));
            return $"{newline}unit={Quote(node.Name)};{{ty={node.Type};sz=10*8;{(root ? "sd=0,ud;ha=y;" : string.Empty)}{attributes}{children}{newline}}}{newline}";
        }

        foreach (string id in networks.Keys.Where(id => !oldNetworks.ContainsKey(id)))
        {
            if (owners.TryGetValue(id, out string? parent) && !oldNetworks.ContainsKey(parent))
            {
                continue; // 新規の祖先の原文内へまとめて生成します。
            }

            var document = documents[sources[id]];
            int position;
            if (parent is not null)
            {
                position = locations[parent].Syntax.End - 1;
            }
            else
            {
                var root = roots.Single(r => r.Node.Id == id);
                position = document.Syntax.TryGetValue(root.ParentPath, out var container) ? container.End - 1 : document.Text.Length;
            }

            document.Replace(position, position, Shell(id, parent is null));
        }

        // ネット構造の原文を基点にし、既存のジョブ・接続・座標編集を再利用します。
        var intermediate = new DefinitionBundle { Hash = bundle.Hash, Sources = documents.Select(d => d.Render()).ToList() };
        foreach (var source in intermediate.Sources)
        {
            intermediate.Units.AddRange(Jp1DefinitionParser.Parse(Decode(source).TrimStart('\uFEFF'), source.Mapping));
        }

        string MapId(string id)
        {
            if (paths.TryGetValue(id, out string? mapped))
            {
                return mapped;
            }

            return units.TryGetValue(id, out var original) && paths.TryGetValue(ScheduleCalculator.Parent(id), out string? parent) ? parent + "/" + (currentNodes.GetValueOrDefault(id)?.Name ?? original.Name) : id;
        }

        var mappedDraft = new EditorDraft(
            bundle.Hash,
            draft.Networks.Select(n => new EditorNetwork(
            paths[n.Path],
            n.Nodes.Select(node => node with { Id = MapId(node.Id) }).ToList(),
            n.Edges.Select(e => new EditorEdge(MapId(e.From), MapId(e.To), e.Conditional)).ToList())).ToList(),
            2);
        return Render(intermediate, mappedDraft);
    }

    private static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) && name == name.Trim() && name is not ("." or "..") && !name.Any(c => char.IsControl(c) || "/\\,;{}()=\"#".Contains(c, StringComparison.Ordinal));

    private static void RenameUnit(Document document, DefinitionSyntax syntax, string name)
    {
        string header = document.Text[syntax.HeaderStart..(syntax.HeaderEnd - 1)];
        int comma = HeaderSeparator(header);
        document.Replace(syntax.HeaderStart, syntax.HeaderEnd, Quote(name) + (comma < 0 ? string.Empty : header[comma..]) + ";");
    }
}
