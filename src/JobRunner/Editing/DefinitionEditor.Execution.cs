using JobRunner.Controllers.Ui;
using JobRunner.Import;
using JobRunner.Models;

namespace JobRunner.Editing;

/// <summary>旧投影で省略された実行属性を削除せず、版5で明示された差分だけを反映します。</summary>
public static partial class DefinitionEditor
{
    private static readonly HashSet<string> JobExecutionKeys = ["ha", "jd", "wth", "tho", "etm", "ex"];
    private static readonly HashSet<string> NetworkExecutionKeys = ["ha", "mp", "sy", "ey", "fd", "ex"];
    private static readonly HashSet<string> CommonExecutionKeys = ["ex"];

    private static HashSet<string> ExecutionKeys(string type) => type is "n" or "rn" ? NetworkExecutionKeys : IsJob(type) ? JobExecutionKeys : CommonExecutionKeys;

    private static List<EditorAttributes> ProjectExecution(DefinitionBundle bundle)
    {
        var present = bundle.Sources.SelectMany(s => new Document(s).Syntax.Keys).ToHashSet(StringComparer.Ordinal);
        return bundle.Units.Flatten().Where(u => present.Contains(u.Path)).Select(u => new EditorAttributes(
            u.Path, u.Attributes.Where(a => ExecutionKeys(u.Type).Contains(a.Key)).ToDictionary(a => a.Key, a => a.Value.ToList(), StringComparer.Ordinal))).ToList();
    }

    private static List<SourceEvidence> RenderExecution(DefinitionBundle bundle, EditorDraft draft)
    {
        var execution = draft.Execution ?? throw new ArgumentException("実行属性の投影がありません。元版を読み直してください。");
        var nodes = (draft.Roots ?? []).Select(r => r.Node).Concat(draft.Networks.SelectMany(n => n.Nodes)).ToDictionary(n => n.Id, StringComparer.Ordinal);
        var groups = ProjectTiming(bundle, false).Select(g => g.Id).ToList();
        if (!execution.Select(e => e.Id).Order(StringComparer.Ordinal).SequenceEqual(nodes.Keys.Concat(groups).Order(StringComparer.Ordinal)))
        {
            throw new ArgumentException("実行属性の対象が一致しないか重複しています。");
        }

        // 構造・予定の編集を確定した後で、安定IDを新しい完全名へ対応付けます。
        var sources = Render(bundle, draft with { EditorVersion = 4 });
        var paths = TimingPaths(draft);
        foreach (var network in draft.Networks)
        {
            foreach (var node in network.Nodes.Where(n => !paths.ContainsKey(n.Id)))
            {
                paths.Add(node.Id, paths[network.Path] + "/" + node.Name);
            }
        }

        foreach (string group in groups)
        {
            paths.Add(group, group);
        }

        var documents = sources.Select(s => new Document(s)).ToList();
        var locations = documents.SelectMany(d => d.Syntax.Select(s => (s.Key, Document: d, Syntax: s.Value))).ToDictionary(s => s.Key, StringComparer.Ordinal);
        var units = sources.SelectMany(s => Jp1DefinitionParser.Parse(Decode(s).TrimStart('\uFEFF'), s.Mapping)).Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        foreach (var entry in execution)
        {
            string path = paths[entry.Id];
            var unit = units[path];
            var keys = ExecutionKeys(unit.Type);
            if (entry.Attributes.Any(a => !keys.Contains(a.Key) || a.Value.Count == 0 || (a.Key is not ("sy" or "ey") && a.Value.Count != 1)))
            {
                throw new ArgumentException($"{path}: 未対応・空配列・重複した実行属性です。");
            }

            foreach (string key in keys)
            {
                var values = entry.Attributes.GetValueOrDefault(key) ?? [];
                if ((unit.Attributes.GetValueOrDefault(key) ?? []).SequenceEqual(values))
                {
                    continue;
                }

                foreach (string value in values)
                {
                    // exだけは引用値として構文記号をエスケープし、名称の対応付けは既存検証へ渡します。
                    ValidateTimingValue(key, value);
                }

                var location = locations[path];
                location.Document.SetValues(location.Syntax, key, values, key == "ex", ordered: true);
            }
        }

        return documents.Select(d => d.Render()).ToList();
    }
}
