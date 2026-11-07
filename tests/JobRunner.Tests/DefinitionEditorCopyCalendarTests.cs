using System.Text;
using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>原文を複製する下書きと実行エンジンのカレンダー表示を検証します。</summary>
public sealed class DefinitionEditorCopyCalendarTests
{
    /// <summary>子孫・フォーム外の属性・内部コメントを複製し、外部接続だけを除外します。</summary>
    [Fact]
    public void CopiesRawSubtreeAndInternalEdgesWithOneReversibleDraft()
    {
        var options = new RunnerOptions();
        var definitions = new DefinitionService(new StorageRepository(options), options);
        const string raw = "unit=net;{ty=n;sd=0,ud;sz=3*1;el=a,j,+80 +48;el=nest,n,+240 +48;el=z,j,+400 +48;ar=(f=a,t=nest);ar=(f=nest,t=z);unit=a,0000,owner,group;{ty=j;te=\"true\";cm=\"keep\";sz=2*3;\n/* internal comment */\n}unit=nest;{ty=n;el=child,j,+80 +48;unit=child;{ty=j;te=\"true\";cm=\"inside\";}}unit=z;{ty=j;te=\"true\";}}";
        var bundle = definitions.Validate([Source("local/main.ajs", raw)]);
        var draft = DefinitionEditor.Project(bundle, 9);
        var result = DefinitionEditor.Paste(bundle, new(draft, draft, ["/net/a", "/net/nest"], "/net"), definitions, options);
        var rendered = DefinitionEditor.Render(bundle, result.Document.Draft);
        string text = Encoding.UTF8.GetString(Convert.FromBase64String(rendered[0].Base64));
        Assert.Contains("sz=2*3;\n/* internal comment */\n", text);
        Assert.Equal(2, text.Split("sz=2*3;\n/* internal comment */\n").Length - 1);
        Assert.Equal(2, text.Split("0000,owner,group").Length - 1);
        Assert.Contains(result.Warnings, w => w.Contains("外部接続", StringComparison.Ordinal) && w.Contains("/net/z", StringComparison.Ordinal));
        var parsed = definitions.Validate(rendered);
        Assert.Contains(parsed.Units.Flatten(), u => u.Path == "/net/nest_copy/child" && u.Get("cm") == "inside");
        var network = parsed.Units.Flatten().Single(u => u.Path == "/net");
        Assert.Equal("5*1", network.Get("sz"));
        Assert.Contains(JobRunner.Import.DefinitionValidator.Edges(network), e => e.From == "/net/a_copy" && e.To == "/net/nest_copy");
        Assert.DoesNotContain(JobRunner.Import.DefinitionValidator.Edges(network), e => e.From == "/net/nest_copy" && e.To == "/net/z");
        Assert.Equal(bundle.Sources[0].Base64, DefinitionEditor.Render(bundle, draft)[0].Base64);
        var changed = result.Document.Draft with { Networks = result.Document.Draft.Networks.Select(n => n.Path == "/net/nest_copy" ? n with { Nodes = n.Nodes.Select(node => node with { Attributes = new() { ["cm"] = ["edited"], ["te"] = ["true"] } }).ToList() } : n).ToList() };
        Assert.Contains(definitions.Validate(DefinitionEditor.Render(bundle, changed)).Units.Flatten(), u => u.Path == "/net/nest_copy/child" && u.Get("cm") == "edited");
        var second = DefinitionEditor.Paste(bundle, new(result.Document.Draft, draft, ["/net/a"], "/net"), definitions, options);
        Assert.Contains("/net/a_copy2", second.ClonedIds);
    }

    /// <summary>別ファイルの原文を保ち、文字コードに表現できないコピーは拒否します。</summary>
    [Fact]
    public void CopiesAcrossFilesAndRejectsEncodingLossAndHierarchyConversion()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var options = new RunnerOptions();
        var definitions = new DefinitionService(new StorageRepository(options), options);
        var source = Source("local/a.ajs", "unit=source;{ty=n;sd=0,ud;el=a,j,+80 +48;unit=a;{ty=j;te=\"true\";cm=\"日本語\";}}", "utf-8");
        var target = Source("local/b.ajs", "unit=target;{ty=n;sd=0,ud;}", "shift_jis");
        var bundle = definitions.Validate([source, target]);
        var draft = DefinitionEditor.Project(bundle, 9);
        var result = DefinitionEditor.Paste(bundle, new(draft, draft, ["/source/a"], "/target"), definitions, options);
        var rendered = DefinitionEditor.Render(bundle, result.Document.Draft);
        Assert.Equal(source.Base64, rendered.Single(s => s.Mapping.BlobPath == source.Mapping.BlobPath).Base64);
        Assert.Equal("shift_jis", rendered.Single(s => s.Mapping.BlobPath == target.Mapping.BlobPath).Mapping.Encoding);
        Assert.Contains(definitions.Validate(rendered).Units.Flatten(), u => u.Path == "/target/a_copy" && u.Get("cm") == "日本語");
        var emoji = draft with { Networks = draft.Networks.Select(n => n.Path == "/source" ? n with { Nodes = n.Nodes.Select(node => node with { Attributes = new() { ["te"] = ["true"], ["cm"] = ["😀"] } }).ToList() } : n).ToList() };
        Assert.Throws<EncoderFallbackException>(() => DefinitionEditor.Paste(bundle, new(draft, emoji, ["/source/a"], "/target"), definitions, options));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Paste(bundle, new(draft, draft, ["/source"], "/target"), definitions, options));
        var root = DefinitionEditor.Paste(bundle, new(draft, draft, ["/source"], SourceIndex: 1, ParentPath: "/"), definitions, options);
        var copiedRoot = definitions.Validate(DefinitionEditor.Render(bundle, root.Document.Draft)).Units.Flatten().Single(u => u.Path == "/source_copy");
        Assert.Equal("y", copiedRoot.Get("ha"));
        Assert.Equal("0,ud", copiedRoot.Get("sd"));
    }

    /// <summary>日付・曜日・親・参照の評価は実行と一致し、月境界を含みます。</summary>
    [Fact]
    public void CalendarMonthUsesEngineAndReportsInheritanceAndReference()
    {
        var options = new RunnerOptions();
        var definitions = new DefinitionService(new StorageRepository(options), options);
        var bundle = definitions.Validate([Source("local/calendar.ajs", "unit=parent;{ty=g;cl=su;cl=2028/2/29;unit=child;{ty=g;op=2028/2/29;}unit=net;{ty=n;sd=0,ud;jc=\"/parent/child\";}}")]);
        var draft = DefinitionEditor.Project(bundle, 9);
        var month = DefinitionEditor.CalendarMonth(bundle, new(draft, "/parent/net", new(2028, 2, 1)));
        Assert.Equal(29, month.Count);
        Assert.Equal(new(2028, 2, 29), month[^1].Date);
        Assert.True(month[^1].Evaluation.Working);
        Assert.Equal("/parent/child", month[^1].Evaluation.SourcePath);
        var sunday = month.Single(d => d.Date == new DateOnly(2028, 2, 6));
        Assert.False(sunday.Evaluation.Working);
        Assert.Equal("/parent", sunday.Evaluation.SourcePath);
        Assert.Equal("cl曜日", sunday.Evaluation.Rule);
        var removed = draft with { Calendars = draft.Calendars!.Select(c => c.Id == "/parent/child" ? c with { Attributes = [] } : c).ToList() };
        var compiled = definitions.Validate(DefinitionEditor.Render(bundle, removed));
        Assert.False(DefinitionEditor.CalendarMonth(compiled, new(removed, "/parent/net", new(2028, 2, 1)))[^1].Evaluation.Working);
        Assert.Throws<ArgumentException>(() => DefinitionEditor.CalendarMonth(bundle, new(draft, "/parent/net", new(2037, 1, 1))));
    }

    /// <summary>別グループを削除した下書きでも、残したネットのコピーと暦表示を行えます。</summary>
    [Fact]
    public void IgnoresDeletedPathsOutsideCopyAndCalendarTargets()
    {
        var options = new RunnerOptions();
        var definitions = new DefinitionService(new StorageRepository(options), options);
        var bundle = definitions.Validate([Source("local/main.ajs", "unit=gone;{ty=g;unit=net;{ty=n;sd=0,ud;}}unit=keep;{ty=g;cl=su;unit=net;{ty=n;sd=0,ud;el=j,j,+80 +48;unit=j;{ty=j;te=\"true\";}}}")]);
        var draft = DefinitionEditor.Project(bundle, 9) with { GroupChanges = [new("/gone", Delete: true)] };
        var compiled = definitions.Validate(DefinitionEditor.Render(bundle, draft));
        Assert.Equal(29, DefinitionEditor.CalendarMonth(compiled, new(draft, "/keep", new(2028, 2, 1))).Count);
        var copied = DefinitionEditor.Paste(bundle, new(draft, draft, ["/keep/net/j"], "/keep/net"), definitions, options);
        Assert.Contains("/keep/net/j_copy", copied.ClonedIds);
        Assert.DoesNotContain(definitions.Validate(DefinitionEditor.Render(bundle, copied.Document.Draft)).Units.Flatten(), u => u.Path.StartsWith("/gone", StringComparison.Ordinal));
    }

    private static SourceEvidence Source(string path, string text, string encoding = "utf-8") => new() { Mapping = new DefinitionSource { BlobPath = path, Encoding = encoding }, Base64 = Convert.ToBase64String(Encoding.GetEncoding(encoding, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(text)) };
}
