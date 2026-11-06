using System.Text;
using System.Text.RegularExpressions;
using JobRunner.Controllers.Ui;
using JobRunner.Import;
using JobRunner.Models;

namespace JobRunner.Editing;

/// <summary>原文の局所置換だけで下書きを生成します。Storageへの書込みは行いません。</summary>
public static partial class DefinitionEditor
{
    private static readonly HashSet<string> EditableKeys = ["cm", "te", "sc", "prm", "wkp", "env"];
    private static readonly HashSet<string> JudgmentKeys = ["cm", "ej", "ejc", "ejl", "ejh", "ejm", "eju", "ejf", "ejv", "ejt", "eji", "ejs", "ejg", "ha"];
    private static readonly HashSet<string> QuotedKeys = ["cm", "te", "sc", "prm", "wkp", "env", "ejf", "ejt"];
    private static readonly HashSet<string> NetworkKeys = ["cm"];

    /// <summary>原文に存在するネットと編集項目を投影します。</summary>
    /// <param name="bundle">原文検証済みの版です。</param>
    /// <param name="editorVersion">旧要求の欠落属性を削除と解釈しないための投影版です。</param>
    /// <returns>元パスで識別する下書きです。</returns>
    public static EditorDraft Project(DefinitionBundle bundle, int editorVersion = 3) => new(
        bundle.Hash,
        bundle.Units.Flatten().Where(u => u.IsNetwork()).Select(network => new EditorNetwork(
        network.Path,
        network.Children.Select(unit =>
        {
            var position = Position(network, unit.Name);
            return new EditorNode(unit.Path, unit.Name, unit.Type, unit.Attributes.Where(a => Keys(unit.Type, editorVersion).Contains(a.Key)).ToDictionary(a => a.Key, a => a.Value.ToList(), StringComparer.Ordinal), position?.X, position?.Y);
        }).ToList(),
        DefinitionValidator.Edges(network).Select(e => new EditorEdge(e.From, e.To, e.Conditional)).ToList())).ToList(),
        editorVersion,
        editorVersion >= 3 ? ProjectRoots(bundle) : null,
        editorVersion >= 4 ? ProjectTiming(bundle, true) : null,
        editorVersion >= 4 ? ProjectTiming(bundle, false) : null);

    /// <summary>元の文字コードで原文を表示します。</summary>
    /// <param name="sources">対象原文です。</param>
    /// <param name="original">変更比較の基点です。</param>
    /// <returns>読取専用の表示です。</returns>
    public static List<EditorSource> Describe(List<SourceEvidence> sources, List<SourceEvidence> original) => sources.Select((source, index) => new EditorSource(source.Mapping.BlobPath, source.Mapping.Encoding, source.Mapping.TargetPath, Decode(source), source.Base64 != original[index].Base64)).ToList();

    /// <summary>下書きを原文へ適用します。意味検証は既存DefinitionServiceで別途行います。</summary>
    /// <param name="bundle">基点版です。</param>
    /// <param name="draft">編集投影です。</param>
    /// <returns>元の配置と文字コードを保つ候補原文です。</returns>
    public static List<SourceEvidence> Render(DefinitionBundle bundle, EditorDraft draft)
    {
        if (bundle.Hash != draft.DefinitionHash)
        {
            throw new ArgumentException("下書きの基点版が一致しません。");
        }

        int editorVersion = draft.EditorVersion ?? 1;
        if (editorVersion == 4)
        {
            return RenderTiming(bundle, draft);
        }

        if (editorVersion == 3)
        {
            return RenderHierarchy(bundle, draft);
        }

        if (editorVersion is not (1 or 2))
        {
            throw new ArgumentException("未対応の下書き形式です。");
        }

        var baseline = Project(bundle, editorVersion);
        if (!baseline.Networks.Select(n => n.Path).Order(StringComparer.Ordinal).SequenceEqual(draft.Networks.Select(n => n.Path).Order(StringComparer.Ordinal)))
        {
            throw new ArgumentException("既存ネットの追加・削除・名前変更はできません。");
        }

        var originals = baseline.Networks.ToDictionary(n => n.Path, StringComparer.Ordinal);
        var documents = bundle.Sources.Select(s => new Document(s)).ToList();
        var locations = documents.SelectMany(d => d.Syntax.Select(s => (s.Key, Document: d, Syntax: s.Value))).ToDictionary(s => s.Key, StringComparer.Ordinal);
        var units = bundle.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        foreach (var network in draft.Networks)
        {
            var before = originals[network.Path];
            var oldNodes = before.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            if (network.Nodes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != network.Nodes.Count || network.Nodes.Select(n => n.Name).Distinct(StringComparer.Ordinal).Count() != network.Nodes.Count)
            {
                throw new ArgumentException($"{network.Path}: ノードIDまたは名前が重複しています。");
            }

            var location = locations[network.Path];
            var document = location.Document;
            var syntax = location.Syntax;
            foreach (var old in before.Nodes.Where(n => network.Nodes.All(current => current.Id != n.Id)))
            {
                if (!CanEdit(old.Type, editorVersion))
                {
                    throw new ArgumentException("この下書きでは対象の種別を削除できません。");
                }

                var deleted = locations[old.Id];
                deleted.Document.Replace(deleted.Syntax.Start, deleted.Syntax.End, string.Empty);
            }

            foreach (var node in network.Nodes)
            {
                oldNodes.TryGetValue(node.Id, out var old);
                if (old is null)
                {
                    if (!node.Id.StartsWith("new:", StringComparison.Ordinal) || !CanEdit(node.Type, editorVersion))
                    {
                        throw new ArgumentException("編集可能なジョブ種別と新規IDを指定してください。");
                    }
                }
                else if (old.Type != node.Type || (!CanEdit(old.Type, editorVersion) && (old.Name != node.Name || !SameAttributes(old.Attributes, node.Attributes))))
                {
                    throw new ArgumentException("既存の種別または編集対象外の属性は変更できません。");
                }

                if (old?.Name != node.Name && (string.IsNullOrWhiteSpace(node.Name) || node.Name != node.Name.Trim() || node.Name is "." or ".." || node.Name.Any(c => char.IsControl(c) || "/\\,;{}()=\"#".Contains(c, StringComparison.Ordinal))))
                {
                    throw new ArgumentException("ジョブ名に空白だけの名前や構文の区切り文字は使用できません。");
                }

                var editableKeys = Keys(node.Type, editorVersion);
                if (node.Attributes.Keys.Any(k => !editableKeys.Contains(k)) || node.Attributes.Any(a => a.Key != "env" && a.Value.Count > 1))
                {
                    throw new ArgumentException("編集対象外または重複した属性です。");
                }

                foreach (var attribute in node.Attributes)
                {
                    // 現在の方式で使わない既存値も、原文を変更しない限りそのまま保持します。
                    if (old is not null && old.Attributes.TryGetValue(attribute.Key, out var originalValues) && originalValues.SequenceEqual(attribute.Value))
                    {
                        continue;
                    }

                    foreach (string value in attribute.Value)
                    {
                        FormatValue(attribute.Key, value);
                    }
                }

                if (node.X is < 0 or > 16000 || node.Y is < 0 or > 10000 || node.X.HasValue != node.Y.HasValue)
                {
                    throw new ArgumentException($"{node.Name}: 座標は水平0〜16000、垂直0〜10000を両方指定してください。");
                }

                if (old is null)
                {
                    string attributes = string.Join(document.Newline + "    ", node.Attributes.SelectMany(a => a.Value.Select(value => a.Key + "=" + FormatValue(a.Key, value) + ";")));
                    document.Replace(syntax.End - 1, syntax.End - 1, $"{document.Newline}  unit={Quote(node.Name)}; {{{document.Newline}    ty={node.Type};{document.Newline}    {attributes}{document.Newline}  }}{document.Newline}");
                }
                else if (CanEdit(old.Type, editorVersion))
                {
                    var existing = locations[old.Id];
                    if (old.Name != node.Name)
                    {
                        // ヘッダーの名前だけを差し替え、許可モード・所有者などの原文を維持します。
                        string header = existing.Document.Text[existing.Syntax.HeaderStart..(existing.Syntax.HeaderEnd - 1)];
                        int comma = HeaderSeparator(header);
                        existing.Document.Replace(existing.Syntax.HeaderStart, existing.Syntax.HeaderEnd, Quote(node.Name) + (comma < 0 ? string.Empty : header[comma..]) + ";");
                    }

                    foreach (string key in editableKeys)
                    {
                        existing.Document.SetValues(existing.Syntax, key, node.Attributes.GetValueOrDefault(key) ?? [], QuotedKeys.Contains(key));
                    }
                }
            }

            var byId = network.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            var edges = network.Edges.Select(edge =>
            {
                if (!byId.TryGetValue(edge.From, out var from) || !byId.TryGetValue(edge.To, out var to))
                {
                    throw new ArgumentException($"{network.Path}: 接続先のジョブがありません。");
                }

                return $"(f={from.Name},t={to.Name},{(edge.Conditional ? "con" : "seq")})";
            }).ToList();
            if (!before.Edges.SequenceEqual(network.Edges) || network.Nodes.Any(n => oldNodes.TryGetValue(n.Id, out var old) && n.Name != old.Name))
            {
                document.SetValues(syntax, "ar", edges, false, value => value.EndsWith(",seq)", StringComparison.Ordinal) ? value[..^5] + ")" : value);
            }

            var elements = new List<string>();
            var changedPositions = before.Nodes.Where(old =>
            {
                var node = network.Nodes.Find(n => n.Id == old.Id);
                return node is null || node.Name != old.Name || node.X != old.X || node.Y != old.Y;
            }).ToDictionary(old => old.Name, old => network.Nodes.Find(n => n.Id == old.Id), StringComparer.Ordinal);
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            bool layoutChanged = changedPositions.Count > 0;

            // 名前交換でも、変更後の名前を次の置換の対象にしないよう原文から一度だけ投影します。
            foreach (string element in units[network.Path].Attributes.GetValueOrDefault("el") ?? [])
            {
                string name = ElementName(element);
                if (!changedPositions.TryGetValue(name, out var node))
                {
                    elements.Add(element);
                }
                else if (node?.X is int x && node.Y is int y && emitted.Add(name))
                {
                    elements.Add($"{node.Name},{node.Type},+{x} +{y}");
                }
            }

            foreach (var position in changedPositions.Where(p => !emitted.Contains(p.Key)))
            {
                if (position.Value is { X: int x, Y: int y } node)
                {
                    elements.Add($"{node.Name},{node.Type},+{x} +{y}");
                }
            }

            foreach (var node in network.Nodes.Where(n => !oldNodes.ContainsKey(n.Id)))
            {
                layoutChanged = true;
                if (node.X is int x && node.Y is int y)
                {
                    elements.Add($"{node.Name},{node.Type},+{x} +{y}");
                }
            }

            if (layoutChanged)
            {
                document.SetValues(syntax, "el", elements, false);
            }

            // 名前変更や削除だけでは、既存の非グリッド座標やszを補正しません。
            var movedNodes = network.Nodes.Where(node => !oldNodes.TryGetValue(node.Id, out var old) || node.X != old.X || node.Y != old.Y).ToList();
            if (movedNodes.Count > 0)
            {
                // elの座標上限とszのアイコン数上限は別です。非グリッド座標は保持し、表示領域だけを100まで広げます。
                int columns = Math.Clamp(movedNodes.Select(n => ((n.X ?? 0) / 160) + 1).Max(), 1, 100);
                int rows = Math.Clamp(movedNodes.Select(n => ((n.Y ?? 0) / 96) + 1).Max(), 1, 100);
                string size = units[network.Path].Get("sz", "10*8");
                var match = Regex.Match(size, @"^(\d+)[*x＊](\d+)$", RegexOptions.CultureInvariant);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int oldColumns) && int.TryParse(match.Groups[2].Value, out int oldRows) && (columns > oldColumns || rows > oldRows))
                {
                    document.SetValues(syntax, "sz", [$"{Math.Max(columns, oldColumns)}*{Math.Max(rows, oldRows)}"], false);
                }
            }
        }

        return documents.Select(d => d.Render()).ToList();
    }

    private static bool IsJob(string type) => type is "j" or "pj" or "rj" or "rp";

    private static bool CanEdit(string type, int version) => IsJob(type) || (version >= 2 && type is "jdj" or "rjdj");

    private static HashSet<string> Keys(string type, int version) => version >= 3 && type is "n" or "rn" ? NetworkKeys : version >= 2 && type is "jdj" or "rjdj" ? JudgmentKeys : EditableKeys;

    private static string FormatValue(string key, string value)
    {
        if (QuotedKeys.Contains(key))
        {
            return Quote(value);
        }

        // 非引用の判定値から属性・ユニットを注入できないよう、構文区切りを拒否します。
        if (!Regex.IsMatch(value, key == "ejv" ? @"^\?AJS2[A-Z0-9.]{1,64}\?$" : "^[a-z0-9]+$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException($"判定属性{key}の形式が不正です。");
        }

        return value;
    }

    private static bool SameAttributes(Dictionary<string, List<string>> left, Dictionary<string, List<string>> right) => left.Count == right.Count && left.All(a => right.TryGetValue(a.Key, out var values) && a.Value.SequenceEqual(values));

    private static string ElementName(string value) => value.Split(',')[0].Trim();

    private static (int X, int Y)? Position(UnitDefinition network, string name)
    {
        string? value = network.Attributes.GetValueOrDefault("el")?.FirstOrDefault(v => ElementName(v) == name);
        var match = Regex.Match(value ?? string.Empty, @"^[^,]+,[^,]+,\s*\+(\d+)\s+\+(\d+)\s*$", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out int x) && int.TryParse(match.Groups[2].Value, out int y) ? (x, y) : null;
    }

    private static int HeaderSeparator(string header)
    {
        bool quoted = false;
        for (int i = 0; i < header.Length; i++)
        {
            if (quoted && header[i] == '#')
            {
                i++;
            }
            else if (header[i] == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && header[i] == ',')
            {
                return i;
            }
        }

        return -1;
    }

    private static string Quote(string value) => "\"" + value.Replace("#", "##", StringComparison.Ordinal).Replace("\"", "#\"", StringComparison.Ordinal) + "\"";

    private static Encoding EncodingFor(SourceEvidence source)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(source.Mapping.Encoding, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static string Decode(SourceEvidence source) => EncodingFor(source).GetString(Convert.FromBase64String(source.Base64));

    private sealed class Document
    {
        private readonly SourceEvidence source;
        private readonly List<(int Start, int End, string Text)> replacements = [];

        public Document(SourceEvidence source)
        {
            this.source = source;
            Text = Decode(source);

            // BOMを含む位置で扱うため、解析時だけ同じ長さの空白へ置換します。
            string parseText = Text.Length > 0 && Text[0] == '\uFEFF' ? " " + Text[1..] : Text;
            Jp1DefinitionParser.ParseCore(parseText, source.Mapping, Syntax);
            Newline = Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        }

        public string Text { get; }

        public string Newline { get; }

        public Dictionary<string, DefinitionSyntax> Syntax { get; } = new(StringComparer.Ordinal);

        public void Replace(int start, int end, string text) => replacements.Add((start, end, text));

        public void SetValues(DefinitionSyntax syntax, string key, List<string> values, bool quoted, Func<string, string>? normalize = null, bool ordered = false)
        {
            normalize ??= value => value;
            var previous = syntax.Attributes.Where(a => a.Key == key).ToList();
            if (previous.Select(a => normalize(a.Value)).SequenceEqual(values.Select(normalize)))
            {
                return;
            }

            if (key == "env" || ordered)
            {
                // 環境変数の後勝ちと、予定・カレンダーの入力順序を維持します。
                for (int i = 0; i < previous.Count; i++)
                {
                    if (i >= values.Count)
                    {
                        Replace(previous[i].Start, previous[i].End, string.Empty);
                    }
                    else if (previous[i].Value != values[i])
                    {
                        Replace(previous[i].ValueStart, previous[i].End, (quoted ? Quote(values[i]) : values[i]) + ";");
                    }
                }

                if (values.Count > previous.Count)
                {
                    int insert = previous.Count > 0 ? previous[^1].End : syntax.BodyStart;
                    Replace(insert, insert, string.Concat(values.Skip(previous.Count).Select(value => Newline + "  " + key + "=" + (quoted ? Quote(value) : value) + ";")) + Newline);
                }

                return;
            }

            // 同値の繰り返し属性は元位置を維持し、未変更の引用・空白を再生成しません。
            var remaining = values.ToList();
            var changed = new List<DefinitionAttribute>();
            foreach (var attribute in previous)
            {
                int same = remaining.FindIndex(value => normalize(value) == normalize(attribute.Value));
                if (same >= 0)
                {
                    remaining.RemoveAt(same);
                }
                else
                {
                    changed.Add(attribute);
                }
            }

            foreach (var attribute in changed)
            {
                if (remaining.Count > 0)
                {
                    Replace(attribute.ValueStart, attribute.End, (quoted ? Quote(remaining[0]) : remaining[0]) + ";");
                    remaining.RemoveAt(0);
                }
                else
                {
                    Replace(attribute.Start, attribute.End, string.Empty);
                }
            }

            if (remaining.Count > 0)
            {
                Replace(syntax.BodyStart, syntax.BodyStart, string.Concat(remaining.Select(value => Newline + "  " + key + "=" + (quoted ? Quote(value) : value) + ";")) + Newline);
            }
        }

        public SourceEvidence Render()
        {
            if (replacements.Count == 0)
            {
                return source;
            }

            var text = new StringBuilder(Text);
            int boundary = Text.Length;
            foreach (var edit in replacements.OrderByDescending(r => r.Start).ThenByDescending(r => r.End))
            {
                if (edit.End > boundary)
                {
                    throw new InvalidDataException("原文の編集範囲が重複しています。");
                }

                text.Remove(edit.Start, edit.End - edit.Start).Insert(edit.Start, edit.Text);
                boundary = edit.Start;
            }

            return new SourceEvidence { Mapping = source.Mapping, ETag = source.ETag, Base64 = Convert.ToBase64String(EncodingFor(source).GetBytes(text.ToString())) };
        }
    }
}
