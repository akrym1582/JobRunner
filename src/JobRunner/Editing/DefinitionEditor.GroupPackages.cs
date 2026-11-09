using System.IO.Compression;
using System.Text.Json;
using JobRunner.Controllers.Ui;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Services;

namespace JobRunner.Editing;

/// <summary>グループ原文を運用状態から分離して持ち運びます。</summary>
public static partial class DefinitionEditor
{
    /// <summary>外部依存を持たないグループを原文のままZIPへ出力します。</summary>
    /// <param name="bundle">原文基点です。</param>
    /// <param name="request">下書きと選択グループです。</param>
    /// <param name="definitions">定義の意味検証器です。</param>
    /// <returns>運用状態を含まないZIPです。</returns>
    public static byte[] ExportGroup(DefinitionBundle bundle, EditorGroupPackageRequest request, DefinitionService definitions)
    {
        var current = definitions.Validate(Render(bundle, request.Draft), true);
        var all = current.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        if (!all.TryGetValue(request.Path, out var selected) || selected.Type != "g" || request.Path == "/")
        {
            throw new ArgumentException("ルート以外のグループを選択してください。");
        }

        var dependencies = GroupDependencies(all, request.Path);
        var sources = ExtractGroup(current, request.Path);
        definitions.Validate(EditorLocalFiles.ValidateSources(sources), true);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var manifest = new List<object>();
            for (int i = 0; i < sources.Count; i++)
            {
                var source = sources[i];
                string file = $"sources/{i + 1:D3}/unit.ajs";
                using var entry = archive.CreateEntry(file).Open();
                entry.Write(Convert.FromBase64String(source.Base64));
                manifest.Add(new { File = file, source.Mapping });
            }

            using var metadata = archive.CreateEntry("manifest.json").Open();

            // 読込側と同じ非圧縮上限で判定します。原文内の参照は省略しません。
            var included = new List<string>();
            int diagnosticBytes = 0;
            foreach (string dependency in dependencies)
            {
                int length = JsonSerializer.SerializeToUtf8Bytes(dependency).Length;
                if (diagnosticBytes + length > 128 * 1024)
                {
                    break;
                }

                diagnosticBytes += length;
                included.Add(dependency);
            }

            int omitted = dependencies.Count - included.Count;
            if (omitted > 0)
            {
                included.Add($"依存診断 {omitted} 件を省略しました。参照は原文に保持し、取込時に再検証します。");
            }

            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new { FormatVersion = 2, GroupRoot = request.Path, Sources = manifest, Dependencies = included });
            if (manifestBytes.Length > 256 * 1024)
            {
                throw new ArgumentException("グループZIPのmanifestは256KiBまでです。原文のファイル対応を分割してください。");
            }

            metadata.Write(manifestBytes);
        }

        if (output.Length > EditorLocalFiles.TotalLimit)
        {
            throw new ArgumentException("グループZIPは8MiBまでです。");
        }

        return output.ToArray();
    }

    /// <summary>検証したグループを一つのundo可能な下書き変更へまとめます。</summary>
    /// <param name="bundle">原文基点です。</param>
    /// <param name="request">ZIPまたはテキストと追加先または置換先です。</param>
    /// <param name="definitions">定義の意味検証器です。</param>
    /// <param name="options">表示用の実行設定です。</param>
    /// <returns>保存も運用反映もしていない候補です。</returns>
    public static EditorDocument ImportGroup(DefinitionBundle bundle, EditorGroupPackageRequest request, DefinitionService definitions, RunnerOptions options)
    {
        var (imported, sourceRoot, sourceDiagnostics) = ReadGroupInput(request, definitions);
        var importedUnits = imported.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        if (sourceRoot == "/" || !importedUnits.TryGetValue(sourceRoot, out var selected) || selected.Type != "g")
        {
            throw new ArgumentException("ZIPのグループ基点が不正です。");
        }

        // 仮想親以外の選択外ユニットを含むZIPを、置換操作へ紛れ込ませません。
        foreach (var source in imported.Sources)
        {
            if (new Document(source).Syntax.Keys.Any(p => !Within(p, sourceRoot)))
            {
                throw new ArgumentException("ZIPに選択グループ外の定義が含まれています。");
            }
        }

        var current = definitions.Validate(Render(bundle, request.Draft), true);
        var currentUnits = current.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        bool replace = request.ParentPath is null;
        string target;
        if (replace)
        {
            target = request.Path;
            if (target == "/" || !currentUnits.TryGetValue(target, out var group) || group.Type != "g")
            {
                throw new ArgumentException("置換するグループを選択してください。");
            }
        }
        else
        {
            string parent = request.ParentPath!;
            if (!ValidName(request.Name!) || (parent != "/" && (!currentUnits.TryGetValue(parent, out var group) || group.Type != "g")))
            {
                throw new ArgumentException("追加先グループと有効な名前を指定してください。");
            }

            target = parent.TrimEnd('/') + "/" + request.Name;
            if (currentUnits.ContainsKey(target))
            {
                throw new ArgumentException("同名ユニットがあります。別名または置換を選択してください。");
            }
        }

        var sources = new List<SourceEvidence>();
        foreach (var source in current.Sources)
        {
            if (replace && Within(source.Mapping.TargetPath, target))
            {
                continue;
            }

            var document = new Document(source);
            if (replace && document.Syntax.TryGetValue(target, out var syntax))
            {
                document.Replace(syntax.Start, syntax.End, string.Empty);
            }

            var remaining = document.Render();
            if (Convert.FromBase64String(remaining.Base64).Length > 0)
            {
                sources.Add(remaining);
            }
        }

        string Map(string path) => Within(path, sourceRoot) ? target + path[sourceRoot.Length..] : ScheduleCalculator.Parent(target);
        string prefix = "group-import/" + Guid.NewGuid().ToString("N") + "/";
        foreach (var source in imported.Sources)
        {
            var document = new Document(source);
            if (document.Syntax.TryGetValue(sourceRoot, out var syntax))
            {
                string header = document.Text[syntax.HeaderStart..(syntax.HeaderEnd - 1)];
                int comma = HeaderSeparator(header);
                document.Replace(syntax.HeaderStart, syntax.HeaderEnd, Quote(target.Split('/').Last()) + (comma < 0 ? string.Empty : header[comma..]) + ";");
            }

            foreach (var item in document.Syntax)
            {
                string reference = importedUnits[item.Key].Get("jc");
                if (reference.Length > 0 && Within(reference, sourceRoot))
                {
                    document.SetValues(item.Value, "jc", [Map(reference)], true);
                }
            }

            var rendered = document.Render();
            rendered.Mapping = new DefinitionSource { Kind = "Unit", BlobPath = prefix + source.Mapping.BlobPath, Encoding = source.Mapping.Encoding, TargetPath = Map(source.Mapping.TargetPath) };
            sources.Add(rendered);
        }

        var result = definitions.Validate(EditorLocalFiles.ValidateSources(sources), true);
        var origins = GroupOrigins(bundle, request.Draft);
        var paths = result.Units.Flatten().Where(u => u.Type == "g").Select(u => u.Path).ToHashSet(StringComparer.Ordinal);
        origins = origins.Where(p => paths.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var projected = Project(result, 9) with { DefinitionHash = request.Draft.DefinitionHash, LocalSources = request.Draft.LocalSources, WorkingSources = result.Sources, WorkingContainers = Containers(result), GroupOrigins = origins, RootOrigins = RootOrigins(bundle, request.Draft), OriginCatalogHash = request.Draft.OriginCatalogHash };
        return new(projected, Describe(result.Sources, bundle.Sources), Containers(result), options.TimeZone, options.ScheduleTimeMode, GroupDependencies(result.Units.Flatten().ToDictionary(u => u.Path), target).Concat(sourceDiagnostics).ToList());
    }

    private static (DefinitionBundle Bundle, string Root, List<string> Diagnostics) ReadGroupInput(EditorGroupPackageRequest request, DefinitionService definitions)
    {
        if ((request.Source is null) == (request.ZipBase64 is null))
        {
            throw new ArgumentException("JP1定義テキストまたはグループZIPのどちらかを指定してください。");
        }

        if (request.Source is { } source)
        {
            if (source.Mapping?.TargetPath != "/")
            {
                throw new ArgumentException("グループ取込のテキストは配置基点 / で指定してください。");
            }

            var bundle = EditorLocalFiles.Read(new([source]), definitions, true);
            var roots = bundle.Units.Where(u => u.Path != "/").ToList();
            if (roots.Count == 1 && roots[0].Type == "g")
            {
                return (bundle, roots[0].Path, []);
            }

            // ネットだけのJP1原文も、バイトを書き換えず配置情報でグループ配下へまとめます。
            const string root = "/__jobrunner_text__";
            var nested = bundle.Sources[0];
            nested.Mapping = new() { Kind = "Unit", BlobPath = "text/unit.ajs", TargetPath = root, Encoding = source.Mapping.Encoding };
            var wrapper = new SourceEvidence
            {
                Mapping = new() { Kind = "Unit", BlobPath = "text/group.ajs", TargetPath = "/", Encoding = "utf-8" },
                Base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("unit=__jobrunner_text__;{ty=g;}")),
            };
            return (definitions.Validate([wrapper, nested], true), root, []);
        }

        var imported = EditorLocalFiles.Read(new(ZipBase64: request.ZipBase64), definitions, true);
        using var input = new MemoryStream(Convert.FromBase64String(request.ZipBase64!));
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        using var json = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        if (!json.RootElement.TryGetProperty("GroupRoot", out var rootValue) || rootValue.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("グループ出力ZIPを選択してください。全定義ZIPはローカル入力から読み込めます。");
        }

        var diagnostics = json.RootElement.TryGetProperty("Dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Array
            ? dependencies.EnumerateArray().Select(d => d.ValueKind == JsonValueKind.String ? "出力元情報: " + d.GetString() : throw new ArgumentException("ZIPの依存診断は文字列で指定してください。")).ToList() : [];
        return (imported, rootValue.GetString()!, diagnostics);
    }

    private static List<string> GroupDependencies(Dictionary<string, UnitDefinition> all, string path)
    {
        var diagnostics = new List<string>();
        foreach (var reference in all.Values.Where(u => Within(u.Path, path) && u.Get("jc").Length > 0 && !Within(u.Get("jc"), path)).GroupBy(u => u.Get("jc"), StringComparer.Ordinal))
        {
            string state = all.TryGetValue(reference.Key, out var dependency) && dependency.Type == "g" ? "参照先あり" : "参照先不足・運用反映不可";
            diagnostics.Add($"外部カレンダー {reference.Key}（{state}）。参照元 {reference.Count()} 件、例: {string.Join(", ", reference.Take(3).Select(u => u.Path))}。依存先は同梱しません。");
        }

        string parent = ScheduleCalculator.Parent(path);
        while (all.TryGetValue(parent, out var ancestor))
        {
            var keys = ancestor.Attributes.Keys.Where(k => k is "op" or "cl" or "md" or "sdd" or "stt" or "ex").ToList();
            if (keys.Count > 0)
            {
                diagnostics.Add($"{path}: 祖先 {parent} の継承属性 {string.Join(", ", keys.Select(k => k + "=" + string.Join("/", ancestor.Attributes[k])))}。取り込み後は配置先の親に従います。");
            }

            if (ancestor.OwnerName is not null || ancestor.PermissionMode is not null || ancestor.ResourceGroup is not null)
            {
                diagnostics.Add($"{path}: 祖先 {parent} の所有者={ancestor.OwnerName}, 許可={ancestor.PermissionMode}, 資源グループ={ancestor.ResourceGroup}。取り込み後は配置先の親に従います。");
            }

            if (parent == "/")
            {
                break;
            }

            parent = ScheduleCalculator.Parent(parent);
        }

        return diagnostics;
    }

    private static List<SourceEvidence> ExtractGroup(DefinitionBundle bundle, string path)
    {
        var result = new List<SourceEvidence>();
        foreach (var source in bundle.Sources)
        {
            var document = new Document(source);
            if (Within(source.Mapping.TargetPath, path))
            {
                result.Add(source);
            }
            else if (document.Syntax.TryGetValue(path, out var syntax))
            {
                int bom = document.Text.StartsWith('\uFEFF') ? 1 : 0;
                document.Replace(bom, syntax.Start, string.Empty);
                document.Replace(syntax.End, document.Text.Length, string.Empty);
                var extracted = document.Render();
                extracted.Mapping = new DefinitionSource { Kind = source.Mapping.Kind, BlobPath = source.Mapping.BlobPath, TargetPath = ScheduleCalculator.Parent(path), Encoding = source.Mapping.Encoding };
                result.Add(extracted);
            }
        }

        if (result.Count == 0)
        {
            throw new ArgumentException("選択グループの原文がありません。");
        }

        return result;
    }
}
