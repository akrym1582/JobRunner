using System.Text;
using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>追加の配置・原文・文字コードと既存編集との併用を検証します。</summary>
public sealed class DefinitionEditorCreationTests
{
    /// <summary>元バイトを保ち、親子グループ・予定・保留ルートを追加して再取込できます。</summary>
    /// <param name="encoding">元文字コードです。</param>
    /// <param name="bom">BOMの指定です。</param>
    [Theory]
    [InlineData("utf-8", false)]
    [InlineData("utf-8", true)]
    [InlineData("utf-16", true)]
    [InlineData("utf-16BE", true)]
    [InlineData("shift_jis", false)]
    public void AddsNestedGroupsAndSafeRootPreservingOriginal(string encoding, bool bom)
    {
        var service = Service();
        var source = DefinitionEditor.CreateFile(new("base.ajs", "/", encoding, bom, "original"));
        string text = (bom ? "\uFEFF" : string.Empty) + "/* 保持 */\r\nunit=original,0000,owner,team;{ty=g;cl=su;unit=solo;{ty=j;te=\"true\";}}\r\n";
        source.Base64 = Convert.ToBase64String(Encoding.GetEncoding(encoding).GetBytes(text));
        var bundle = service.Validate([source]);
        var draft = DefinitionEditor.Project(bundle, 6);
        Assert.Equal(source.Base64, DefinitionEditor.Render(bundle, draft)[0].Base64);
        Add(draft, new(0, "/original", "追加"));
        Add(draft, new(0, "/original/追加", "child"));
        draft.Calendars!.Single(c => c.Id == "/original/追加").Attributes["cl"] = ["sa"];
        draft.Roots!.Add(new(new("new:root", "net", "n", [], null, null), 0, "/original/追加/child"));
        draft.Networks.Add(new("new:root", [], []));
        draft.Schedules!.Add(new("new:root", new() { ["sd"] = ["0,ud"] }));
        draft.Execution!.Add(new("new:root", new() { ["ha"] = ["y"] }));
        var rendered = DefinitionEditor.Render(bundle, draft);
        string output = Encoding.GetEncoding(encoding).GetString(Convert.FromBase64String(rendered[0].Base64));
        Assert.StartsWith(text[..text.LastIndexOf('}')], output, StringComparison.Ordinal);
        Assert.Equal(source.Base64, bundle.Sources[0].Base64);
        Assert.Equal(source.Mapping, rendered[0].Mapping);
        var units = service.Validate(rendered).Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        Assert.Equal("sa", units["/original/追加"].Get("cl"));
        Assert.Equal("y", units["/original/追加/child/net"].Get("ha"));
        Assert.Equal("0,ud", units["/original/追加/child/net"].Get("sd"));
        Assert.Equal("true", units["/original/solo"].Get("te"));
    }

    /// <summary>追加ファイルは末尾で対応付け、既存ファイルをそのまま保持します。</summary>
    [Fact]
    public void AppendsFilesAndPreservesReferencesAndSourceOrder()
    {
        var service = Service();
        var bundle = service.Validate([DefinitionEditor.CreateFile(new("z.ajs", "/", "utf-8", true, "base"))]);
        var draft = DefinitionEditor.Project(bundle, 6) with { LocalSources = bundle.Sources };
        Add(draft, new(0, "/base", "calendar"));
        draft.Calendars!.Single(c => c.Id == "/base/calendar").Attributes["cl"] = ["su"];
        draft.NewFiles!.Add(new("a.ajs", "/", "utf-16BE", true, "extra"));
        draft.Calendars!.Add(new("/extra", []));
        draft.Execution!.Add(new("/extra", []));
        Add(draft, new(1, "/extra", "child"));
        draft.Roots!.Add(new(new("new:net", "net", "n", [], null, null), 1, "/extra/child"));
        draft.Networks.Add(new("new:net", [], []));
        draft.Schedules!.Add(new("new:net", new() { ["sd"] = ["0,ud"], ["jc"] = ["/base/calendar"] }));
        draft.Execution.Add(new("new:net", new() { ["ha"] = ["y"] }));
        var rendered = DefinitionEditor.Render(bundle, draft);
        Assert.Equal(["z.ajs", "a.ajs"], rendered.Select(s => s.Mapping.BlobPath));
        Assert.Equal("utf-16BE", rendered[1].Mapping.Encoding);
        Assert.StartsWith("\uFEFF", Encoding.BigEndianUnicode.GetString(Convert.FromBase64String(rendered[1].Base64)), StringComparison.Ordinal);
        Assert.Equal("/base/calendar", service.Validate(rendered).Units.Flatten().Single(u => u.Name == "net").Get("jc"));
        Assert.Equal(0, Assert.Single(DefinitionEditor.Project(service.Validate(rendered), 6).Roots!).SourceIndex);
        Assert.True(DefinitionEditor.Describe(rendered, bundle.Sources)[1].Changed);
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft with { LocalSources = null }));
    }

    /// <summary>危険なパス、BOM、文字コード、名前をファイル作成時に拒否します。</summary>
    /// <param name="name">ファイル名です。</param>
    /// <param name="target">配置先です。</param>
    /// <param name="encoding">文字コードです。</param>
    /// <param name="bom">BOMです。</param>
    /// <param name="group">グループ名です。</param>
    [Theory]
    [InlineData("../bad.ajs", "/", "utf-8", false, "g")]
    [InlineData("/bad.ajs", "/", "utf-8", false, "g")]
    [InlineData("a.zip", "/", "utf-8", false, "g")]
    [InlineData("a.ajs", "/a/../b", "utf-8", false, "g")]
    [InlineData("a.ajs", "/", "shift_jis", true, "g")]
    [InlineData("a.ajs", "/", "utf-32", false, "g")]
    [InlineData("a.ajs", "/", "shift_jis", false, "😀")]
    [InlineData("a.ajs", "/", "utf-8", false, "a;ty=j")]
    public void RejectsInvalidNewFiles(string name, string target, string encoding, bool bom, string group)
    {
        var error = Record.Exception(() => DefinitionEditor.CreateFile(new(name, target, encoding, bom, group)));
        Assert.True(error is ArgumentException or FormatException, error?.ToString() ?? "不正入力が受理されました。");
    }

    /// <summary>ネット・他ファイル内・仮想グループの横取りや重複を拒否します。</summary>
    [Fact]
    public void RejectsUnsafeGroupPlacementAndDuplicates()
    {
        var service = Service();
        var source = DefinitionEditor.CreateFile(new("base.ajs", "/", "utf-8", false, "g"));
        source.Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=g;{ty=g;unit=net;{ty=n;sd=0,ud;}}"));
        var bundle = service.Validate([source, DefinitionEditor.CreateFile(new("other.ajs", "/virtual", "utf-8", false, "other"))]);
        foreach (var group in new EditorGroupCreation[] { new(0, "/g/net", "child"), new(0, "/missing", "child"), new(0, "/virtual/other", "child"), new(0, "/", "virtual"), new(0, "/", "g"), new(9, "/", "child") })
        {
            var draft = DefinitionEditor.Project(bundle, 6);
            Add(draft, group);
            Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
        }

        var local = DefinitionEditor.Project(bundle, 6) with { LocalSources = bundle.Sources };
        local.NewFiles!.Add(new("BASE.AJS", "/", "utf-8", false, "new"));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, local));
        local.NewFiles.Clear();
        for (int i = 0; i < 65; i++)
        {
            Add(local, new(0, "/g" + string.Concat(Enumerable.Repeat("/a", i)), "a"));
        }

        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, local));
    }

    /// <summary>ファイル・グループ件数の上限を超える追加を解析前に拒否します。</summary>
    [Fact]
    public void RejectsCreationCountLimits()
    {
        Assert.Throws<ArgumentException>(() => DefinitionEditor.CreateFile(null!));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.CreateFile(new(null!, "/", "utf-8", false, "g")));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.CreateFile(new("name.ajs", "/", null!, false, "g")));
        var sources = Enumerable.Range(0, 32).Select(i => DefinitionEditor.CreateFile(new($"{i:D2}.ajs", "/", "utf-8", false, "g" + i))).ToList();
        var bundle = Service().Validate(sources);
        var draft = DefinitionEditor.Project(bundle, 6) with { LocalSources = bundle.Sources };
        draft.NewFiles!.Add(new("overflow.ajs", "/", "utf-8", false, "overflow"));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
        draft.NewFiles.Clear();
        draft.NewGroups!.Add(null!);
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
        draft.NewGroups.Clear();
        draft.NewGroups.Add(new(0, null!, "g"));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
        draft.NewGroups.Clear();
        for (int i = 0; i <= 1000; i++)
        {
            Add(draft, new(0, "/g0", "child" + i));
        }

        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
    }

    private static void Add(EditorDraft draft, EditorGroupCreation group)
    {
        draft.NewGroups!.Add(group);
        string path = group.ParentPath.TrimEnd('/') + "/" + group.Name;
        draft.Calendars!.Add(new(path, []));
        draft.Execution!.Add(new(path, []));
    }

    private static DefinitionService Service()
    {
        var options = new RunnerOptions();
        return new(new StorageRepository(options), options);
    }
}
