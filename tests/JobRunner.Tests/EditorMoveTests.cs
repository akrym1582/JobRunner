using System.Text;
using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>移動と外部依存の下書きを検証します。</summary>
public sealed class EditorMoveTests
{
    /// <summary>別ファイルのグループ移動で参照と独立ファイル配置を追従します。</summary>
    /// <param name="encoding">移動先文字コードです。</param>
    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("shift_jis")]
    public void MovesGroupAcrossFiles(string encoding)
    {
        var service = Service();
        var bundle = service.Validate([Source("a", "unit=a;{ty=g;unit=g;{ty=g;cl=su;unit=n;{ty=n;sd=0,ud;}}}unit=keep;{ty=n;sd=0,ud;jc=\"/a/g\";}"), Source("b", "unit=b;{ty=g;cl=sa;}", encoding), Source("c", "unit=child;{ty=n;sd=0,ud;}", "utf-8", "/a/g")]);
        var draft = DefinitionEditor.Project(bundle, 9);
        var moved = DefinitionEditor.Move(bundle, new(draft, "/a/g", "/b", 0, "b", "/", encoding), service, new());
        var result = service.Validate(DefinitionEditor.Render(bundle, moved.Draft));
        Assert.Contains(result.Units.Flatten(), u => u.Path == "/b/g/n");
        Assert.Contains(result.Units.Flatten(), u => u.Path == "/b/g/child");
        Assert.Equal("/b/g", result.Units.Flatten().Single(u => u.Path == "/keep").Get("jc"));
        Assert.Equal("/a/g", moved.Draft.GroupOrigins!["/b/g"]);
        Assert.Equal("/a/g/n", moved.Draft.RootOrigins!["/b/g/n"]);
        Assert.Equal(bundle.Sources[2].Base64, result.Sources.Single(s => s.Mapping.BlobPath == "c").Base64);
        Assert.Equal(bundle.Sources[0].Base64, DefinitionEditor.Render(bundle, draft)[0].Base64);
    }

    /// <summary>依存先を同梱せず、不足状態を保存して後から解決できます。</summary>
    [Fact]
    public void ImportsMissingCalendarAndResolvesLater()
    {
        var service = Service();
        var bundle = service.Validate([Source("a", "unit=calendar;{ty=g;cl=su;}unit=g;{ty=g;unit=n;{ty=n;sd=0,ud;jc=\"/calendar\";}}")]);
        string zip = Convert.ToBase64String(DefinitionEditor.ExportGroup(bundle, new(DefinitionEditor.Project(bundle, 9), "/g"), service));
        var target = service.Validate([Source("target", "unit=keep;{ty=g;}")]);
        var imported = DefinitionEditor.ImportGroup(target, new(DefinitionEditor.Project(target, 9), string.Empty, zip, "/keep", "imported"), service, new());
        var raw = DefinitionEditor.Render(target, imported.Draft);
        Assert.Throws<FormatException>(() => service.Validate(raw));
        var incomplete = service.Validate(raw, true);
        Assert.DoesNotContain(incomplete.Units.Flatten(), u => u.Path == "/calendar");
        Assert.Equal("/calendar", incomplete.Units.Flatten().Single(u => u.Path == "/keep/imported/n").Get("jc"));
        Assert.Contains(imported.Diagnostics!, d => d.Contains("参照先不足", StringComparison.Ordinal));
        Assert.NotEmpty(DefinitionEditor.ExportGroup(target, new(imported.Draft, "/keep/imported"), service));
        raw.Add(Source("calendar", "unit=calendar;{ty=g;cl=su;}"));
        Assert.NotEmpty(service.Validate(raw).Hash);
    }

    /// <summary>自己移動と空ファイルを拒否します。</summary>
    [Fact]
    public void RejectsCyclesAndEmptySource()
    {
        var service = Service();
        var bundle = service.Validate([Source("a", "unit=g;{ty=g;unit=n;{ty=n;sd=0,ud;}}"), Source("b", "unit=b;{ty=g;}")]);
        var draft = DefinitionEditor.Project(bundle, 9);
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Move(bundle, new(draft, "/g", "/g", 0), service, new()));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Move(bundle, new(draft, "/g", "/b", 1), service, new()));
    }

    /// <summary>末尾コメントを同じ行の属性へ変えず、移動ブロックを挿入します。</summary>
    [Fact]
    public void InsertsAfterTrailingCommentWithLineBoundary()
    {
        var service = Service();
        var bundle = service.Validate([Source("a", "unit=a;{ty=g;unit=n;{ty=n;sd=0,ud;}}"), Source("b", "unit=keep;{ty=g;}\n/* unchanged */")]);
        var moved = DefinitionEditor.Move(bundle, new(DefinitionEditor.Project(bundle, 9), "/a/n", "/", 1), service, new());
        var result = service.Validate(DefinitionEditor.Render(bundle, moved.Draft));
        Assert.Contains(result.Units.Flatten(), u => u.Path == "/n");
        Assert.Contains("/* unchanged */", Encoding.UTF8.GetString(Convert.FromBase64String(result.Sources.Single(s => s.Mapping.BlobPath == "b").Base64)), StringComparison.Ordinal);
    }

    /// <summary>継承先の変更で実際の稼働日が変わることを確認できます。</summary>
    [Fact]
    public void ReportsEffectiveCalendarDifferences()
    {
        var service = Service();
        var bundle = service.Validate([Source("a", "unit=a;{ty=g;cl=su;unit=n;{ty=n;sd=0,ud;}}"), Source("b", "unit=b;{ty=g;cl=sa;}")]);
        var moved = DefinitionEditor.Move(bundle, new(DefinitionEditor.Project(bundle, 9), "/a/n", "/b", 1), service, new());
        Assert.Contains(moved.Diagnostics!, d => d.Contains("稼働 → 休業", StringComparison.Ordinal));
        Assert.Contains(moved.Diagnostics!, d => d.Contains("休業 → 稼働", StringComparison.Ordinal));
    }

    private static SourceEvidence Source(string file, string text, string encoding = "utf-8", string parent = "/")
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return new() { Mapping = new() { BlobPath = file, Encoding = encoding, TargetPath = parent }, Base64 = Convert.ToBase64String(Encoding.GetEncoding(encoding).GetBytes(text)) };
    }

    private static DefinitionService Service() => new(new StorageRepository(new RunnerOptions { ConnectionString = "UseDevelopmentStorage=true" }), new RunnerOptions());
}
