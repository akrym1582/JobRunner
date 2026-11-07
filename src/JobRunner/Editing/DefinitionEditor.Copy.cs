using System.Globalization;
using JobRunner.Controllers.Ui;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Services;

namespace JobRunner.Editing;

/// <summary>コピー時点の原文を複製し、編集履歴の中で新しい基点を保持します。</summary>
public static partial class DefinitionEditor
{
    /// <summary>同じ下書きの原文・未知属性・内部コメントを保って貼り付け候補を作ります。</summary>
    /// <param name="bundle">共通の原文基点です。</param>
    /// <param name="request">コピー元と貼り付け先です。</param>
    /// <param name="definitions">参照と実行モデルの検証器です。</param>
    /// <param name="options">表示用の実行設定です。</param>
    /// <returns>運用データを変更しない候補です。</returns>
    public static EditorPasteResult Paste(DefinitionBundle bundle, EditorPasteRequest request, DefinitionService definitions, RunnerOptions options)
    {
        if (request.Draft.EditorVersion != 9 || request.CopiedDraft.EditorVersion != 9 || request.Draft.DefinitionHash != request.CopiedDraft.DefinitionHash || request.Ids.Count == 0 || request.Ids.Distinct(StringComparer.Ordinal).Count() != request.Ids.Count)
        {
            throw new ArgumentException("同一下書き版9の重複しない選択を指定してください。");
        }

        var copied = definitions.Validate(Render(bundle, request.CopiedDraft));
        var current = definitions.Validate(Render(bundle, request.Draft));
        var sourcePaths = EditorPaths(request.CopiedDraft);
        var targetPaths = EditorPaths(request.Draft);
        var units = copied.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        var selected = request.Ids.Select(id => sourcePaths.TryGetValue(id, out string? path) && units.TryGetValue(path, out var unit) ? unit : throw new ArgumentException("コピー元がありません。")).ToList();
        string sourceParent = ScheduleCalculator.Parent(selected[0].Path);
        if (selected.Any(u => ScheduleCalculator.Parent(u.Path) != sourceParent || u.Type is not ("n" or "rn" or "j" or "rj" or "pj" or "rp" or "jdj" or "rjdj")))
        {
            throw new ArgumentException("同じ親の編集可能なジョブ・ネットを選択してください。");
        }

        bool roots = !units.TryGetValue(sourceParent, out var parent) || !parent.IsNetwork();
        if (roots != (request.TargetNetworkId is null) || (roots && selected.Any(u => !u.IsNetwork())))
        {
            throw new ArgumentException("ルートと子ネットの相互変換はできません。同じ階層の役割へ貼り付けてください。");
        }

        var targetUnits = current.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        var documents = current.Sources.Select(s => new Document(s)).ToList();
        string targetPath;
        int sourceIndex;
        if (roots)
        {
            sourceIndex = request.TargetFile is null ? request.SourceIndex ?? -1 : current.Sources.FindIndex(s => s.Mapping.BlobPath == request.TargetFile);
            targetPath = request.ParentPath ?? string.Empty;
            if (!Containers(current).Contains(new EditorContainer(sourceIndex, targetPath)))
            {
                throw new ArgumentException("ルートの配置先を確認してください。");
            }
        }
        else
        {
            targetPath = targetPaths.GetValueOrDefault(request.TargetNetworkId!) ?? throw new ArgumentException("貼り付け先ネットがありません。");
            if (!targetUnits.TryGetValue(targetPath, out var target) || !target.IsNetwork())
            {
                throw new ArgumentException("貼り付け先はネットにしてください。");
            }

            sourceIndex = documents.FindIndex(d => d.Syntax.ContainsKey(targetPath));
        }

        var names = targetUnits.Values.Where(u => ScheduleCalculator.Parent(u.Path) == targetPath).Select(u => u.Name).ToHashSet(StringComparer.Ordinal);
        var mappedNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var unit in selected)
        {
            string name = unit.Name + "_copy";
            for (int suffix = 2; names.Contains(name); suffix++)
            {
                name = unit.Name + "_copy" + suffix.ToString(CultureInfo.InvariantCulture);
            }

            names.Add(name);
            mappedNames.Add(unit.Path, name);
        }

        var warnings = new List<string>();
        if (sourceParent != targetPath)
        {
            warnings.Add($"継承元が {sourceParent} から {targetPath} に変わります。省略属性は貼り付け先から継承します。");
        }

        if (roots)
        {
            warnings.Add("複製ルートの開始日は予定なし、保留はありに変更します。");
        }

        var sourceDocuments = copied.Sources.Select(s => new Document(s)).ToList();
        var blocks = new List<string>();
        foreach (var unit in selected)
        {
            var document = sourceDocuments.Single(d => d.Syntax.ContainsKey(unit.Path));
            var syntax = document.Syntax[unit.Path];
            var blockSource = new SourceEvidence { Mapping = new DefinitionSource { BlobPath = "clipboard/unit.ajs", Encoding = "utf-8", TargetPath = sourceParent }, Base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(document.Text[syntax.Start..syntax.End])) };
            var block = new Document(blockSource);
            var blockSyntax = block.Syntax[unit.Path];
            string header = block.Text[blockSyntax.HeaderStart..(blockSyntax.HeaderEnd - 1)];
            int comma = HeaderSeparator(header);
            block.Replace(blockSyntax.HeaderStart, blockSyntax.HeaderEnd, Quote(mappedNames[unit.Path]) + (comma < 0 ? string.Empty : header[comma..]) + ";");
            if (roots)
            {
                block.SetValues(blockSyntax, "sd", ["0,ud"], false, ordered: true);
                block.SetValues(blockSyntax, "ha", ["y"], false);
            }

            blocks.Add(Decode(block.Render()));
            foreach (var reference in new[] { unit }.Flatten().Select(u => u.Get("jc")).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal))
            {
                warnings.Add($"参照カレンダー {reference} は参照先を維持します。");
            }
        }

        var targetDocument = documents[sourceIndex];
        int insertion = targetDocument.Syntax.TryGetValue(targetPath, out var targetSyntax) ? targetSyntax.End - 1 : targetDocument.Text.Length;
        targetDocument.Replace(insertion, insertion, targetDocument.Newline + string.Join(targetDocument.Newline, blocks) + targetDocument.Newline);
        if (!roots)
        {
            var edges = DefinitionValidator.Edges(parent!).ToList();
            var omitted = edges.Where(e => mappedNames.ContainsKey(e.From) != mappedNames.ContainsKey(e.To)).ToList();
            warnings.AddRange(omitted.Select(e => $"外部接続を除外: {e.From} → {e.To}"));
            var target = targetUnits[targetPath];
            var addedEdges = edges.Where(e => mappedNames.ContainsKey(e.From) && mappedNames.ContainsKey(e.To)).Select(e => $"(f={mappedNames[e.From]},t={mappedNames[e.To]},{(e.Conditional ? "con" : "seq")})");
            targetDocument.SetValues(targetSyntax!, "ar", (target.Attributes.GetValueOrDefault("ar") ?? []).Concat(addedEdges).ToList(), false, ordered: true);
            var sourceLayout = DefinitionLayout.Read(parent!);
            var targetLayout = DefinitionLayout.Read(target);
            var positions = selected.Select(u => sourceLayout[u.Path]).ToList();
            int minX = positions.Min(p => p.X);
            int minY = positions.Min(p => p.Y);
            (int X, int Y)? origin = null;
            for (int y = 48; y <= 10000 && origin is null; y += 96)
            {
                for (int x = 80; x <= 16000; x += 160)
                {
                    if (positions.All(p => x + p.X - minX <= 16000 && y + p.Y - minY <= 10000 && targetLayout.Values.All(q => Math.Abs(q.X - (x + p.X - minX)) >= 160 || Math.Abs(q.Y - (y + p.Y - minY)) >= 96)))
                    {
                        origin = (x, y);
                        break;
                    }
                }
            }

            if (origin is null)
            {
                throw new ArgumentException("複製する配置を保てる空き領域がありません。");
            }

            var elements = selected.Select((u, i) => $"{mappedNames[u.Path]},{u.Type},+{origin.Value.X + positions[i].X - minX} +{origin.Value.Y + positions[i].Y - minY}");
            targetDocument.SetValues(targetSyntax!, "el", (target.Attributes.GetValueOrDefault("el") ?? []).Concat(elements).ToList(), false, ordered: true);
            ExpandLayout(targetDocument, targetSyntax!, target, origin.Value.X + positions.Max(p => p.X) - minX, origin.Value.Y + positions.Max(p => p.Y) - minY);
        }

        var sources = documents.Select(d => d.Render()).ToList();
        var result = definitions.Validate(EditorLocalFiles.ValidateSources(sources));
        var projected = Project(result, 9) with { DefinitionHash = request.Draft.DefinitionHash, LocalSources = request.Draft.LocalSources, WorkingSources = result.Sources, WorkingContainers = Containers(result) };
        var cloned = selected.Select(u => targetPath.TrimEnd('/') + "/" + mappedNames[u.Path]).ToList();
        return new(new EditorDocument(projected, Describe(result.Sources, bundle.Sources), Containers(result), options.TimeZone, options.ScheduleTimeMode), cloned, roots ? cloned[0] : targetPath, warnings.Distinct(StringComparer.Ordinal).ToList());
    }
}
