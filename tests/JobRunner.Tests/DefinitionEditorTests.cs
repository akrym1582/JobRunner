using System.Text;
using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>実行モデルを再出力せず、原文に対する差分と既存検証を確認します。</summary>
public sealed class DefinitionEditorTests
{
    private const string Original = "/* 原文コメント */\nunit=net,0000,owner,group; {\n ty=n; sz=2*1; sd=0,ud;\n el=a,j,+80 +48; el=b,j,+240 +48; ar=(f=a,t=b);\n unit=a;{ty=j;te=\"echo a\";env=\"A=first\";cm=\"残す\";env=\"A=last\";wth=0;}\n /* 子のコメント */\n unit=b;{ty=j;sc=\"/bin/true\";prm=\"\";}\n}\n";

    /// <summary>無変更では文字コード・BOM・改行を含めバイト列を変えません。</summary>
    /// <param name="encodingName">保存文字コードです。</param>
    /// <param name="bom">BOMの有無です。</param>
    [Theory]
    [InlineData("utf-8", false)]
    [InlineData("utf-8", true)]
    [InlineData("utf-16", true)]
    [InlineData("shift_jis", false)]
    public void UnchangedExportPreservesEveryByte(string encodingName, bool bom)
    {
        var (service, bundle) = Create(Original.Replace("\n", "\r\n", StringComparison.Ordinal), encodingName, bom);
        var result = DefinitionEditor.Render(bundle, DefinitionEditor.Project(bundle));
        Assert.Equal(bundle.Sources[0].Base64, result[0].Base64);
        Assert.Equal(bundle.Hash, service.Validate(result).Hash);
    }

    /// <summary>編集した値だけを引用し、コメント・原文属性・所有者・CRLFを保持します。</summary>
    [Fact]
    public void RenameAndQuotedFieldsKeepUneditedSyntaxAndReferences()
    {
        var (service, bundle) = Create(Original.Replace("\n", "\r\n", StringComparison.Ordinal), "utf-8", true);
        var draft = DefinitionEditor.Project(bundle);
        var net = draft.Networks[0];
        var node = net.Nodes[0];
        var attributes = new Dictionary<string, List<string>>(node.Attributes) { ["te"] = ["printf \"日本語#;{}\""], ["env"] = ["A=last", "A=first", "B=日本語"] };
        net.Nodes[0] = node with { Name = "集計", Attributes = attributes, X = 560, Y = 144 };
        var sources = DefinitionEditor.Render(bundle, draft);
        string output = Encoding.UTF8.GetString(Convert.FromBase64String(sources[0].Base64));
        Assert.StartsWith("\uFEFF/* 原文コメント */\r\nunit=net,0000,owner,group;", output, StringComparison.Ordinal);
        Assert.Contains("/* 子のコメント */\r\n unit=b;{ty=j;sc=\"/bin/true\";prm=\"\";}", output, StringComparison.Ordinal);
        Assert.Contains("cm=\"残す\";", output, StringComparison.Ordinal);
        Assert.Contains("wth=0;", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", output.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        var parsed = service.Validate(sources).Units[0];
        Assert.Equal("printf \"日本語#;{}\"", parsed.Children[0].Get("te"));
        Assert.Equal(["A=last", "A=first", "B=日本語"], parsed.Children[0].Attributes["env"]);
        Assert.Equal("/net/集計", Assert.Single(DefinitionValidator.Edges(parsed)).From);
        Assert.Equal((560, 144), DefinitionLayout.Read(parsed)["/net/集計"]);
        Assert.Equal("4*2", parsed.Get("sz"));
    }

    /// <summary>追加・接続・削除で関連する構造だけを変更します。</summary>
    [Fact]
    public void AddAndDeleteJobsUpdateEdgesAndPositions()
    {
        var (service, bundle) = Create(Original);
        var draft = DefinitionEditor.Project(bundle);
        var net = draft.Networks[0];
        net.Nodes.RemoveAt(1);
        net.Nodes.Add(new EditorNode("new:one", "追加", "pj", new() { ["sc"] = ["/bin/true"] }, 400, 48));
        net.Edges.Clear();
        net.Edges.Add(new EditorEdge("/net/a", "new:one", false));
        var parsed = service.Validate(DefinitionEditor.Render(bundle, draft)).Units[0];
        Assert.Equal(["a", "追加"], parsed.Children.Select(u => u.Name));
        Assert.Equal("/net/追加", Assert.Single(DefinitionValidator.Edges(parsed)).To);
        Assert.Equal(2, DefinitionLayout.Read(parsed).Count);
        net.Nodes.Clear();
        net.Edges.Clear();
        Assert.Empty(service.Validate(DefinitionEditor.Render(bundle, draft)).Units[0].Children);
    }

    /// <summary>複数ファイルの配置とカレンダー参照を保ち、無関係の原文を変更しません。</summary>
    [Fact]
    public void CrossFileReferencesAndUntouchedBytesSurvive()
    {
        var (service, bundle) = Create(Original.Replace("sd=0,ud;", "sd=0,ud;jc=/calendar;", StringComparison.Ordinal), validate: false);
        bundle = service.Validate([bundle.Sources[0], Source("unit=calendar;{ty=g;op=mo;}", "shift_jis", false, "input/calendar")]);
        var draft = DefinitionEditor.Project(bundle);
        draft.Networks[0].Nodes[0].Attributes["te"] = ["printf changed"];
        var result = DefinitionEditor.Render(bundle, draft);
        Assert.Equal(bundle.Sources.Single(s => s.Mapping.BlobPath == "input/calendar").Base64, result.Single(s => s.Mapping.BlobPath == "input/calendar").Base64);
        Assert.Equal("/calendar", service.Validate(result).Units.Single(u => u.Path == "/net").Get("jc"));
    }

    /// <summary>表現できない文字、重複名、循環、条件接続の制約を拒否します。</summary>
    [Fact]
    public void RejectsInvalidDraftsAndUnencodableText()
    {
        var (service, bundle) = Create(Original, "shift_jis");
        var draft = DefinitionEditor.Project(bundle);
        draft.Networks[0].Nodes[0].Attributes["te"] = ["echo 😀"];
        Assert.Throws<EncoderFallbackException>(() => DefinitionEditor.Render(bundle, draft));
        draft = DefinitionEditor.Project(bundle);
        draft.Networks[0].Nodes[0] = draft.Networks[0].Nodes[0] with { Name = "b" };
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
        draft = DefinitionEditor.Project(bundle);
        draft.Networks[0].Edges.Add(new EditorEdge("/net/b", "/net/a", false));
        Assert.Throws<FormatException>(() => service.Validate(DefinitionEditor.Render(bundle, draft)));
        draft.Networks[0].Edges.RemoveAt(1);
        draft.Networks[0].Edges[0] = draft.Networks[0].Edges[0] with { Conditional = true };
        Assert.Throws<FormatException>(() => service.Validate(DefinitionEditor.Render(bundle, draft)));
    }

    /// <summary>座標欠落は実行検証と区別し、フォームに相当する変更で補修できます。</summary>
    [Fact]
    public void MissingCoordinatesCanBeRepairedWithoutAutomaticLayout()
    {
        var (service, bundle) = Create("unit=net;{ty=n;unit=a;{ty=j;te=\"true\";}}");
        var draft = DefinitionEditor.Project(bundle);
        Assert.Null(draft.Networks[0].Nodes[0].X);
        Assert.Throws<InvalidDataException>(() => DefinitionLayout.Read(bundle.Units[0]));
        draft.Networks[0].Nodes[0] = draft.Networks[0].Nodes[0] with { X = 80, Y = 48 };
        var parsed = service.Validate(DefinitionEditor.Render(bundle, draft));
        Assert.Equal((80, 48), DefinitionLayout.Read(parsed.Units[0])["/net/a"]);
    }

    /// <summary>値の前にある独立行コメントも置換範囲へ巻き込みません。</summary>
    [Fact]
    public void KeepsCommentsBetweenEqualsAndValue()
    {
        var (service, bundle) = Create("unit=net;{ty=n;el=a,j,+80 +48;unit=a;{ty=j;te=\n/* command note */\n\"true\";}}");
        var draft = DefinitionEditor.Project(bundle);
        draft.Networks[0].Nodes[0].Attributes["te"] = ["echo changed"];
        var result = DefinitionEditor.Render(bundle, draft);
        string text = Encoding.UTF8.GetString(Convert.FromBase64String(result[0].Base64));
        Assert.Contains("te=\n/* command note */\n\"echo changed\";", text, StringComparison.Ordinal);
        service.Validate(result);
    }

    /// <summary>通常ジョブへ戻るリカバリー接続と既存種別の変更を拒否します。</summary>
    [Fact]
    public void RejectsRecoveryBackEdgesAndTypeConversion()
    {
        var (service, bundle) = Create(Original.Replace("el=b,j", "el=b,rj", StringComparison.Ordinal).Replace("unit=b;{ty=j;", "unit=b;{ty=rj;", StringComparison.Ordinal));
        var draft = DefinitionEditor.Project(bundle);
        draft.Networks[0].Edges.Clear();
        draft.Networks[0].Edges.Add(new EditorEdge("/net/b", "/net/a", false));
        Assert.Throws<FormatException>(() => service.Validate(DefinitionEditor.Render(bundle, draft)));
        draft = DefinitionEditor.Project(bundle);
        draft.Networks[0].Nodes[0] = draft.Networks[0].Nodes[0] with { Type = "pj" };
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
    }

    /// <summary>二つの名前を交換しても、元IDに対応する座標と接続を維持します。</summary>
    [Fact]
    public void SwappingNamesDoesNotDeleteAnotherNodesCoordinates()
    {
        var (service, bundle) = Create(Original);
        var draft = DefinitionEditor.Project(bundle);
        var nodes = draft.Networks[0].Nodes;
        nodes[0] = nodes[0] with { Name = "b" };
        nodes[1] = nodes[1] with { Name = "a" };
        var parsed = service.Validate(DefinitionEditor.Render(bundle, draft)).Units[0];
        Assert.Equal((80, 48), DefinitionLayout.Read(parsed)["/net/b"]);
        Assert.Equal((240, 48), DefinitionLayout.Read(parsed)["/net/a"]);
        Assert.Equal(("/net/b", "/net/a", false), Assert.Single(DefinitionValidator.Edges(parsed)));
    }

    /// <summary>名前だけ変える場合、範囲内の非グリッド座標と既存szを補正しません。</summary>
    [Fact]
    public void RenameKeepsOriginalNonGridCoordinatesAndSize()
    {
        var (service, bundle) = Create(Original.Replace("+80 +48", "+16000 +10000", StringComparison.Ordinal));
        var draft = DefinitionEditor.Project(bundle);
        draft.Networks[0].Nodes[0] = draft.Networks[0].Nodes[0] with { Name = "changed" };
        var parsed = service.Validate(DefinitionEditor.Render(bundle, draft)).Units[0];
        Assert.Equal("2*1", parsed.Get("sz"));
        Assert.Equal((16000, 10000), DefinitionLayout.Read(parsed)["/net/changed"]);
        draft.Networks[0].Nodes.Add(new EditorNode("new:job", "追加", "j", new() { ["te"] = ["true"] }, 400, 48));
        parsed = service.Validate(DefinitionEditor.Render(bundle, draft)).Units[0];
        Assert.Equal("3*1", parsed.Get("sz"));
        Assert.Equal((16000, 10000), DefinitionLayout.Read(parsed)["/net/changed"]);
    }

    /// <summary>合法な端の座標はszのアイコン数上限とは別に保持します。</summary>
    /// <param name="x">水平座標です。</param>
    /// <param name="y">垂直座標です。</param>
    /// <param name="size">拡張後のアイコン数です。</param>
    [Theory]
    [InlineData(16000, 10000, "100*100")]
    [InlineData(15920, 9552, "100*100")]
    [InlineData(0, 0, "2*1")]
    [InlineData(16000, 48, "100*1")]
    [InlineData(80, 10000, "2*100")]
    public void MoveAcceptsCoordinateBoundariesWithoutOverflowingSize(int x, int y, string size)
    {
        var (service, bundle) = Create(Original);
        var draft = DefinitionEditor.Project(bundle);
        draft.Networks[0].Nodes[0] = draft.Networks[0].Nodes[0] with { X = x, Y = y };
        var parsed = service.Validate(DefinitionEditor.Render(bundle, draft)).Units[0];
        Assert.Equal((x, y), DefinitionLayout.Read(parsed)["/net/a"]);
        Assert.Equal(size, parsed.Get("sz"));
    }

    /// <summary>szの上限への丸めで不正な座標を受け入れません。</summary>
    /// <param name="x">水平座標です。</param>
    /// <param name="y">垂直座標です。</param>
    [Theory]
    [InlineData(-1, 48)]
    [InlineData(16001, 48)]
    [InlineData(80, -1)]
    [InlineData(80, 10001)]
    public void MoveRejectsCoordinatesOutsideBoundaries(int x, int y)
    {
        var (_, bundle) = Create(Original);
        var draft = DefinitionEditor.Project(bundle);
        draft.Networks[0].Nodes[0] = draft.Networks[0].Nodes[0] with { X = x, Y = y };
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
    }

    private static (DefinitionService Service, DefinitionBundle Bundle) Create(string text, string encoding = "utf-8", bool bom = false, bool validate = true)
    {
        var options = new RunnerOptions();
        var service = new DefinitionService(new StorageRepository(options), options);
        var source = Source(text, encoding, bom, "input/jobs");
        return (service, validate ? service.Validate([source]) : new DefinitionBundle { Sources = [source] });
    }

    private static SourceEvidence Source(string text, string encodingName, bool bom, string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(encodingName);
        return new SourceEvidence { Mapping = new DefinitionSource { BlobPath = path, Encoding = encodingName }, Base64 = Convert.ToBase64String(encoding.GetBytes((bom ? "\uFEFF" : string.Empty) + text)) };
    }
}
