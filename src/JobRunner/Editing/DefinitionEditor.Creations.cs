using System.Text;
using JobRunner.Controllers.Ui;
using JobRunner.Import;
using JobRunner.Models;

namespace JobRunner.Editing;

/// <summary>元原文を固定した追加記述を検証し、既存の編集経路に渡します。</summary>
public static partial class DefinitionEditor
{
    /// <summary>実行可能なユニットを含まないローカル原文を作ります。</summary>
    /// <param name="file">新しいファイルの明示設定です。</param>
    /// <returns>Storageへ保存していない原文です。</returns>
    public static SourceEvidence CreateFile(EditorFileCreation file)
    {
        if (file is null || string.IsNullOrEmpty(file.FileName) || string.IsNullOrEmpty(file.Encoding))
        {
            throw new ArgumentException("ファイル名と文字コードを指定してください。");
        }

        if (!ValidName(file.GroupName) || file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("有効なグループ名と、ZIP以外のUnitファイル名を指定してください。");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int codePage = file.Encoding.ToLowerInvariant() switch
        {
            "utf-8" => 65001,
            "utf-16" or "utf-16le" => 1200,
            "utf-16be" => 1201,
            "shift_jis" or "shift-jis" => 932,
            _ => throw new ArgumentException("文字コードはUTF-8、UTF-16LE/BE、Shift_JISから指定してください。"),
        };
        if (file.Bom && codePage == 932)
        {
            throw new ArgumentException("Shift_JISにはBOMを指定できません。");
        }

        var encoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var source = new SourceEvidence
        {
            Mapping = new() { Kind = "Unit", BlobPath = file.FileName, TargetPath = file.TargetPath, Encoding = file.Encoding },
            Base64 = Convert.ToBase64String(encoding.GetBytes((file.Bom ? "\uFEFF" : string.Empty) + $"unit={file.GroupName};{{ty=g;}}\n")),
        };
        return EditorLocalFiles.ValidateSources([source])[0];
    }

    private static List<SourceEvidence> RenderCreations(DefinitionBundle bundle, EditorDraft draft)
    {
        var groups = draft.NewGroups ?? throw new ArgumentException("追加グループの投影がありません。");
        var files = draft.NewFiles ?? throw new ArgumentException("追加ファイルの投影がありません。");
        if (groups.Count == 0 && files.Count == 0)
        {
            return Render(bundle, draft with { EditorVersion = 5 });
        }

        if (groups.Count > 1000 || bundle.Sources.Count + files.Count > EditorLocalFiles.CountLimit || (files.Count > 0 && draft.LocalSources is null))
        {
            throw new ArgumentException("追加は1000グループ・合計32ファイルまでです。ファイル追加はローカル下書きだけで利用できます。");
        }

        // 原文の順序を固定し、追加ファイルを末尾に置くことで既存ネットの所属を変えません。
        var sources = bundle.Sources.Concat(files.Select(CreateFile)).ToList();
        EditorLocalFiles.ValidateSources(sources);
        var units = bundle.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        var allPaths = units.Keys.ToHashSet(StringComparer.Ordinal);
        var groupPaths = units.Values.Where(u => u.Type == "g").Select(u => u.Path).ToHashSet(StringComparer.Ordinal);
        groupPaths.Add("/");
        var additions = groups.Concat(files.Select((f, i) => new EditorGroupCreation(bundle.Sources.Count + i, f.TargetPath, f.GroupName))).ToList();
        foreach (var addition in additions)
        {
            if (addition is null || string.IsNullOrEmpty(addition.ParentPath))
            {
                throw new ArgumentException("追加グループと絶対論理配置先を指定してください。");
            }

            string path = addition.ParentPath.TrimEnd('/') + "/" + addition.Name;
            if (addition.SourceIndex < 0 || addition.SourceIndex >= sources.Count || !ValidName(addition.Name)
                || addition.ParentPath.Length > 1024 || addition.ParentPath.Any(char.IsControl) || Jp1DefinitionParser.NormalizePath(addition.ParentPath) != addition.ParentPath
                || path.Length > 1024 || !allPaths.Add(path))
            {
                throw new ArgumentException("追加グループの配置・名前が不正、または完全名が重複しています。");
            }

            groupPaths.Add(path);
        }

        // 新規配置先の未定義祖先は、読込と同じ属性のない仮想グループです。
        // 既存ネットやジョブを親グループとして読み替えることはしません。
        foreach (var file in files)
        {
            for (string path = file.TargetPath; path != "/"; path = Scheduling.ScheduleCalculator.Parent(path))
            {
                if (allPaths.Contains(path) && !groupPaths.Contains(path))
                {
                    throw new ArgumentException($"{path}: 新規ファイルの配置先と祖先はグループでなければなりません。");
                }

                allPaths.Add(path);
                groupPaths.Add(path);
            }
        }

        var documents = sources.Select(s => new Document(s)).ToList();
        foreach (var addition in additions)
        {
            var source = sources[addition.SourceIndex];
            bool physical = documents[addition.SourceIndex].Syntax.ContainsKey(addition.ParentPath)
                || additions.Any(g => g.SourceIndex == addition.SourceIndex && g.ParentPath.TrimEnd('/') + "/" + g.Name == addition.ParentPath);
            if (!groupPaths.Contains(addition.ParentPath) || (!physical && source.Mapping.TargetPath != addition.ParentPath)
                || addition.ParentPath.Count(c => c == '/') - source.Mapping.TargetPath.TrimEnd('/').Count(c => c == '/') >= 64)
            {
                throw new ArgumentException("親は同一ファイルのグループまたは配置先です。64階層を超える追加はできません。");
            }
        }

        foreach (var byFile in groups.GroupBy(g => g.SourceIndex))
        {
            var document = documents[byFile.Key];
            var children = byFile.ToLookup(g => g.ParentPath, StringComparer.Ordinal);
            var addedPaths = byFile.Select(g => g.ParentPath.TrimEnd('/') + "/" + g.Name).ToHashSet(StringComparer.Ordinal);
            string Shell(EditorGroupCreation group)
            {
                string path = group.ParentPath.TrimEnd('/') + "/" + group.Name;
                return document.Newline + $"unit={group.Name};{{ty=g;" + string.Concat(children[path].Select(Shell)) + "}" + document.Newline;
            }

            foreach (var parent in byFile.Where(g => !addedPaths.Contains(g.ParentPath)).GroupBy(g => g.ParentPath))
            {
                int position = document.Syntax.TryGetValue(parent.Key, out var syntax) ? syntax.End - 1 : document.Text.Length;
                document.Replace(position, position, string.Concat(parent.Select(Shell)));
            }
        }

        sources = documents.Select(d => d.Render()).ToList();
        EditorLocalFiles.ValidateSources(sources);
        var augmented = new DefinitionBundle { Hash = bundle.Hash, Sources = sources };
        augmented.Units.AddRange(sources.SelectMany(s => Jp1DefinitionParser.Parse(Decode(s).TrimStart('\uFEFF'), s.Mapping)));
        var present = bundle.Sources.SelectMany(s => new Document(s).Syntax.Keys).ToHashSet(StringComparer.Ordinal);
        augmented.Units.AddRange(bundle.Units.Where(u => !present.Contains(u.Path)));
        CompleteEditorParents(augmented);
        var rendered = Render(augmented, draft with { EditorVersion = 5 });
        EditorLocalFiles.ValidateSources(rendered);
        return rendered;
    }

    private static void CompleteEditorParents(DefinitionBundle bundle)
    {
        var paths = bundle.Units.Flatten().Select(u => u.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var source in bundle.Sources)
        {
            for (string path = source.Mapping.TargetPath; path != "/"; path = Scheduling.ScheduleCalculator.Parent(path))
            {
                if (paths.Add(path))
                {
                    bundle.Units.Add(new UnitDefinition { Path = path, Name = path[(path.LastIndexOf('/') + 1)..], Type = "g", Attributes = new() { ["ty"] = ["g"] } });
                }
            }
        }
    }
}
