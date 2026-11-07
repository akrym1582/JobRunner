using JobRunner.Controllers.Ui;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Services;

namespace JobRunner.Editing;

/// <summary>原文と固定識別情報を維持して移動候補を作ります。</summary>
public static partial class DefinitionEditor
{
    /// <summary>移動と改名を適用したルートの元パスです。</summary>
    /// <param name="bundle">原文基点です。</param>
    /// <param name="draft">現在の編集です。</param>
    /// <returns>現在の完全名と元の完全名です。</returns>
    public static Dictionary<string, string> RootOrigins(DefinitionBundle bundle, EditorDraft draft)
    {
        var origins = draft.RootOrigins ?? DefinitionPublication.Roots(bundle).Values.ToDictionary(u => u.Path, u => u.Path, StringComparer.Ordinal);
        var groups = (draft.GroupChanges ?? []).ToDictionary(c => c.Id, StringComparer.Ordinal);
        var paths = EditorPaths(draft);
        return origins.Select(p => (Path: paths.GetValueOrDefault(p.Key) ?? MapGroupPath(p.Key, groups), Origin: p.Value))
            .Where(p => p.Path is not null).ToDictionary(p => p.Path!, p => p.Origin, StringComparer.Ordinal);
    }

    /// <summary>運用状態を変更せず、移動候補と確認事項を返します。</summary>
    /// <param name="bundle">原文基点です。</param>
    /// <param name="request">移動対象と配置先です。</param>
    /// <param name="definitions">意味検証器です。</param>
    /// <param name="options">表示設定です。</param>
    /// <param name="token">要求の取消です。</param>
    /// <returns>undo可能な候補です。</returns>
    public static EditorDocument Move(DefinitionBundle bundle, EditorMoveRequest request, DefinitionService definitions, RunnerOptions options, CancellationToken token = default)
    {
        var current = definitions.Validate(Render(bundle, request.Draft), true);
        var units = current.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        int destinationIndex = request.FileName is null ? request.SourceIndex : current.Sources.FindIndex(s => s.Mapping.BlobPath == request.FileName && s.Mapping.TargetPath == request.FileTargetPath && s.Mapping.Encoding == request.FileEncoding);
        var documents = current.Sources.Select(s => new Document(s)).ToList();
        var locations = documents.SelectMany((d, i) => d.Syntax.Keys.Select(p => (Path: p, Index: i))).ToDictionary(p => p.Path, p => p.Index, StringComparer.Ordinal);
        if (!locations.TryGetValue(request.Path, out int originIndex) || !units.TryGetValue(request.Path, out var moving)
            || (moving.Type != "g" && moving.Type != "n") || (units.TryGetValue(ScheduleCalculator.Parent(request.Path), out var oldParent) && oldParent.IsNetwork()))
        {
            throw new ArgumentException("原文のあるグループまたは通常ルートネットを選択してください。");
        }

        if (destinationIndex < 0 || destinationIndex >= documents.Count || Within(request.ParentPath, request.Path)
            || (request.ParentPath != "/" && (!units.TryGetValue(request.ParentPath, out var parent) || parent.Type != "g")))
        {
            throw new ArgumentException("移動先グループ・ファイルが不正、または自己/子孫への移動です。");
        }

        if (originIndex != destinationIndex && documents[originIndex].Syntax.Keys.All(p => Within(p, request.Path)))
        {
            throw new ArgumentException("元ファイルが空になります。先に代替ユニットを作成してください。");
        }

        var targetSource = current.Sources[destinationIndex];
        if (targetSource.Mapping.TargetPath != request.ParentPath && (!locations.TryGetValue(request.ParentPath, out int parentIndex) || parentIndex != destinationIndex))
        {
            throw new ArgumentException("移動先グループの原文があるファイルを選択してください。");
        }

        string target = request.ParentPath.TrimEnd('/') + "/" + moving.Name;
        string Map(string path) => Within(path, request.Path) ? target + path[request.Path.Length..] : path;
        if ((target == request.Path && originIndex == destinationIndex) || units.Keys.Any(p => Within(p, request.Path) && (Map(p).Length > 1024 || (units.ContainsKey(Map(p)) && !Within(Map(p), request.Path)))))
        {
            throw new ArgumentException("移動先が現在と同じ、名前が衝突、または完全名が長すぎます。");
        }

        var warnings = new List<string> { $"{request.Path} → {target}。省略属性は移動先の親から継承します。コマンド等の文字列は変更しません。" };
        warnings.Add("移動元の継承: " + InheritanceSignature(ScheduleCalculator.Parent(request.Path), units));
        warnings.Add("移動先の継承: " + InheritanceSignature(request.ParentPath, units));
        foreach (var ancestor in units.Values.Where(u => u.Type == "g" && (Within(request.Path, u.Path) || Within(target, u.Path))))
        {
            warnings.Add($"継承候補 {ancestor.Path}: " + string.Join("; ", ancestor.Attributes.Where(a => a.Key is "op" or "cl" or "stt" or "sdd" or "md" or "ex").Select(a => a.Key + "=" + string.Join(",", a.Value))));
        }

        foreach (var document in documents)
        {
            foreach (var item in document.Syntax)
            {
                var unit = units[item.Key];
                string reference = unit.Get("jc");
                if (reference.Length > 0 && Map(reference) != reference)
                {
                    document.SetValues(item.Value, "jc", [Map(reference)], true);
                    warnings.Add($"{item.Key}: jc {reference} → {Map(reference)}");
                }
            }
        }

        string oldParentPath = ScheduleCalculator.Parent(request.Path);
        if (oldParentPath != request.ParentPath && locations.TryGetValue(oldParentPath, out int oldIndex))
        {
            documents[oldIndex].SetValues(documents[oldIndex].Syntax[oldParentPath], "el", (units[oldParentPath].Attributes.GetValueOrDefault("el") ?? []).Where(v => ElementName(v) != moving.Name).ToList(), false, ordered: true);
        }

        var display = units.GetValueOrDefault(oldParentPath)?.Attributes.GetValueOrDefault("el")?.Where(v => ElementName(v) == moving.Name).ToList() ?? [];
        if (oldParentPath != request.ParentPath && display.Count > 0 && locations.TryGetValue(request.ParentPath, out int nextIndex))
        {
            documents[nextIndex].SetValues(documents[nextIndex].Syntax[request.ParentPath], "el", (units[request.ParentPath].Attributes.GetValueOrDefault("el") ?? []).Concat(display).ToList(), false, ordered: true);
        }

        // 参照の局所編集を先に確定し、移動ブロックとの編集範囲重複を避けます。
        var sources = documents.Select(d => d.Render()).ToList();
        documents = sources.Select(s => new Document(s)).ToList();
        var origin = documents[originIndex];
        var syntax = origin.Syntax[request.Path];
        var extracted = new Document(sources[originIndex]);
        extracted.Replace(0, syntax.Start, string.Empty);
        extracted.Replace(syntax.End, extracted.Text.Length, string.Empty);
        byte[] block = Convert.FromBase64String(extracted.Render().Base64);
        var destination = documents[destinationIndex];
        int insertion = request.ParentPath == targetSource.Mapping.TargetPath ? destination.Text.Length : destination.Syntax[request.ParentPath].End - 1;
        if (EncodingFor(sources[originIndex]).CodePage != EncodingFor(targetSource).CodePage)
        {
            block = EncodingFor(targetSource).GetBytes(EncodingFor(sources[originIndex]).GetString(block));
            warnings.Add("移動ブロックだけを移動先の文字コードへ変換します。");
        }

        origin.Replace(syntax.Start, syntax.End, string.Empty);
        byte[] newline = EncodingFor(targetSource).GetBytes(destination.Newline);
        destination.ReplaceRaw(insertion, insertion, newline.Concat(block).Concat(newline).ToArray());
        var output = new List<SourceEvidence>();
        for (int i = 0; i < documents.Count; i++)
        {
            var source = documents[i].Render();
            if (Within(source.Mapping.TargetPath, request.Path))
            {
                source = new SourceEvidence { Base64 = source.Base64, Mapping = new DefinitionSource { Kind = source.Mapping.Kind, BlobPath = source.Mapping.BlobPath, Encoding = source.Mapping.Encoding, TargetPath = Map(source.Mapping.TargetPath) } };
            }

            if (Jp1DefinitionParser.Parse(Decode(source).TrimStart('\uFEFF'), source.Mapping).Count == 0)
            {
                throw new ArgumentException("元ファイルが空になります。先に代替ユニットを作成してください。");
            }

            output.Add(source);
        }

        var result = definitions.Validate(EditorLocalFiles.ValidateSources(output), true);
        var afterUnits = result.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        var comparisonStart = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone)).DateTime);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var affectedRoots = DefinitionPublication.Roots(current).Values.Where(u => Within(u.Path, request.Path)).ToList();
        foreach (var root in affectedRoots.Take(50))
        {
            token.ThrowIfCancellationRequested();
            if (timer.Elapsed > TimeSpan.FromSeconds(2))
            {
                warnings.Add("カレンダー比較は2秒で打ち切りました。残りは下書き予定試算で確認してください。");
                break;
            }

            var differences = new List<string>();
            try
            {
                for (int offset = 0; offset < 31 && comparisonStart.Year is >= 1994 and <= 2036; offset++)
                {
                    token.ThrowIfCancellationRequested();
                    if (timer.Elapsed > TimeSpan.FromSeconds(2))
                    {
                        throw new FormatException("カレンダー比較を2秒で中断しました。下書き予定試算で確認してください。");
                    }

                    var day = comparisonStart.AddDays(offset);
                    if (day.Year > 2036)
                    {
                        break;
                    }

                    var before = ScheduleCalculator.EvaluateCalendar(day, root, units);
                    var after = ScheduleCalculator.EvaluateCalendar(day, afterUnits[Map(root.Path)], afterUnits);
                    if (before.Working != after.Working)
                    {
                        differences.Add($"{day:yyyy-MM-dd}: {(before.Working ? "稼働" : "休業")} → {(after.Working ? "稼働" : "休業")}");
                    }
                }

                warnings.Add($"{root.Path}: {comparisonStart:yyyy-MM-dd}から31日間の実効カレンダー差: {(differences.Count == 0 ? "なし" : string.Join(", ", differences))}。予定全体は下書き予定試算で確認してください。");
            }
            catch (Exception ex) when (ex is FormatException or KeyNotFoundException or ArgumentException)
            {
                warnings.Add($"{root.Path}: カレンダー比較不可: {ex.Message}");
            }
        }

        if (affectedRoots.Count > 50)
        {
            warnings.Add("カレンダー比較は先頭50ルートまでです。残りは下書き予定試算で確認してください。");
        }

        var groupOrigins = GroupOrigins(bundle, request.Draft).ToDictionary(p => Map(p.Key), p => p.Value, StringComparer.Ordinal);
        var rootOrigins = RootOrigins(bundle, request.Draft).ToDictionary(p => Map(p.Key), p => p.Value, StringComparer.Ordinal);
        var draft = Project(result, 9) with { DefinitionHash = request.Draft.DefinitionHash, LocalSources = request.Draft.LocalSources, WorkingSources = result.Sources, WorkingContainers = Containers(result), GroupOrigins = groupOrigins, RootOrigins = rootOrigins, OriginCatalogHash = request.Draft.OriginCatalogHash };
        return new(draft, Describe(result.Sources, bundle.Sources), Containers(result), options.TimeZone, options.ScheduleTimeMode, warnings);
    }
}
