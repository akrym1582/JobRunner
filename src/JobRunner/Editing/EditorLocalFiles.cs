using System.Text;
using JobRunner.Controllers.Ui;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Services;

namespace JobRunner.Editing;

/// <summary>ローカル原文の入力境界です。ファイルシステム・Storageへ書き込みません。</summary>
public static partial class EditorLocalFiles
{
    /// <summary>一つの原文のバイト上限です。</summary>
    public const int FileLimit = 2 * 1024 * 1024;

    /// <summary>原文全体またはZIPのバイト上限です。</summary>
    public const int TotalLimit = 8 * 1024 * 1024;

    /// <summary>一括入力する原文数の上限です。</summary>
    public const int CountLimit = 32;

    /// <summary>入力を検証し、既存エンジンで全ファイルの参照を解決します。</summary>
    /// <param name="request">原文または出力ZIPです。</param>
    /// <param name="definitions">意味検証器です。</param>
    /// <param name="allowMissingCalendars">編集専用の参照不足許可です。</param>
    /// <returns>保存していない定義です。</returns>
    public static DefinitionBundle Read(EditorLocalRequest request, DefinitionService definitions, bool allowMissingCalendars = false)
    {
        if ((request.Sources is null) == (request.ZipBase64 is null))
        {
            throw new ArgumentException("Unitファイル一式またはJobRunner出力ZIPのどちらかを指定してください。");
        }

        var sources = request.Sources ?? ReadZip(DecodeBase64(request.ZipBase64!, TotalLimit));
        return definitions.Validate(ValidateSources(sources), allowMissingCalendars);
    }

    /// <summary>解析前に入力のパス・バイト・階層制限を検証します。</summary>
    /// <param name="sources">入力原文です。</param>
    /// <returns>呼出元の証跡を含まない原文です。</returns>
    internal static List<SourceEvidence> ValidateSources(List<SourceEvidence> sources)
    {
        if (sources.Count is < 1 or > CountLimit)
        {
            throw new ArgumentException("ローカル入力は1〜32ファイルです。");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SourceEvidence>();
        int total = 0;
        foreach (var source in sources)
        {
            if (source?.Mapping is not { } mapping || mapping.Kind != "Unit")
            {
                throw new ArgumentException("ローカル入力はUnit定義だけに対応します。");
            }

            SafePath(mapping.BlobPath);
            if (!paths.Add(mapping.BlobPath))
            {
                throw new ArgumentException("元ファイルの対応が重複しています。大文字小文字だけの違いも使用できません。");
            }

            if (string.IsNullOrEmpty(mapping.TargetPath) || mapping.TargetPath.Length > 1024 || mapping.TargetPath.Any(char.IsControl)
                || Jp1DefinitionParser.NormalizePath(mapping.TargetPath) != mapping.TargetPath)
            {
                throw new ArgumentException("配置先は正規化された絶対論理パスで指定してください。");
            }

            int codePage = mapping.Encoding?.ToLowerInvariant() switch
            {
                "utf-8" => 65001,
                "utf-16" or "utf-16le" => 1200,
                "utf-16be" => 1201,
                "shift_jis" or "shift-jis" => 932,
                _ => throw new ArgumentException("文字コードはUTF-8、UTF-16LE/BE、Shift_JISから明示してください。"),
            };
            byte[] bytes = DecodeBase64(source.Base64, FileLimit);
            total += bytes.Length;
            if (total > TotalLimit)
            {
                throw new ArgumentException("ローカル原文の合計は8MiBまでです。");
            }

            int bom = bytes.AsSpan().StartsWith(new byte[] { 0, 0, 254, 255 }) || bytes.AsSpan().StartsWith(new byte[] { 255, 254, 0, 0 }) ? 12000
                : bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 65001
                : bytes.AsSpan().StartsWith(new byte[] { 255, 254 }) ? 1200
                : bytes.AsSpan().StartsWith(new byte[] { 254, 255 }) ? 1201 : 0;
            if (bom != 0 && bom != codePage)
            {
                throw new ArgumentException("BOMと指定文字コードが一致しません。UTF-32は未対応です。");
            }

            string text = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes);
            CheckNesting(text);

            // 呼出元の証跡やハッシュを信用せず、元バイトから既存検証器に再計算させます。
            result.Add(new SourceEvidence
            {
                Mapping = new() { Kind = "Unit", BlobPath = mapping.BlobPath, TargetPath = mapping.TargetPath, Encoding = mapping.Encoding! },
                Base64 = Convert.ToBase64String(bytes),
            });
        }

        return result;
    }

    private static byte[] DecodeBase64(string value, int limit)
    {
        if (string.IsNullOrEmpty(value) || value.Length > ((limit + 2) / 3 * 4))
        {
            throw new ArgumentException("入力が空、またはサイズ上限を超えています。");
        }

        byte[] bytes = Convert.FromBase64String(value);
        if (bytes.Length == 0 || bytes.Length > limit)
        {
            throw new ArgumentException("入力が空、またはサイズ上限を超えています。");
        }

        return bytes;
    }

    private static void SafePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.Any(c => char.IsControl(c) || "\\:<>\"|?*".Contains(c, StringComparison.Ordinal))
            || path.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ')))
        {
            throw new ArgumentException("ファイル対応には安全な相対パスを指定してください。絶対パス・ドット区間・制御文字は使えません。");
        }
    }

    private static void CheckNesting(string text)
    {
        int depth = 0;
        int units = 0;
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (quoted && text[i] == '#')
            {
                i++;
            }
            else if (text[i] == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw new FormatException("ブロックコメントが閉じていません。");
                }

                i = end + 1;
            }
            else if (!quoted && text[i] == '{' && (++depth > 64 || ++units > 10000))
            {
                throw new ArgumentException("ローカル入力は64階層・1ファイル10000ユニットまでです。");
            }
            else if (!quoted && text[i] == '}')
            {
                depth--;
            }
        }
    }
}
