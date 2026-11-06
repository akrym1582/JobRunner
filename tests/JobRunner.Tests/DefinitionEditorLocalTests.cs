using System.IO.Compression;
using System.Text;
using System.Text.Json;
using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>保存せずローカル原文を読み、入力境界と出力原文を確認します。</summary>
public sealed class DefinitionEditorLocalTests
{
    private const string Text = "/* 原文 */\r\nunit=net,0000,owner,team;{ty=n;sd=0,ud;unit=job;{ty=j;te=\"true\";}}\r\n";

    /// <summary>指定文字コードとBOMの元バイトを保ちます。</summary>
    /// <param name="encoding">文字コードです。</param>
    /// <param name="bom">BOMの有無です。</param>
    [Theory]
    [InlineData("utf-8", false)]
    [InlineData("utf-8", true)]
    [InlineData("utf-16", true)]
    [InlineData("utf-16BE", true)]
    [InlineData("shift_jis", false)]
    public void PreservesBytesAndEditsOnlyRequestedText(string encoding, bool bom)
    {
        var source = Source(encoding: encoding, bom: bom);
        var bundle = Read([source]);
        var draft = DefinitionEditor.Project(bundle, 4) with { LocalSources = bundle.Sources };
        Assert.Equal(source.Base64, Assert.Single(DefinitionEditor.Render(bundle, draft)).Base64);
        draft.Networks[0].Nodes[0].Attributes["te"] = ["echo changed"];
        var rendered = Assert.Single(DefinitionEditor.Render(bundle, draft));
        byte[] expected = Encoding.GetEncoding(encoding).GetBytes((bom ? "\uFEFF" : string.Empty) + Text.Replace("true", "echo changed", StringComparison.Ordinal));
        Assert.Equal(expected, Convert.FromBase64String(rendered.Base64));
        Assert.Equal(source.Mapping.Encoding, rendered.Mapping.Encoding);
        Assert.Equal(source.Base64, draft.LocalSources[0].Base64);
        Assert.NotEqual(bundle.Hash, Read([rendered]).Hash);
    }

    /// <summary>別ファイル参照と配置対応を一括で検証します。</summary>
    [Fact]
    public void ResolvesCrossFileReferencesAndZipMappings()
    {
        var first = Source("a/net.ajs", "unit=net;{ty=n;sd=0,ud;jc=\"/business/cal\";}");
        var second = Source("b/cal.ajs", "unit=cal;{ty=g;cl=su;}", target: "/business");
        var direct = Read([first, second]);
        var zipped = EditorLocalFiles.Read(new(ZipBase64: Zip([first, second])), Service());
        Assert.Equal(direct.Hash, zipped.Hash);
        Assert.Equal(direct.Sources.Select(s => s.Base64), zipped.Sources.Select(s => s.Base64));
        Assert.Contains(zipped.Units.Flatten(), u => u.Path == "/business/cal");
        Assert.Throws<FormatException>(() => Read([first]));
        Assert.Throws<FormatException>(() => Read([first, Source("other.ajs", "unit=net;{ty=n;sd=0,ud;}")]));
    }

    /// <summary>ZIPのCRC破損と形式の破損を見逃しません。</summary>
    [Fact]
    public void RejectsCorruptZip()
    {
        byte[] zip = Convert.FromBase64String(Zip([Source()]));
        int header = zip.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 0x01, 0x02 });
        Assert.True(header >= 0);
        zip[header + 16] ^= 1;
        Assert.Throws<ArgumentException>(() => EditorLocalFiles.Read(new(ZipBase64: Convert.ToBase64String(zip)), Service()));
        Assert.Throws<ArgumentException>(() => EditorLocalFiles.Read(new(ZipBase64: Convert.ToBase64String([1, 2, 3])), Service()));
    }

    /// <summary>件数・合計・階層の上限ちょうどは受理します。</summary>
    [Fact]
    public void AcceptsInclusiveLimits()
    {
        Assert.Equal(32, Read(Enumerable.Range(0, 32).Select(i => Source(i + ".ajs", "unit=n" + i + ";{ty=n;sd=0,ud;}")).ToList()).Sources.Count);
        var sources = Enumerable.Range(0, 4).Select(i =>
        {
            string text = "unit=n" + i + ";{ty=n;sd=0,ud;}";
            return Source(i + ".ajs", text + new string(' ', EditorLocalFiles.FileLimit - text.Length));
        }).ToList();
        Assert.Equal(4, Read(sources).Sources.Count);
        Assert.Single(Read([Source(text: string.Concat(Enumerable.Repeat("unit=n;{ty=n;", 64)) + new string('}', 64))]).Sources);
    }

    /// <summary>パスの曖昧さやディレクトリ外参照を拒否します。</summary>
    /// <param name="path">拒否するファイル対応です。</param>
    [Theory]
    [InlineData("../a.ajs")]
    [InlineData("/a.ajs")]
    [InlineData("C:/a.ajs")]
    [InlineData("a\\b.ajs")]
    [InlineData("a//b.ajs")]
    [InlineData("a/./b.ajs")]
    [InlineData("a\nb.ajs")]
    [InlineData("a.")]
    public void RejectsUnsafePaths(string path) => Assert.Throws<ArgumentException>(() => Read([Source(path)]));

    /// <summary>入力の文字コード・形式・配置先を曖昧なまま補完しません。</summary>
    [Fact]
    public void RejectsInvalidSourceMetadataAndBytes()
    {
        Assert.Throws<ArgumentException>(() => Read([Source(), Source("JOBS.AJS")]));
        var source = Source();
        source.Mapping.Kind = "Calendar";
        Assert.Throws<ArgumentException>(() => Read([source]));
        source = Source();
        source.Mapping.Encoding = "auto";
        Assert.Throws<ArgumentException>(() => Read([source]));
        source = Source(encoding: "utf-16", bom: true);
        source.Mapping.Encoding = "utf-8";
        Assert.Throws<ArgumentException>(() => Read([source]));
        source = Source();
        source.Base64 = Convert.ToBase64String([0xff, 0xff]);
        Assert.Throws<DecoderFallbackException>(() => Read([source]));
        source.Base64 = "not base64";
        Assert.Throws<FormatException>(() => Read([source]));
        source = Source(target: "/business/../x");
        Assert.Throws<FormatException>(() => Read([source]));
        Assert.Throws<ArgumentException>(() => Read([Source(target: "/business//x")]));
        Assert.Throws<ArgumentException>(() => EditorLocalFiles.Read(new(), Service()));
        Assert.Throws<ArgumentException>(() => EditorLocalFiles.Read(new([Source()], Zip([Source()])), Service()));
    }

    /// <summary>ファイル・合計・件数・深さの上限は展開前にも適用します。</summary>
    [Fact]
    public void RejectsOversizedInputsAndKeepsExactFileBoundary()
    {
        var exact = Source(text: Text + new string(' ', EditorLocalFiles.FileLimit - Encoding.UTF8.GetByteCount(Text)));
        Assert.Single(Read([exact]).Sources);
        exact.Base64 = Convert.ToBase64String(new byte[EditorLocalFiles.FileLimit + 1]);
        Assert.Throws<ArgumentException>(() => Read([exact]));
        Assert.Throws<ArgumentException>(() => Read(Enumerable.Range(0, 33).Select(i => Source(i + ".ajs", "unit=n" + i + ";{ty=n;sd=0,ud;}")).ToList()));
        var many = Enumerable.Range(0, 5).Select(i => Source(i + ".ajs", "unit=n" + i + ";{ty=n;sd=0,ud;}" + new string(' ', EditorLocalFiles.FileLimit - 100))).ToList();
        Assert.Throws<ArgumentException>(() => Read(many));
        Assert.Throws<ArgumentException>(() => Read([Source(text: string.Concat(Enumerable.Repeat("unit=n;{ty=n;", 65)) + new string('}', 65))]));
        Assert.Throws<ArgumentException>(() => EditorLocalFiles.Read(new(ZipBase64: Convert.ToBase64String(new byte[EditorLocalFiles.TotalLimit + 1])), Service()));
    }

    /// <summary>ZIPの未対応・重複・危険なエントリー・欠落と膨張を拒否します。</summary>
    /// <param name="problem">壊す箇所です。</param>
    [Theory]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    [InlineData("unlisted")]
    [InlineData("missing")]
    [InlineData("symlink")]
    [InlineData("directory")]
    [InlineData("large")]
    [InlineData("version")]
    [InlineData("duplicate-json")]
    [InlineData("missing-encoding")]
    [InlineData("bad-json")]
    public void RejectsUnsafeZip(string problem)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            string path = problem == "traversal" ? "../jobs.ajs" : "sources/001/jobs.ajs";
            var entry = archive.CreateEntry(path);
            if (problem == "symlink")
            {
                entry.ExternalAttributes = 0xa1ff << 16;
            }

            if (problem == "directory")
            {
                entry.ExternalAttributes = 0x10;
            }

            using (var writer = new StreamWriter(entry.Open()))
            {
                writer.Write(problem == "large" ? new string(' ', EditorLocalFiles.FileLimit + 1) : Text);
            }

            if (problem is "duplicate" or "unlisted")
            {
                using var writer = new StreamWriter(archive.CreateEntry(problem == "duplicate" ? path.ToUpperInvariant() : "extra.ajs").Open());
                writer.Write(Text);
            }

            string manifest = JsonSerializer.Serialize(new { FormatVersion = problem == "version" ? 2 : 1, Sources = new[] { new { File = problem == "missing" ? "missing.ajs" : path, Mapping = Source().Mapping } } });
            if (problem == "duplicate-json")
            {
                manifest = manifest.Replace("\"FormatVersion\":1", "\"FormatVersion\":1,\"FormatVersion\":1", StringComparison.Ordinal);
            }

            if (problem == "missing-encoding")
            {
                manifest = manifest.Replace(",\"Encoding\":\"utf-8\"", string.Empty, StringComparison.Ordinal);
            }

            using var metadata = new StreamWriter(archive.CreateEntry("manifest.json").Open());
            metadata.Write(problem == "bad-json" ? "{" : manifest);
        }

        Assert.Throws<ArgumentException>(() => EditorLocalFiles.Read(new(ZipBase64: Convert.ToBase64String(stream.ToArray())), Service()));
    }

    private static DefinitionService Service()
    {
        var options = new RunnerOptions();
        return new(new StorageRepository(options), options);
    }

    private static DefinitionBundle Read(List<SourceEvidence> sources) => EditorLocalFiles.Read(new(sources), Service());

    private static SourceEvidence Source(string path = "jobs.ajs", string text = Text, string encoding = "utf-8", bool bom = false, string target = "/")
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return new() { Mapping = new() { BlobPath = path, TargetPath = target, Encoding = encoding }, Base64 = Convert.ToBase64String(Encoding.GetEncoding(encoding).GetBytes((bom ? "\uFEFF" : string.Empty) + text)) };
    }

    private static string Zip(List<SourceEvidence> sources)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var manifest = new List<object>();
            for (int i = 0; i < sources.Count; i++)
            {
                string name = $"sources/{i + 1:D3}/{Path.GetFileName(sources[i].Mapping.BlobPath)}";
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(Convert.FromBase64String(sources[i].Base64));
                manifest.Add(new { File = name, sources[i].Mapping });
            }

            using var metadata = archive.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(metadata, new { FormatVersion = 1, BaseDefinitionHash = "ignored-parent-hash", Sources = manifest });
        }

        return Convert.ToBase64String(stream.ToArray());
    }
}
