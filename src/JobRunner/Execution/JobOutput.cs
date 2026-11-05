using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobRunner.Execution;

/// <summary>プロセスが明示出力した変数を、上限付きの独自契約で読みます。</summary>
public static class JobOutput
{
    /// <summary>未出力は空集合、不正出力はエラーとし、部分的に受理しません。</summary>
    /// <param name="path">試行専用のJSONファイルです。</param>
    /// <returns>大文字小文字を区別するマクロ名と文字列値です。</returns>
    public static Dictionary<string, string> Read(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new FormatException("変数出力は通常のファイルにしてください。");
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return result;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 131072)
        {
            throw new FormatException("変数出力ファイルが128KiBを超えています。");
        }

        // 長さ確認後に子プロセスが追記しても、上限以上のメモリーを読み込みません。
        byte[] buffer = new byte[131073];
        int count = 0;
        int read;
        while (count < buffer.Length && (read = stream.Read(buffer, count, buffer.Length - count)) > 0)
        {
            count += read;
        }

        if (count == buffer.Length)
        {
            throw new FormatException("変数出力ファイルが128KiBを超えています。");
        }

        using var document = JsonDocument.Parse(buffer.AsMemory(0, count));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("変数出力はJSONオブジェクトにしてください。");
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!Regex.IsMatch(property.Name, "^AJS2[A-Z0-9.]{1,64}$") || property.Value.ValueKind != JsonValueKind.String
                || Encoding.UTF8.GetByteCount(property.Value.GetString()!) > 255 || result.Count == 32 || !result.TryAdd(property.Name, property.Value.GetString()!))
            {
                throw new FormatException("変数の名前・型・長さ・件数・重複が不正です。");
            }
        }

        return result;
    }
}
