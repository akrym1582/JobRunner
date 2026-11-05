using System.Text;
using JobRunner.Models;

namespace JobRunner.Import;

/// <summary>
/// 区切り文字と引用文字列を読み分け、原文の階層と繰り返し属性を保持します。
/// </summary>
public sealed class Jp1DefinitionParser
{
    /// <summary>
    /// 全ユニットを構文解析します。意味が未対応の属性もこの段階では捨てません。
    /// </summary>
    /// <param name="source">文字コードを明示して復号した原文です。</param>
    /// <param name="mapping">配置先の親グループと診断用Blob名です。</param>
    /// <returns>指定親グループ配下へ配置したユニットです。</returns>
    public static List<UnitDefinition> Parse(string source, DefinitionSource mapping)
    {
        var reader = new Reader(source, mapping.BlobPath);
        var units = new List<UnitDefinition>();
        while (!reader.End)
        {
            units.Add(reader.Unit(NormalizePath(mapping.TargetPath)));
        }

        if (units.Count == 0)
        {
            throw new FormatException($"{mapping.BlobPath}: 定義が空です。");
        }

        return units;
    }

    /// <summary>
    /// 親パスの区切りを正規化し、意味が曖昧な相対指定を拒否します。
    /// </summary>
    /// <param name="path">スラッシュで始まるJP1論理パスです。</param>
    /// <returns>末尾区切りのない完全名。ルートはスラッシュです。</returns>
    public static string NormalizePath(string path)
    {
        if (!path.StartsWith('/') || path.Contains('\\') || path.Split('/').Any(x => x is "." or ".."))
        {
            throw new FormatException("TargetPathには絶対論理パスを指定してください。");
        }

        return "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// 読み取り位置を持つ再帰下降パーサーです。文字列内のセミコロンを属性終端と混同しません。
    /// </summary>
    private sealed class Reader(string text, string source)
    {
        /// <summary>原文中の現在位置。文字列を分割せず構文境界を追跡します。</summary>
        private int offset;

        /// <summary>利用者が原文を修正できるよう1始まりの行番号を保持します。</summary>
        private int line = 1;

        /// <summary>
        /// Gets a value indicating whether. コメントと空白を除いた入力末尾かどうかを取得します。
        /// </summary>
        public bool End
        {
            get
            {
                Skip();
                return offset == text.Length;
            }
        }

        /// <summary>
        /// 一つのユニットと下位定義を読み取り、配置先の完全名を作ります。
        /// </summary>
        /// <param name="parent">配置先の親グループまたはネットの完全名です。</param>
        /// <returns>属性と子ユニットを含む構文木です。</returns>
        public UnitDefinition Unit(string parent)
        {
            Skip();
            int startLine = line;
            ExpectWord("unit");
            Expect('=');
            string header = Value();
            string name = header.Split(',')[0].Trim();
            if (header.Contains(',') || name.Length == 0 || name.Contains('/') || name is "." or "..")
            {
                throw Error("所有者・許可属性または絶対名の基点解決は未対応です。単純ユニット名を指定してください。");
            }

            var unit = new UnitDefinition
            {
                Name = name,
                Path = parent.TrimEnd('/') + "/" + name,
                Line = startLine,
                Source = source,
            };
            Expect('{');
            while (true)
            {
                Skip();
                if (offset >= text.Length)
                {
                    throw Error("閉じ括弧がありません。");
                }

                if (text[offset] == '}')
                {
                    offset++;
                    break;
                }

                int saved = offset;
                string key = Word();
                if (key == "unit")
                {
                    offset = saved;
                    unit.Children.Add(Unit(unit.Path));
                    continue;
                }

                if (unit.Children.Count > 0)
                {
                    throw Error("下位ユニットの後に属性は記述できません。");
                }

                Expect('=');
                if (!unit.Attributes.TryGetValue(key, out var values))
                {
                    values = [];
                    unit.Attributes.Add(key, values);
                }

                values.Add(Value());
            }

            unit.Type = unit.Attributes.GetValueOrDefault("ty")?.FirstOrDefault() ?? string.Empty;
            return unit;
        }

        /// <summary>識別子を読み、属性名と構造キーワードを区別します。</summary>
        /// <returns>空白を含まない識別子です。</returns>
        private string Word()
        {
            Skip();
            int start = offset;
            while (offset < text.Length && (char.IsLetterOrDigit(text[offset]) || text[offset] == '_'))
            {
                offset++;
            }

            if (start == offset)
            {
                throw Error("属性名が必要です。");
            }

            return text[start..offset];
        }

        /// <summary>引用文字列のキャストを解決し、セミコロンまでの属性値を読みます。</summary>
        /// <returns>引用符を除いた値。複合値の括弧とカンマは維持します。</returns>
        private string Value()
        {
            Skip();
            var value = new StringBuilder();
            bool quoted = false;
            int meaningfulLength = 0;
            while (offset < text.Length)
            {
                char c = text[offset++];
                if (c == '\n')
                {
                    line++;
                }

                if (quoted && c == '#')
                {
                    if (offset == text.Length || text[offset] is not ('#' or '"'))
                    {
                        throw Error("文字列のキャストは##または#\"で指定してください。");
                    }

                    value.Append(text[offset++]);
                }
                else if (c == '"')
                {
                    quoted = !quoted;
                }
                else if (!quoted && c == ';')
                {
                    return value.ToString(0, meaningfulLength);
                }
                else if (!quoted && c is '{' or '}')
                {
                    throw Error("属性の終端セミコロンがありません。");
                }
                else
                {
                    value.Append(c);
                }

                if (quoted || !char.IsWhiteSpace(c))
                {
                    meaningfulLength = value.Length;
                }
            }

            throw Error("文字列または属性が閉じていません。");
        }

        /// <summary>構文上必要なキーワードとの不一致を位置付きで報告します。</summary>
        /// <param name="word">期待する構造キーワードです。</param>
        private void ExpectWord(string word)
        {
            if (Word() != word)
            {
                throw Error($"{word}が必要です。");
            }
        }

        /// <summary>必要な区切り文字を消費し、壊れた定義を受け入れないようにします。</summary>
        /// <param name="character">期待する区切り文字です。</param>
        private void Expect(char character)
        {
            Skip();
            if (offset == text.Length || text[offset++] != character)
            {
                throw Error($"{character}が必要です。");
            }
        }

        /// <summary>引用文字列以外の空白とブロックコメントを読み飛ばします。</summary>
        private void Skip()
        {
            while (offset < text.Length)
            {
                if (char.IsWhiteSpace(text[offset]))
                {
                    if (text[offset++] == '\n')
                    {
                        line++;
                    }
                }
                else if (text.AsSpan(offset).StartsWith("/*"))
                {
                    int end = text.IndexOf("*/", offset + 2, StringComparison.Ordinal);
                    if (end < 0 || text[(offset + 2)..end].Contains("/*", StringComparison.Ordinal))
                    {
                        throw Error("コメントが閉じていないか入れ子です。");
                    }

                    int lineStart = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
                    int lineEnd = text.IndexOf('\n', end + 2);
                    if (lineEnd < 0)
                    {
                        lineEnd = text.Length;
                    }

                    if (text[lineStart..offset].Trim().Length > 0 || text[(end + 2)..lineEnd].Trim().Length > 0)
                    {
                        throw Error("コメントは属性と同じ行に記述できません。");
                    }

                    line += text[offset..end].Count(c => c == '\n');
                    offset = end + 2;
                }
                else
                {
                    break;
                }
            }
        }

        /// <summary>入力元と現在行を含む診断を作ります。</summary>
        /// <param name="message">修正すべき構文上の問題です。</param>
        /// <returns>APIの検証結果へ返す例外です。</returns>
        private FormatException Error(string message) => new($"{source}:{line}: {message}");
    }
}
