using System.Text;
using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>ネット階層を編集しても原文・参照・所属・旧契約を保つことを確認します。</summary>
public sealed class DefinitionEditorNetworkTests
{
    private const string Text = "/* 原文 */\r\nunit=group;{ty=g;el=net,n,+80 +48;op=mo;\r\nunit=net,0000,owner,group;{ty=n;sd=0,ud;jc=/calendar;el=sub,n,+80 +48;el=b,j,+240 +48;ar=(f=sub,t=b,seq);\r\nunit=sub;{ty=n;cm=\"残す\";el=a,j,+80 +48;unit=a;{ty=j;te=\"echo /group/net/sub\";}}\r\nunit=b;{ty=j;te=\"true\";}}}\r\n";

    /// <summary>ルートと子ネットの初期値・予定非生成とファイル配置を確認します。</summary>
    /// <param name="nested">子ネットを作るかどうかです。</param>
    /// <param name="type">ネット種別です。</param>
    [Theory]
    [InlineData(false, "n")]
    [InlineData(true, "n")]
    [InlineData(true, "rn")]
    public void CreatesNetworksInTheirSelectedSource(bool nested, string type)
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle);
        var node = new EditorNode("new:net", "追加", type, new() { ["cm"] = ["説明"] }, nested ? 400 : null, nested ? 48 : null);
        if (nested)
        {
            draft.Networks[0].Nodes.Add(node);
        }
        else
        {
            draft.Roots!.Add(new(node, 0, "/group"));
        }

        draft.Networks.Add(new(node.Id, [new("new:job", "処理", type == "rn" ? "rj" : "j", new() { ["te"] = ["true"] }, 80, 48)], []));
        var sources = DefinitionEditor.Render(bundle, draft);
        Assert.Equal(bundle.Sources[1].Base64, sources[1].Base64);
        var parsed = service.Validate(sources);
        var added = parsed.Units.Flatten().Single(n => n.Name == "追加");
        Assert.Equal(nested ? "/group/net/追加" : "/group/追加", added.Path);
        Assert.Equal(type, added.Type);
        Assert.Equal("処理", Assert.Single(added.Children).Name);
        if (!nested)
        {
            Assert.Equal("0,ud", added.Get("sd"));
            Assert.Equal("y", added.Get("ha"));
            Assert.Empty(ScheduleCalculator.Calculate(added, parsed.Units.Flatten().ToDictionary(n => n.Path), new Registration(), new(2026, 1, 1), new(2027, 1, 1), new RunnerOptions()));
        }
        else
        {
            Assert.False(added.Attributes.ContainsKey("sd"));
        }
    }

    /// <summary>親子の同時改名と子ジョブの編集が参照と原文を保ちます。</summary>
    /// <param name="encoding">原文文字コードです。</param>
    /// <param name="bom">BOMの有無です。</param>
    [Theory]
    [InlineData("utf-8", false)]
    [InlineData("utf-8", true)]
    [InlineData("utf-16", true)]
    [InlineData("shift_jis", false)]
    public void RenamesParentAndChildWithoutRewritingOtherSyntax(string encoding, bool bom)
    {
        var (service, bundle) = Create(encoding, bom);
        var draft = DefinitionEditor.Project(bundle);
        Assert.Equal(bundle.Sources.Select(s => s.Base64), DefinitionEditor.Render(bundle, draft).Select(s => s.Base64));
        draft.Roots![0] = draft.Roots[0] with { Node = draft.Roots[0].Node with { Name = "親" } };
        draft.Networks[0].Nodes[0] = draft.Networks[0].Nodes[0] with { Name = "子" };
        draft.Networks[1].Nodes[0].Attributes["te"] = ["echo changed"];
        var sources = DefinitionEditor.Render(bundle, draft);
        var parsed = service.Validate(sources);
        Assert.Contains(parsed.Units.Flatten(), n => n.Path == "/group/親/子/a" && n.Get("te") == "echo changed");
        string expected = Text.Replace("el=net,n", "el=親,n", StringComparison.Ordinal).Replace("unit=net,", "unit=\"親\",", StringComparison.Ordinal).Replace("el=sub,n", "el=子,n", StringComparison.Ordinal).Replace("f=sub,", "f=子,", StringComparison.Ordinal).Replace("unit=sub;", "unit=\"子\";", StringComparison.Ordinal).Replace("echo /group/net/sub", "echo changed", StringComparison.Ordinal);
        Assert.Equal(Encoding.GetEncoding(encoding).GetBytes((bom ? "\uFEFF" : string.Empty) + expected), Convert.FromBase64String(sources[0].Base64));
        Assert.Equal(bundle.Sources[1].Base64, sources[1].Base64);
        Assert.Equal(bundle.Sources.Select(s => s.Mapping.TargetPath), sources.Select(s => s.Mapping.TargetPath));
    }

    /// <summary>ネットを削除すると子孫と親の接続・座標だけを取り除きます。</summary>
    [Fact]
    public void DeletesSubtreeAndAllowsReplacingLastRoot()
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle);
        draft.Networks.RemoveAt(1);
        draft.Networks[0].Nodes.RemoveAt(0);
        draft.Networks[0].Edges.Clear();
        var parsed = service.Validate(DefinitionEditor.Render(bundle, draft));
        Assert.DoesNotContain(parsed.Units.Flatten(), n => n.Path.Contains("/sub", StringComparison.Ordinal));
        Assert.Equal("true", parsed.Units.Flatten().Single(n => n.Name == "b").Get("te"));
        draft.Networks.Clear();
        draft.Roots!.Clear();
        parsed = service.Validate(DefinitionEditor.Render(bundle, draft));
        Assert.DoesNotContain(parsed.Units.Flatten(), n => n.IsNetwork());
        Assert.Equal("mo", parsed.Units[0].Get("op"));
    }

    /// <summary>既存のファイル配置と階層移動、未接続の階層情報を拒否します。</summary>
    [Fact]
    public void RejectsMovesAndMalformedHierarchy()
    {
        var (_, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle);
        draft.Roots![0] = draft.Roots[0] with { SourceIndex = 1 };
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
        draft = DefinitionEditor.Project(bundle);
        var child = draft.Networks[0].Nodes[0];
        draft.Networks[0].Nodes.RemoveAt(0);
        draft.Roots!.Add(new(child with { X = null, Y = null }, 0, "/group"));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
        draft = DefinitionEditor.Project(bundle);
        draft.Networks.RemoveAt(1);
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
        draft = DefinitionEditor.Project(bundle);
        draft.Roots!.Add(new(new("new:recovery", "復旧", "rn", new(), null, null), 0, "/group"));
        draft.Networks.Add(new("new:recovery", [], []));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
    }

    /// <summary>旧版の投影を再出力してもネット属性や参照を失いません。</summary>
    /// <param name="version">旧投影版です。</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PreservesLegacyDraftContracts(int version)
    {
        var (_, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle, version);
        Assert.Null(draft.Roots);
        Assert.Equal(bundle.Sources.Select(s => s.Base64), DefinitionEditor.Render(bundle, draft).Select(s => s.Base64));
        draft.Networks[0].Nodes[0] = draft.Networks[0].Nodes[0] with { Name = "変更" };
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
    }

    /// <summary>兄弟ネットの名前交換でも子ジョブと座標・接続を混同しません。</summary>
    [Fact]
    public void SwapsNetworkNamesAndKeepsLiteralPaths()
    {
        string text = Text.Replace("unit=b;{ty=j;te=\"true\";}", "unit=b;{ty=n;unit=c;{ty=j;te=\"true\";}}", StringComparison.Ordinal).Replace("el=b,j", "el=b,n", StringComparison.Ordinal);
        var (service, bundle) = Create(sourceText: text);
        var draft = DefinitionEditor.Project(bundle);
        var parent = draft.Networks[0];
        parent.Nodes[0] = parent.Nodes[0] with { Name = "b" };
        parent.Nodes[1] = parent.Nodes[1] with { Name = "sub" };
        var parsed = service.Validate(DefinitionEditor.Render(bundle, draft));
        var units = parsed.Units.Flatten().ToDictionary(u => u.Path);
        Assert.Equal("echo /group/net/sub", units["/group/net/b/a"].Get("te"));
        Assert.Equal("true", units["/group/net/sub/c"].Get("te"));
        Assert.Equal((80, 48), DefinitionLayout.Read(units["/group/net"])["/group/net/b"]);
        Assert.Contains(DefinitionValidator.Edges(units["/group/net"]), e => e.From == "/group/net/b" && e.To == "/group/net/sub");
    }

    /// <summary>ネットとジョブの名前交換や削除名の再利用を、中間の重複で拒否しません。</summary>
    /// <param name="deleteJob">ジョブを削除してその名前を使うかどうかです。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenamesNetworkToRenamedOrDeletedJobName(bool deleteJob)
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle);
        var parent = draft.Networks[0];
        parent.Nodes[0] = parent.Nodes[0] with { Name = "b" };
        if (deleteJob)
        {
            parent.Nodes.RemoveAt(1);
            parent.Edges.Clear();
        }
        else
        {
            parent.Nodes[1] = parent.Nodes[1] with { Name = "sub" };
            parent.Nodes[1].Attributes["te"] = ["echo changed"];
        }

        var parsed = service.Validate(DefinitionEditor.Render(bundle, draft));
        var units = parsed.Units.Flatten().ToDictionary(u => u.Path);
        Assert.Equal("echo /group/net/sub", units["/group/net/b/a"].Get("te"));
        if (!deleteJob)
        {
            Assert.Equal("echo changed", units["/group/net/sub"].Get("te"));
            Assert.Contains(DefinitionValidator.Edges(units["/group/net"]), e => e.From == "/group/net/b" && e.To == "/group/net/sub");
        }
    }

    /// <summary>別入力の配置先を暗黙に移動・切り離しません。</summary>
    [Fact]
    public void RejectsRenamingOrDeletingExternalSourceParents()
    {
        var (service, bundle) = Create();
        bundle.Sources.Add(new() { Mapping = new() { BlobPath = "input/external", TargetPath = "/group/net" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=external;{ty=n;}")) });
        bundle = service.Validate(bundle.Sources);
        var draft = DefinitionEditor.Project(bundle);
        Assert.Equal(bundle.Sources.Select(s => s.Base64), DefinitionEditor.Render(bundle, draft).Select(s => s.Base64));
        Assert.DoesNotContain(DefinitionEditor.Containers(bundle), c => c.ParentPath == "/group/net");
        draft.Roots![0] = draft.Roots[0] with { Node = draft.Roots[0].Node with { Name = "変更" } };
        Assert.Contains("別原文の配置先", Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft)).Message, StringComparison.Ordinal);
        draft = DefinitionEditor.Project(bundle);
        draft.Roots!.RemoveAll(r => r.Node.Id == "/group/net");
        draft.Networks.RemoveAll(n => n.Path == "/group/net" || n.Path == "/group/net/sub");
        Assert.Contains("別原文の配置先", Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft)).Message, StringComparison.Ordinal);
    }

    /// <summary>マッピング直下への追加と最後のネットの置換を再取り込みできます。</summary>
    [Fact]
    public void AddsToMappedSourceAndRejectsEmptyFiles()
    {
        var (service, original) = Create();
        var source = new SourceEvidence { Mapping = new() { BlobPath = "input/only", TargetPath = "/team" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=only;{ty=n;sd=0,ud;}")) };
        var bundle = service.Validate([source]);
        var draft = DefinitionEditor.Project(bundle);
        draft.Roots!.Clear();
        draft.Networks.Clear();
        Assert.Throws<FormatException>(() => DefinitionEditor.Render(bundle, draft));
        draft.Roots.Add(new(new("new:replacement", "replacement", "n", new(), null, null), 0, "/team"));
        draft.Networks.Add(new("new:replacement", [], []));
        var rendered = DefinitionEditor.Render(bundle, draft);
        Assert.Equal("/team", rendered[0].Mapping.TargetPath);
        Assert.Contains(service.Validate(rendered).Units.Flatten(), n => n.Path == "/team/replacement");
        Assert.DoesNotContain(service.Validate(rendered).Units.Flatten(), n => n.Path == "/team/only");
        draft.Roots[0] = draft.Roots[0] with { ParentPath = "/other" };
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
        draft = DefinitionEditor.Project(original);
        draft.Roots![0].Node.Attributes["sd"] = ["1,en"];
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(original, draft));
    }

    private static (DefinitionService Service, DefinitionBundle Bundle) Create(string encoding = "utf-8", bool bom = false, string? sourceText = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var options = new RunnerOptions();
        var service = new DefinitionService(new StorageRepository(options), options);
        var sources = new List<SourceEvidence>
        {
            new() { Mapping = new() { BlobPath = "input/a.ajs", Encoding = encoding }, Base64 = Convert.ToBase64String(Encoding.GetEncoding(encoding).GetBytes((bom ? "\uFEFF" : string.Empty) + (sourceText ?? Text))) },
            new() { Mapping = new() { BlobPath = "input/z.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=calendar;{ty=g;op=mo;}")) },
        };
        return (service, service.Validate(sources));
    }
}
