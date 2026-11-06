using System.IO.Compression;
using System.Text.Json;
using JobRunner.Models;

namespace JobRunner.Editing;

/// <summary>出力ZIPを展開せずメモリーで検証します。</summary>
public static partial class EditorLocalFiles
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    private static List<SourceEvidence> ReadZip(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (archive.Entries.Count is < 2 or > CountLimit + 1)
            {
                throw new ArgumentException("ZIPはmanifest.jsonと1〜32個の原文だけで構成してください。");
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                SafePath(entry.FullName);
                int fileType = (entry.ExternalAttributes >> 16) & 0xf000;
                if (!names.Add(entry.FullName) || (fileType != 0 && fileType != 0x8000) || (entry.ExternalAttributes & 0x10) != 0)
                {
                    throw new ArgumentException("ZIPの重複・ディレクトリ・リンクは使用できません。");
                }

                int limit = entry.FullName == "manifest.json" ? 256 * 1024 : FileLimit;
                total += entry.Length;
                if (entry.Length <= 0 || entry.Length > limit || total > TotalLimit + (256 * 1024))
                {
                    throw new ArgumentException("ZIPの展開後サイズが上限を超えています。");
                }
            }

            var manifest = archive.GetEntry("manifest.json") ?? throw new ArgumentException("JobRunnerのmanifest.jsonがありません。");
            using var json = JsonDocument.Parse(ReadEntry(manifest, 256 * 1024));
            var root = json.RootElement;
            UniqueProperties(root);
            if (root.GetProperty("FormatVersion").GetInt32() != 1)
            {
                throw new ArgumentException("未対応のZIP形式です。");
            }

            var sources = new List<SourceEvidence>();
            var used = new HashSet<string>(StringComparer.Ordinal) { "manifest.json" };
            foreach (var item in root.GetProperty("Sources").EnumerateArray())
            {
                string file = item.GetProperty("File").GetString() ?? string.Empty;
                SafePath(file);
                var entry = archive.GetEntry(file);
                if (!used.Add(file) || entry is null)
                {
                    throw new ArgumentException("ZIPのファイル対応が重複、または原文が不足しています。");
                }

                var mappingJson = item.GetProperty("Mapping");
                foreach (string key in new[] { "Kind", "BlobPath", "TargetPath", "Encoding" })
                {
                    if (mappingJson.GetProperty(key).ValueKind != JsonValueKind.String)
                    {
                        throw new ArgumentException("ファイル対応の形式・配置先・文字コードを明示してください。");
                    }
                }

                var mapping = mappingJson.Deserialize<DefinitionSource>() ?? throw new ArgumentException("ファイル対応がありません。");
                sources.Add(new() { Mapping = mapping, Base64 = Convert.ToBase64String(ReadEntry(entry, FileLimit)) });
            }

            if (used.Count != archive.Entries.Count)
            {
                throw new ArgumentException("ZIPに対応表へ記載されていないファイルがあります。");
            }

            // BaseDefinitionHashは編集前の版を表すため、読込後のハッシュには流用しません。
            return sources;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or IOException or InvalidDataException or NotSupportedException)
        {
            throw new ArgumentException("ZIPまたはmanifest.jsonを読み込めません: " + error.Message, error);
        }
    }

    private static void UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ArgumentException("manifest.jsonのプロパティが重複しています。");
                }

                UniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                UniqueProperties(child);
            }
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, int limit)
    {
        using var input = entry.Open();
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        uint crc = uint.MaxValue;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            if (output.Length + read > limit)
            {
                throw new ArgumentException("ZIPの展開後サイズが上限を超えています。");
            }

            output.Write(buffer, 0, read);
            for (int i = 0; i < read; i++)
            {
                crc = CrcTable[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
            }
        }

        if (output.Length != entry.Length || ~crc != entry.Crc32)
        {
            throw new ArgumentException("ZIPの原文サイズまたはCRCが一致しません。");
        }

        return output.ToArray();
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 1 ? (value >> 1) ^ 0xedb88320 : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
