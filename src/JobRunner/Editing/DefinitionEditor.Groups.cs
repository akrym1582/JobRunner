using JobRunner.Controllers.Ui;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;

namespace JobRunner.Editing;

/// <summary>ファイル境界と参照を保つグループ操作を、既存の原文編集後に適用します。</summary>
public static partial class DefinitionEditor
{
    private static string? MapGroupPath(string path, IReadOnlyDictionary<string, EditorGroupChange> changes)
    {
        string result = string.Empty;
        string prefix = string.Empty;
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            prefix += "/" + segment;
            var change = changes.GetValueOrDefault(prefix);
            if (change?.Delete == true)
            {
                return null;
            }

            result += "/" + (change?.Name ?? segment);
        }

        return result.Length == 0 ? "/" : result;
    }

    private static List<SourceEvidence> RenderGroups(DefinitionBundle bundle, EditorDraft draft)
    {
        var changes = draft.GroupChanges ?? throw new ArgumentException("グループ操作の投影がありません。");
        if (changes.Count > 1000 || changes.Any(c => c is null || string.IsNullOrEmpty(c.Id) || (c.Delete ? c.Name is not null : !ValidName(c.Name!)))
            || changes.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != changes.Count)
        {
            throw new ArgumentException("グループ操作は重複なしの1000件までです。改名か削除のどちらかを指定してください。");
        }

        var sources = Render(bundle, draft with { EditorVersion = 6 });
        if (changes.Count == 0)
        {
            return sources;
        }

        var documents = sources.Select(s => new Document(s)).ToList();
        var locations = documents.SelectMany(d => d.Syntax.Select(s => (s.Key, Document: d, Syntax: s.Value))).ToDictionary(s => s.Key, StringComparer.Ordinal);
        var intermediate = new DefinitionBundle { Sources = sources, Units = sources.SelectMany(s => Jp1DefinitionParser.Parse(Decode(s).TrimStart('\uFEFF'), s.Mapping)).ToList() };
        CompleteEditorParents(intermediate);
        var units = intermediate.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (!locations.ContainsKey(change.Id) || !units.TryGetValue(change.Id, out var unit) || unit.Type != "g")
            {
                throw new ArgumentException($"{change.Id}: 原文のあるグループだけを改名・削除できます。");
            }

            if ((change.Delete || change.Name != unit.Name) && sources.Any(s => Within(s.Mapping.TargetPath, change.Id)))
            {
                throw new ArgumentException($"{change.Id}: 独立ファイルの配置先またはその祖先は改名・削除できません。配置先を移動せずに扱える原文へ整理してください。");
            }
        }

        var byId = changes.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var mapped = units.Keys.ToDictionary(p => p, p => MapGroupPath(p, byId), StringComparer.Ordinal);
        var remaining = mapped.Where(p => p.Value is not null).ToList();
        if (remaining.Any(p => p.Key != p.Value && p.Value!.Length > 1024))
        {
            throw new ArgumentException("変更後の完全名は1024文字までです。");
        }

        if (remaining.Select(p => p.Value).Distinct(StringComparer.Ordinal).Count() != remaining.Count
            || remaining.Any(p => p.Key != p.Value && units.ContainsKey(p.Value!)))
        {
            throw new ArgumentException("変更後の完全名が重複するか既存名を再利用しています。名前交換・削除名の再利用はできません。");
        }

        foreach (var document in documents)
        {
            if (document.Syntax.Keys.All(p => mapped[p] is null))
            {
                throw new ArgumentException("グループ削除で原文ファイルが空になります。同じファイルに代替ユニットを作成してください。");
            }
        }

        foreach (var change in changes.Where(c => c.Delete && !changes.Any(parent => parent.Delete && parent.Id != c.Id && Within(c.Id, parent.Id))))
        {
            var location = locations[change.Id];
            location.Document.Replace(location.Syntax.Start, location.Syntax.End, string.Empty);
        }

        foreach (var change in changes.Where(c => !c.Delete && mapped[c.Id] is not null && c.Name != units[c.Id].Name))
        {
            var location = locations[change.Id];
            RenameUnit(location.Document, location.Syntax, change.Name!);
        }

        // 完全名として解釈するjcと、直接の子名を持つelだけを更新します。
        // コマンド・コメント等の文字列を置換すると別の意味を変えるため触りません。
        foreach (var unit in units.Values.Where(u => mapped[u.Path] is not null && locations.ContainsKey(u.Path)))
        {
            var location = locations[unit.Path];
            string reference = unit.Get("jc");
            if (reference.Length > 0 && mapped.TryGetValue(reference, out string? next))
            {
                if (next is null)
                {
                    throw new ArgumentException($"{unit.Path}: jcが削除対象{reference}を参照しています。先に参照を解除・変更してください。");
                }

                if (reference != next)
                {
                    location.Document.SetValues(location.Syntax, "jc", [next], true, ordered: true);
                }
            }

            var childChanges = changes.Where(c => ScheduleCalculator.Parent(c.Id) == unit.Path && (c.Delete || c.Name != units[c.Id].Name)).ToDictionary(c => units[c.Id].Name, StringComparer.Ordinal);
            var elements = (unit.Attributes.GetValueOrDefault("el") ?? []).Select(value =>
            {
                if (!childChanges.TryGetValue(ElementName(value), out var child))
                {
                    return value;
                }

                int comma = value.IndexOf(',');
                if (comma < 0)
                {
                    throw new ArgumentException($"{unit.Path}: グループのel表示参照を解決できません。先に原文を修正してください。");
                }

                return child.Delete ? null : child.Name + value[comma..];
            }).Where(value => value is not null).Select(value => value!).ToList();
            location.Document.SetValues(location.Syntax, "el", elements, false, ordered: true);
        }

        var rendered = documents.Select(d => d.Render()).ToList();
        if (draft.LocalSources is not null || draft.NewGroups?.Count > 0 || draft.NewFiles?.Count > 0)
        {
            EditorLocalFiles.ValidateSources(rendered);
        }

        return rendered;
    }

    private static bool Within(string path, string parent) => path == parent || path.StartsWith(parent + "/", StringComparison.Ordinal);
}
