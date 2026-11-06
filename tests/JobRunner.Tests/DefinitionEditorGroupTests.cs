using System.Text;
using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>グループ操作の参照・継承・ファイル境界と原文保持を確認します。</summary>
public sealed class DefinitionEditorGroupTests
{
    private const string Text = "/* /group 原文 */\r\nunit=group,0000,owner,team;{ty=g;cl=su;stt=06:00;sdd=20;md=ne;el=child,g,+80 +48;\r\nunit=child;{ty=g;unit=solo;{ty=j;te=\"echo /group/child\";cm=\"/group\";}unit=net;{ty=n;sd=1,1;st=1,07:00;cy=1,(1,d);}}}\r\nunit=keep;{ty=g;}\r\n";

    /// <summary>親子改名でjc/elと子孫のパスを更新し、継承による試算と元の表記を保ちます。</summary>
    /// <param name="encoding">文字コードです。</param>
    /// <param name="bom">BOMです。</param>
    [Theory]
    [InlineData("utf-8", false)]
    [InlineData("utf-8", true)]
    [InlineData("utf-16", true)]
    [InlineData("utf-16BE", true)]
    [InlineData("shift_jis", false)]
    public void RenamesParentAndChildPreservingCalendarAndSource(string encoding, bool bom)
    {
        var service = Service();
        var bundle = service.Validate([Source("a.ajs", Text, encoding, bom), Source("b.ajs", "unit=external;{ty=n;sd=1,1;st=1,07:00;cy=1,(1,d);jc=\"/group/child\";}")]);
        var draft = DefinitionEditor.Project(bundle, 7);
        Assert.Equal(bundle.Sources.Select(s => s.Base64), DefinitionEditor.Render(bundle, draft).Select(s => s.Base64));
        var before = Points(bundle, "/group/child/net");
        Assert.NotEmpty(before);
        draft.GroupChanges!.AddRange([new("/group", "renamed"), new("/group/child", "nested")]);
        var rendered = DefinitionEditor.Render(bundle, draft);
        string expected = Text.Replace("unit=group,", "unit=\"renamed\",", StringComparison.Ordinal).Replace("unit=child;", "unit=\"nested\";", StringComparison.Ordinal).Replace("el=child,g", "el=nested,g", StringComparison.Ordinal);
        Assert.Equal(Encoding.GetEncoding(encoding).GetBytes((bom ? "\uFEFF" : string.Empty) + expected), Convert.FromBase64String(rendered[0].Base64));
        Assert.Equal(bundle.Sources[0].Mapping, rendered[0].Mapping);
        var imported = service.Validate(rendered);
        Assert.Equal(before, Points(imported, "/renamed/nested/net"));
        Assert.Equal(Points(bundle, "/external"), Points(imported, "/external"));
        Assert.Equal("/renamed/nested", imported.Units.Flatten().Single(u => u.Path == "/external").Get("jc"));
        var preview = DefinitionEditor.PreviewSchedule(imported, new(draft, "/group/child/net", new(2026, 11, 1), new(2026, 11, 1), new(2026, 11, 10)), new());
        Assert.Equal(before, preview.Points.Select(p => p.Due));
        Assert.Equal(Encoding.GetEncoding(encoding).GetBytes((bom ? "\uFEFF" : string.Empty) + Text), Convert.FromBase64String(bundle.Sources[0].Base64));
    }

    /// <summary>子孫削除は親の表示参照だけ除去し、外部のjcがあれば拒否します。</summary>
    [Fact]
    public void DeletesSubtreeButRejectsSurvivingCalendarReference()
    {
        var service = Service();
        var bundle = service.Validate([Source("a.ajs", Text)]);
        var draft = DefinitionEditor.Project(bundle, 7);
        draft.GroupChanges!.Add(new("/group/child", Delete: true));
        var rendered = DefinitionEditor.Render(bundle, draft);
        var units = service.Validate(rendered).Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        Assert.Equal(new[] { "/group", "/keep" }, units.Keys.Order(StringComparer.Ordinal));
        Assert.False(units["/group"].Attributes.ContainsKey("el"));
        Assert.Equal("su", units["/group"].Get("cl"));
        Assert.Contains("/* /group 原文 */", Encoding.UTF8.GetString(Convert.FromBase64String(rendered[0].Base64)), StringComparison.Ordinal);
        bundle = service.Validate([Source("a.ajs", Text), Source("b.ajs", "unit=external;{ty=n;sd=0,ud;jc=\"/group/child\";}")]);
        draft = DefinitionEditor.Project(bundle, 7);
        draft.GroupChanges!.Add(new("/group", Delete: true));
        Assert.Contains("jc", Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft)).Message, StringComparison.Ordinal);
        draft.Schedules!.Single(s => s.Id == "/external").Attributes["jc"] = ["/keep"];
        Assert.DoesNotContain(service.Validate(DefinitionEditor.Render(bundle, draft)).Units.Flatten(), u => u.Path.StartsWith("/group", StringComparison.Ordinal));
    }

    /// <summary>独立ファイル、仮想親、空原文、名前再利用と不明な表示参照を安全に拒否します。</summary>
    [Fact]
    public void RejectsAmbiguousOrDestructiveBoundaries()
    {
        var service = Service();
        var bundle = service.Validate([Source("a.ajs", Text), Source("b.ajs", "unit=separate;{ty=g;}", target: "/group/child/independent")]);
        foreach (var change in new EditorGroupChange[] { new("/group", "new"), new("/group/child", Delete: true), new("/group/child/independent", "new"), new("/missing", "new") })
        {
            var draft = DefinitionEditor.Project(bundle, 7);
            draft.GroupChanges!.Add(change);
            Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
        }

        bundle = service.Validate([Source("a.ajs", Text)]);
        var rename = DefinitionEditor.Project(bundle, 7);
        rename.GroupChanges!.AddRange([new("/group", "keep"), new("/keep", Delete: true)]);
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, rename));
        rename.GroupChanges.Clear();
        rename.GroupChanges.Add(new("/group", new string('a', 1025)));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, rename));
        var empty = service.Validate([Source("a.ajs", "unit=only;{ty=g;}")]);
        var deletion = DefinitionEditor.Project(empty, 7);
        deletion.GroupChanges!.Add(new("/only", Delete: true));
        Assert.Contains("空", Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(empty, deletion)).Message, StringComparison.Ordinal);
        var malformed = service.Validate([Source("a.ajs", "unit=g;{ty=g;el=child;unit=child;{ty=g;}}")]);
        var invalid = DefinitionEditor.Project(malformed, 7);
        invalid.GroupChanges!.Add(new("/g/child", "new"));
        Assert.Contains("el", Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(malformed, invalid)).Message, StringComparison.Ordinal);
    }

    /// <summary>追加グループを改名し、改名後の明示参照と後続のネット編集を保持します。</summary>
    [Fact]
    public void CombinesPendingCreationsAndNetworkEdits()
    {
        var service = Service();
        var bundle = service.Validate([Source("a.ajs", Text)]);
        var draft = DefinitionEditor.Project(bundle, 7) with { LocalSources = bundle.Sources };
        draft.NewGroups!.Add(new(0, "/group", "added"));
        draft.Calendars!.Add(new("/group/added", new() { ["cl"] = ["sa"] }));
        draft.Execution!.Add(new("/group/added", []));
        draft.GroupChanges!.AddRange([new("/group", "renamed"), new("/group/added", "calendar")]);
        draft.Roots!.Single().Node.Attributes["cm"] = ["edited"];
        draft.Schedules!.Single().Attributes["jc"] = ["/renamed/calendar"];
        var result = service.Validate(DefinitionEditor.Render(bundle, draft));
        Assert.Equal("edited", result.Units.Flatten().Single(u => u.Type == "n").Get("cm"));
        Assert.Equal("/renamed/calendar", result.Units.Flatten().Single(u => u.Type == "n").Get("jc"));
        draft.NewFiles!.Add(new("new.ajs", "/group/added", "utf-8", false, "extra"));
        draft.Calendars.Add(new("/group/added/extra", []));
        draft.Execution.Add(new("/group/added/extra", []));
        Assert.Contains("配置先", Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft)).Message, StringComparison.Ordinal);
    }

    /// <summary>旧版要求では未知のグループ操作を適用せず、出力と試算の解決を揃えます。</summary>
    /// <param name="version">旧版の番号です。</param>
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void OldVersionsKeepGroupPaths(int version)
    {
        var bundle = Service().Validate([Source("a.ajs", Text)]);
        var draft = DefinitionEditor.Project(bundle, version) with { GroupChanges = [new("/group", "ignored")] };
        var rendered = DefinitionEditor.Render(bundle, draft);
        Assert.Equal(bundle.Sources[0].Base64, rendered[0].Base64);
        var preview = DefinitionEditor.PreviewSchedule(Service().Validate(rendered), new(draft, "/group/child/net", new(2026, 11, 1), new(2026, 11, 1), new(2026, 11, 10)), new());
        Assert.Equal(Points(bundle, "/group/child/net"), preview.Points.Select(p => p.Due));
    }

    private static List<DateTimeOffset> Points(DefinitionBundle bundle, string path)
    {
        var units = bundle.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        return ScheduleCalculator.Calculate(units[path], units, new() { Anchor = new(2026, 11, 1) }, new(2026, 11, 1), new(2026, 11, 10), new()).Select(p => p.Due).ToList();
    }

    private static SourceEvidence Source(string name, string text, string encoding = "utf-8", bool bom = false, string target = "/")
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return new() { Mapping = new() { BlobPath = name, Encoding = encoding, TargetPath = target }, Base64 = Convert.ToBase64String(Encoding.GetEncoding(encoding).GetBytes((bom ? "\uFEFF" : string.Empty) + text)) };
    }

    private static DefinitionService Service()
    {
        var options = new RunnerOptions();
        return new(new StorageRepository(options), options);
    }
}
