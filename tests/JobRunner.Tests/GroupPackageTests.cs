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

/// <summary>グループZIPの原文・依存・追加と置換を検証します。</summary>
public sealed class GroupPackageTests
{
    /// <summary>別ファイルの配置を保ち、対象外の原文は変更しません。</summary>
    /// <param name="encoding">原文の文字コードです。</param>
    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("shift_jis")]
    public void RoundTripsAndAddsOrReplacesSubtree(string encoding)
    {
        var service = Service();
        var bundle = service.Validate([Source("main.ajs", "unit=group;{ty=g;cl=su;unit=n;{ty=n;sd=0,ud;}}unit=keep;{ty=g;cm=\"keep\";}", encoding), Source("child.ajs", "unit=child;{ty=g;unit=n;{ty=n;sd=0,ud;jc=\"/group\";}}", encoding, "/group")]);
        var draft = DefinitionEditor.Project(bundle, 9);
        var zip = DefinitionEditor.ExportGroup(bundle, new(draft, "/group"), service);
        var document = DefinitionEditor.ImportGroup(bundle, new(draft, string.Empty, Convert.ToBase64String(zip), "/", "copy"), service, new());
        var added = service.Validate(DefinitionEditor.Render(bundle, document.Draft));
        Assert.Equal(bundle.Sources.Select(s => s.Base64), added.Sources.Where(s => !s.Mapping.BlobPath.StartsWith("group-import/", StringComparison.Ordinal)).Select(s => s.Base64));
        Assert.Contains(added.Units.Flatten(), u => u.Path == "/copy/child/n" && u.Get("jc") == "/copy");
        Assert.Contains(added.Units.Flatten(), u => u.Path == "/group/child/n");
        var replaced = DefinitionEditor.ImportGroup(bundle, new(draft, "/group", Convert.ToBase64String(zip)), service, new());
        var output = service.Validate(DefinitionEditor.Render(bundle, replaced.Draft));
        Assert.Equal(bundle.Units.Flatten().Select(u => u.Path).Order(), output.Units.Flatten().Select(u => u.Path).Order());
        Assert.Contains(output.Units.Flatten(), u => u.Path == "/keep" && u.Get("cm") == "keep");
        Assert.Equal("/group", replaced.Draft.GroupOrigins!["/group"]);
    }

    /// <summary>選択外への参照とカレンダー継承を黙って切り離しません。</summary>
    [Fact]
    public void PreservesExternalDependenciesAndRejectsNameCollision()
    {
        var service = Service();
        var bundle = service.Validate([Source("a.ajs", "unit=calendar;{ty=g;cl=su;}unit=g;{ty=g;unit=n;{ty=n;jc=\"/calendar\";sd=0,ud;}}")]);
        Assert.NotEmpty(DefinitionEditor.ExportGroup(bundle, new(DefinitionEditor.Project(bundle, 9), "/g"), service));
        bundle = service.Validate([Source("a.ajs", "unit=parent;{ty=g;cl=su;unit=g;{ty=g;unit=n;{ty=n;sd=0,ud;}}}")]);
        Assert.NotEmpty(DefinitionEditor.ExportGroup(bundle, new(DefinitionEditor.Project(bundle, 9), "/parent/g"), service));
        bundle = service.Validate([Source("a.ajs", "unit=g;{ty=g;unit=n;{ty=n;sd=0,ud;}}")]);
        var draft = DefinitionEditor.Project(bundle, 9);
        string zip = Convert.ToBase64String(DefinitionEditor.ExportGroup(bundle, new(draft, "/g"), service));
        Assert.Contains("同名", Assert.Throws<ArgumentException>(() => DefinitionEditor.ImportGroup(bundle, new(draft, string.Empty, zip, "/", "g"), service, new())).Message, StringComparison.Ordinal);
    }

    /// <summary>CP932の原文を維持し、.NETが無効と判定する重複符号は入力時に拒否します。</summary>
    [Fact]
    public void PreservesCp932BytesAndRejectsInvalidDuplicate()
    {
        var service = Service();
        var prefix = Encoding.ASCII.GetBytes("unit=g;{ty=g;cm=\"");
        var suffix = Encoding.ASCII.GetBytes("\";unit=n;{ty=n;sd=0,ud;unit=j;{ty=j;te=\"true\";}}}unit=keep;{ty=g;}");
        var source = Source("a.ajs", string.Empty, "shift_jis");
        source.Base64 = Convert.ToBase64String(prefix.Concat(new byte[] { 0x87, 0x90 }).Concat(suffix).ToArray());
        Assert.Throws<DecoderFallbackException>(() => service.Validate([source]));
        source.Base64 = Convert.ToBase64String(prefix.Concat(new byte[] { 0x81, 0xe0 }).Concat(suffix).ToArray());
        var bundle = service.Validate([source]);
        var draft = DefinitionEditor.Project(bundle, 9);
        byte[] zip = DefinitionEditor.ExportGroup(bundle, new(draft, "/g"), service);
        using var archive = new ZipArchive(new MemoryStream(zip));
        using var entry = archive.GetEntry("sources/001/unit.ajs")!.Open();
        using var copy = new MemoryStream();
        entry.CopyTo(copy);
        Assert.True(copy.ToArray().AsSpan().IndexOf(new byte[] { 0x81, 0xe0 }) >= 0);
        draft.Networks.Single(n => n.Path == "/g/n").Nodes.Single().Attributes["te"] = ["echo changed"];
        byte[] changed = Convert.FromBase64String(DefinitionEditor.Render(bundle, draft)[0].Base64);
        Assert.True(changed.AsSpan().IndexOf(new byte[] { 0x81, 0xe0 }) >= 0);
    }

    /// <summary>グループ外の定義を追加した偽装ZIPを拒否します。</summary>
    [Fact]
    public void RejectsPackageContainingUnselectedUnits()
    {
        var service = Service();
        var source = Source("a.ajs", "unit=g;{ty=g;}unit=outside;{ty=g;}");
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using (var entry = archive.CreateEntry("a.ajs").Open())
            {
                entry.Write(Convert.FromBase64String(source.Base64));
            }

            using var manifest = archive.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(manifest, new { FormatVersion = 1, GroupRoot = "/g", Sources = new[] { new { File = "a.ajs", source.Mapping } } });
        }

        var bundle = service.Validate([source]);
        Assert.Throws<ArgumentException>(() => DefinitionEditor.ImportGroup(bundle, new(DefinitionEditor.Project(bundle, 9), "/g", Convert.ToBase64String(stream.ToArray())), service, new()));
    }

    /// <summary>大量の依存を持つ出力も読込側のmanifest上限内に収まります。</summary>
    /// <param name="unique">参照先を別々にするかです。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeDependenciesRoundTripWithinManifestLimit(bool unique)
    {
        var service = Service();
        string networks = string.Concat(Enumerable.Range(0, 2500).Select(i => $"unit=n{i};{{ty=n;sd=0,ud;jc=\"/external/{new string('x', 60)}{(unique ? i : 0)}\";}}"));
        var bundle = service.Validate([Source("large.ajs", "unit=g;{ty=g;" + networks + "}unit=keep;{ty=g;}")], true);
        var draft = DefinitionEditor.Project(bundle, 9);
        byte[] zip = DefinitionEditor.ExportGroup(bundle, new(draft, "/g"), service);
        using var archive = new ZipArchive(new MemoryStream(zip));
        Assert.InRange(archive.GetEntry("manifest.json")!.Length, 1, 256 * 1024);
        var imported = DefinitionEditor.ImportGroup(bundle, new(draft, string.Empty, Convert.ToBase64String(zip), "/", "copy"), service, new());
        var result = service.Validate(DefinitionEditor.Render(bundle, imported.Draft), true);
        Assert.Equal(2500, result.Units.Flatten().Count(u => u.Path.StartsWith("/copy/n", StringComparison.Ordinal)));
        if (unique)
        {
            Assert.Contains(imported.Diagnostics!, d => d.Contains("省略", StringComparison.Ordinal));
        }
    }

    /// <summary>ファイル対応だけでmanifest上限を超える入力は壊れたZIPを返しません。</summary>
    [Fact]
    public void RejectsOversizedSourceManifestBeforeReturningZip()
    {
        var service = Service();
        var sources = new List<SourceEvidence> { Source("main.ajs", "unit=g;{ty=g;}") };
        sources.AddRange(Enumerable.Range(0, 31).Select(i => Source(new string('い', 500) + i + ".ajs", "unit=n;{ty=n;sd=0,ud;}", "utf-8", "/g/" + new string('あ', 1000) + i)));
        var bundle = service.Validate(sources);
        Assert.Contains("manifest", Assert.Throws<ArgumentException>(() => DefinitionEditor.ExportGroup(bundle, new(DefinitionEditor.Project(bundle, 9), "/g"), service)).Message, StringComparison.Ordinal);
    }

    private static SourceEvidence Source(string name, string text, string encoding = "utf-8", string target = "/")
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return new() { Mapping = new() { BlobPath = name, Encoding = encoding, TargetPath = target }, Base64 = Convert.ToBase64String(Encoding.GetEncoding(encoding).GetBytes(text)) };
    }

    private static DefinitionService Service()
    {
        var options = new RunnerOptions();
        return new(new StorageRepository(options), options);
    }
}
