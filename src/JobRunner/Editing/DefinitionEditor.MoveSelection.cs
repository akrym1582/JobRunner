using JobRunner.Controllers.Ui;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Services;

namespace JobRunner.Editing;

/// <summary>接続と未編集バイトを保持し、同じ親の選択をまとめて移動します。</summary>
public static partial class DefinitionEditor
{
    /// <summary>運用状態を変更せず、一度のundoで戻せる移動候補を作ります。</summary>
    /// <param name="bundle">原文基点です。</param>
    /// <param name="request">選択と移動先です。</param>
    /// <param name="definitions">定義検証器です。</param>
    /// <param name="options">表示設定です。</param>
    /// <param name="token">計算の取消です。</param>
    /// <returns>接続の切断内容を含む候補です。</returns>
    public static EditorDocument MoveSelection(DefinitionBundle bundle, EditorMoveSelectionRequest request, DefinitionService definitions, RunnerOptions options, CancellationToken token = default)
    {
        if (request.Draft.EditorVersion != 9 || request.Ids is not { Count: > 0 } || request.Ids.Distinct(StringComparer.Ordinal).Count() != request.Ids.Count)
        {
            throw new ArgumentException("版9の下書きと、重複しないジョブ・子ネットの選択を指定してください。");
        }

        token.ThrowIfCancellationRequested();
        var current = definitions.Validate(Render(bundle, request.Draft), true);
        var units = current.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        var paths = EditorPaths(request.Draft);
        var selected = request.Ids.Select(id => paths.TryGetValue(id, out string? path) && units.TryGetValue(path, out var unit) ? unit : throw new ArgumentException("選択したユニットがありません。現在の下書きで選び直してください。")).ToList();
        string oldParent = ScheduleCalculator.Parent(selected[0].Path);
        if (!units.TryGetValue(oldParent, out var parent) || !parent.IsNetwork()
            || selected.Any(u => ScheduleCalculator.Parent(u.Path) != oldParent || u.Type is not ("n" or "rn" or "j" or "rj" or "pj" or "rp" or "jdj" or "rjdj")))
        {
            throw new ArgumentException("同じ親ネット直下の編集可能なジョブ・子ネットを選択してください。ルートとの相互変換はできません。");
        }

        if (!paths.TryGetValue(request.TargetNetworkId, out string? targetPath) || !units.TryGetValue(targetPath, out var target) || !target.IsNetwork()
            || targetPath == oldParent || selected.Any(u => Within(targetPath, u.Path)))
        {
            throw new ArgumentException("移動先は別ネットにしてください。自己・選択した子孫への移動はできません。");
        }

        var names = selected.Select(u => u.Name).ToHashSet(StringComparer.Ordinal);
        if (target.Children.Any(u => names.Contains(u.Name)))
        {
            throw new ArgumentException("移動先に同名ユニットがあります。名前を変更してから移動してください。");
        }

        string Map(string path)
        {
            string prefix = oldParent + "/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return path;
            }

            string relative = path[prefix.Length..];
            int slash = relative.IndexOf('/');
            string name = slash < 0 ? relative : relative[..slash];
            return names.Contains(name) ? targetPath + "/" + relative : path;
        }

        if (units.Keys.Any(p => Map(p).Length > 1024))
        {
            throw new ArgumentException("移動後の完全名が長すぎます。");
        }

        var documents = current.Sources.Select(s => new Document(s)).ToList();
        var locations = documents.SelectMany((d, i) => d.Syntax.Keys.Select(p => (Path: p, Index: i))).ToDictionary(p => p.Path, p => p.Index, StringComparer.Ordinal);
        if (!locations.TryGetValue(oldParent, out int parentIndex) || !locations.TryGetValue(targetPath, out int targetIndex) || selected.Any(u => !locations.ContainsKey(u.Path)))
        {
            throw new ArgumentException("親ネットと移動対象の原文が必要です。");
        }

        var warnings = selected.Select(u => $"移動: {u.Path} → {Map(u.Path)}（種別・明示属性・座標を保持）").ToList();
        warnings.Add("移動元の継承: " + InheritanceSignature(oldParent, units));
        warnings.Add("移動先の継承: " + InheritanceSignature(targetPath, units));
        warnings.Add("省略属性・上位ルールは移動先で評価します。運用反映まで実行データは変更しません。");
        foreach (var ancestor in new[] { parent, target })
        {
            warnings.Add($"親ネット {ancestor.Path} のルール: " + string.Join("; ", ancestor.Attributes.Where(a => a.Key is "sd" or "st" or "cy" or "ln" or "de").Select(a => a.Key + "=" + string.Join(",", a.Value))));
        }

        var selectedPaths = selected.Select(u => u.Path).ToHashSet(StringComparer.Ordinal);
        var edges = DefinitionValidator.Edges(parent).ToList();
        foreach (var edge in edges.Where(e => selectedPaths.Contains(e.From) != selectedPaths.Contains(e.To)))
        {
            warnings.Add($"外部接続を切断: {edge.From} → {edge.To}（{(edge.Conditional ? "条件接続" : "通常接続")}）");
        }

        var rawEdges = edges.Zip(parent.Attributes.GetValueOrDefault("ar") ?? []).ToList();
        var internalEdges = rawEdges.Where(p => selectedPaths.Contains(p.First.From) && selectedPaths.Contains(p.First.To)).Select(p => p.Second).ToList();
        documents[parentIndex].SetValues(documents[parentIndex].Syntax[oldParent], "ar", rawEdges.Where(p => !selectedPaths.Contains(p.First.From) && !selectedPaths.Contains(p.First.To)).Select(p => p.Second).ToList(), false, ordered: true);
        documents[targetIndex].SetValues(documents[targetIndex].Syntax[targetPath], "ar", (target.Attributes.GetValueOrDefault("ar") ?? []).Concat(internalEdges).ToList(), false, ordered: true);
        var display = parent.Attributes.GetValueOrDefault("el") ?? [];
        documents[parentIndex].SetValues(documents[parentIndex].Syntax[oldParent], "el", display.Where(v => !names.Contains(ElementName(v))).ToList(), false, ordered: true);
        documents[targetIndex].SetValues(documents[targetIndex].Syntax[targetPath], "el", (target.Attributes.GetValueOrDefault("el") ?? []).Concat(display.Where(v => names.Contains(ElementName(v)))).ToList(), false, ordered: true);
        var positions = display.GroupBy(ElementName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => Position(g.First()), StringComparer.Ordinal);
        var targetPositions = (target.Attributes.GetValueOrDefault("el") ?? []).Select(Position).Where(p => p is not null).ToList();
        foreach (var unit in selected.Where(u => positions.GetValueOrDefault(u.Name) is { } position && targetPositions.Contains(position)))
        {
            warnings.Add($"座標重複: {unit.Path}。元の座標を保持します。必要なら移動後に配置を調整してください。");
        }

        foreach (var unit in selected.SelectMany(u => new[] { u }.Flatten()))
        {
            string reference = unit.Get("jc");
            if (reference.Length > 0)
            {
                warnings.Add($"{unit.Path}: カレンダー参照 {reference} を保持します。");
            }

            if (unit.Attributes.Keys.Any(k => k is "sd" or "de" or "sh" or "ln"))
            {
                warnings.Add($"{unit.Path}: スケジュール・上位ルール属性を保持: " + string.Join("; ", unit.Attributes.Where(a => a.Key is "sd" or "de" or "sh" or "ln").Select(a => a.Key + "=" + string.Join(",", a.Value))));
            }
        }

        // 属性編集を確定してから原文を切り出し、削除範囲との重複を避けます。
        var sources = documents.Select(d => d.Render()).ToList();
        documents = sources.Select(s => new Document(s)).ToList();
        var destination = documents[targetIndex];
        using var blocks = new MemoryStream();
        byte[] newline = EncodingFor(sources[targetIndex]).GetBytes(destination.Newline);
        foreach (var unit in selected.OrderBy(u => locations[u.Path]).ThenBy(u => documents[locations[u.Path]].Syntax[u.Path].Start))
        {
            token.ThrowIfCancellationRequested();
            int index = locations[unit.Path];
            var origin = documents[index];
            var syntax = origin.Syntax[unit.Path];
            byte[] block = origin.SliceRaw(syntax.Start, syntax.End);
            if (EncodingFor(sources[index]).CodePage != EncodingFor(sources[targetIndex]).CodePage)
            {
                block = EncodingFor(sources[targetIndex]).GetBytes(EncodingFor(sources[index]).GetString(block));
                warnings.Add($"{unit.Path}: 移動部分だけを移動先の文字コードへ変換します。");
            }

            blocks.Write(newline);
            blocks.Write(block);
            blocks.Write(newline);
            origin.Replace(syntax.Start, syntax.End, string.Empty);
        }

        destination.ReplaceRaw(destination.Syntax[targetPath].End - 1, destination.Syntax[targetPath].End - 1, blocks.ToArray());
        var output = documents.Select(d => d.Render()).Select(s => new SourceEvidence { Mapping = new DefinitionSource { Kind = s.Mapping.Kind, BlobPath = s.Mapping.BlobPath, Encoding = s.Mapping.Encoding, TargetPath = Map(s.Mapping.TargetPath) }, Base64 = s.Base64, ETag = s.ETag }).ToList();
        if (output.Any(s => Jp1DefinitionParser.Parse(Decode(s).TrimStart('\uFEFF'), s.Mapping).Count == 0))
        {
            throw new ArgumentException("元ファイルが空になります。先に代替ユニットを作成してください。");
        }

        DefinitionBundle result;
        try
        {
            result = definitions.Validate(EditorLocalFiles.ValidateSources(output), true);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("移動後の定義が不正です。判定の先行など必要な関連ユニットを一緒に選択するか、接続・上位ルールを修正してください。" + ex.Message, ex);
        }

        var draft = Project(result, 9) with { DefinitionHash = request.Draft.DefinitionHash, LocalSources = request.Draft.LocalSources, WorkingSources = result.Sources, WorkingContainers = Containers(result), GroupOrigins = GroupOrigins(bundle, request.Draft), RootOrigins = RootOrigins(bundle, request.Draft), OriginCatalogHash = request.Draft.OriginCatalogHash };
        return new(draft, Describe(result.Sources, bundle.Sources), Containers(result), options.TimeZone, options.ScheduleTimeMode, warnings.Distinct(StringComparer.Ordinal).ToList());
    }
}
