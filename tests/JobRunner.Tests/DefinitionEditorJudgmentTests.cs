using System.Text;
using System.Text.Json;
using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>判定ジョブの原文編集と旧下書きの互換性を確認します。</summary>
public sealed class DefinitionEditorJudgmentTests
{
    private const string Original = "/* 残すコメント */\r\nunit=net;{ty=n;sd=0,ud;sz=3*1;el=a,j,+80 +48;el=judge,jdj,+240 +48;el=b,j,+400 +48;ar=(f=a,t=judge);ar=(f=judge,t=b,con);\r\nunit=a;{ty=j;te=\"true\";wth=0;}\r\nunit=judge,0000,owner,group;{ty=jdj;cm=\"判定\";ej=ri;ejl=1;ejh=9;ejm=gt;eju=le;ha=n;sz=1*1;}\r\nunit=b;{ty=j;te=\"true\";}}\r\n";

    /// <summary>判定の入力値を投影し、判定値以外の原文をそのまま残します。</summary>
    [Fact]
    public void ProjectsAndEditsJudgmentAttributesWithoutChangingOtherSyntax()
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle);
        var node = draft.Networks[0].Nodes[1];
        Assert.Equal("ri", node.Attributes["ej"][0]);
        node.Attributes["ejl"] = ["2"];
        var sources = DefinitionEditor.Render(bundle, draft);
        Assert.Equal(Original.Replace("ejl=1;", "ejl=2;", StringComparison.Ordinal), Encoding.UTF8.GetString(Convert.FromBase64String(sources[0].Base64)));
        Assert.Equal("2", service.Validate(sources).Units[0].Children[1].Get("ejl"));
    }

    /// <summary>判定ジョブを追加して通常・条件接続を定義できます。</summary>
    [Fact]
    public void AddsJudgmentWithNormalAndConditionalEdges()
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle);
        var network = draft.Networks[0];
        network.Nodes.Add(new EditorNode("new:judge", "追加判定", "jdj", new() { ["ej"] = ["eq"], ["ejc"] = ["0"] }, 560, 48));
        network.Edges.Add(new EditorEdge("/net/a", "new:judge", false));
        var parsed = service.Validate(DefinitionEditor.Render(bundle, draft));
        Assert.Equal("eq", parsed.Units[0].Children.Single(n => n.Name == "追加判定").Get("ej"));
    }

    /// <summary>各判定方式の属性を公式の引用規則で書き出し、既存検証で再解析します。</summary>
    /// <param name="kind">判定方式です。</param>
    [Theory]
    [InlineData("gt")]
    [InlineData("ge")]
    [InlineData("lt")]
    [InlineData("le")]
    [InlineData("eq")]
    [InlineData("ne")]
    [InlineData("ri")]
    [InlineData("ro")]
    [InlineData("ef")]
    [InlineData("nf")]
    [InlineData("vgt")]
    [InlineData("vge")]
    [InlineData("vlt")]
    [InlineData("vle")]
    [InlineData("veq")]
    [InlineData("vne")]
    [InlineData("vri")]
    [InlineData("vro")]
    [InlineData("sce")]
    [InlineData("spe")]
    [InlineData("sne")]
    [InlineData("spn")]
    [InlineData("snn")]
    [InlineData("snl")]
    public void RoundTripsEverySupportedCondition(string kind)
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle);
        var values = draft.Networks[0].Nodes[1].Attributes;
        values["ej"] = [kind];
        if (kind is "ef" or "nf")
        {
            values["ejf"] = [Path.Combine(Path.GetTempPath(), "日本語#判定")];
        }
        else if (kind.StartsWith('v') || kind.StartsWith('s'))
        {
            values["ejv"] = ["?AJS2RESULT?"];
            values["eji"] = ["2147483647"];
            values["ejs"] = ["1"];
            values["ejg"] = ["2147483647"];
            values["ejt"] = ["日本語\"#;{}"];
        }
        else
        {
            values["ejc"] = ["4294967295"];
        }

        var sources = DefinitionEditor.Render(bundle, draft);
        var parsed = service.Validate(sources).Units[0].Children[1];
        foreach (var (key, value) in values)
        {
            Assert.Equal(value, parsed.Attributes[key]);
        }

        Assert.Contains($"ej={kind};", Encoding.UTF8.GetString(Convert.FromBase64String(sources[0].Base64)), StringComparison.Ordinal);
        values["ej"] = ["sce"];
        values["ejv"] = ["?AJS2RESULT?"];
        values["ejt"] = [string.Empty];
        Assert.Equal(string.Empty, service.Validate(DefinitionEditor.Render(bundle, draft)).Units[0].Children[1].Get("ejt"));
    }

    /// <summary>判定編集でも原文の文字コード、未変更部分、BOMと改行を保ちます。</summary>
    /// <param name="encoding">原文の文字コードです。</param>
    /// <param name="bom">BOMの有無です。</param>
    [Theory]
    [InlineData("utf-8", false)]
    [InlineData("utf-8", true)]
    [InlineData("utf-16", true)]
    [InlineData("shift_jis", false)]
    public void PreservesEncodingAndUntouchedBytes(string encoding, bool bom)
    {
        var (service, bundle) = Create(encoding, bom);
        var draft = DefinitionEditor.Project(bundle);
        Assert.Equal(bundle.Sources[0].Base64, DefinitionEditor.Render(bundle, draft)[0].Base64);
        draft.Networks[0].Nodes[1].Attributes["cm"] = ["新しい判定"];
        var source = DefinitionEditor.Render(bundle, draft)[0];
        Assert.Equal(Encoding.GetEncoding(encoding).GetBytes((bom ? "\uFEFF" : string.Empty) + Original.Replace("判定\"", "新しい判定\"", StringComparison.Ordinal)), Convert.FromBase64String(source.Base64));
        service.Validate([source]);
        if (encoding == "shift_jis")
        {
            draft.Networks[0].Nodes[1].Attributes["cm"] = ["😀"];
            Assert.Throws<EncoderFallbackException>(() => DefinitionEditor.Render(bundle, draft));
        }
    }

    /// <summary>名前交換・削除で条件接続と座標を対応するノードに維持します。</summary>
    [Fact]
    public void RenamesAndDeletesJudgmentWithReferences()
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle);
        var network = draft.Networks[0];
        network.Nodes[1] = network.Nodes[1] with { Name = "b" };
        network.Nodes[2] = network.Nodes[2] with { Name = "judge" };
        var parsed = service.Validate(DefinitionEditor.Render(bundle, draft)).Units[0];
        Assert.Contains(DefinitionValidator.Edges(parsed), e => e.From == "/net/b" && e.To == "/net/judge" && e.Conditional);
        Assert.Equal((240, 48), DefinitionLayout.Read(parsed)["/net/b"]);
        network.Nodes.RemoveAt(1);
        network.Edges.Clear();
        parsed = service.Validate(DefinitionEditor.Render(bundle, draft)).Units[0];
        Assert.DoesNotContain(parsed.Children, n => n.IsJudgment());
        Assert.Equal(2, DefinitionLayout.Read(parsed).Count);
    }

    /// <summary>リカバリー判定も追加でき、通常後続へ戻る接続は拒否します。</summary>
    [Fact]
    public void AddsRecoveryJudgmentAndRejectsInvalidConnections()
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle);
        var network = draft.Networks[0];
        network.Nodes.Add(new EditorNode("new:recovery", "復旧", "rj", new() { ["te"] = ["true"] }, 560, 48));
        network.Nodes.Add(new EditorNode("new:condition", "復旧判定", "rjdj", new() { ["ej"] = ["eq"], ["ejc"] = ["0"] }, 720, 48));
        network.Nodes.Add(new EditorNode("new:dependent", "復旧後続", "rp", new() { ["te"] = ["true"] }, 880, 48));
        network.Edges.AddRange([new("/net/a", "new:recovery", false), new("new:recovery", "new:condition", false), new("new:condition", "new:dependent", true)]);
        var parsed = service.Validate(DefinitionEditor.Render(bundle, draft));
        Assert.Contains(parsed.Units[0].Children, n => n.Type == "rjdj");
        network.Edges.Add(new("new:condition", "/net/b", false));
        Assert.Throws<FormatException>(() => service.Validate(DefinitionEditor.Render(bundle, draft)));
        network.Edges.RemoveAt(network.Edges.Count - 1);
        network.Edges.Add(new("/net/b", "/net/judge", false));
        Assert.Throws<FormatException>(() => service.Validate(DefinitionEditor.Render(bundle, draft)));
    }

    /// <summary>旧JSONの欠落した判定属性を削除指定として扱いません。</summary>
    [Fact]
    public void LegacyDraftKeepsJudgmentAttributesAndRestrictsItsEdits()
    {
        var (service, bundle) = Create();
        var old = DefinitionEditor.Project(bundle, 1);
        var draft = JsonSerializer.Deserialize<EditorDraft>(JsonSerializer.Serialize(new { old.DefinitionHash, old.Networks }))!;
        Assert.Null(draft.EditorVersion);
        Assert.Equal(bundle.Sources[0].Base64, DefinitionEditor.Render(bundle, draft)[0].Base64);
        draft.Networks[0].Nodes[0].Attributes["te"] = ["echo legacy"];
        var parsed = service.Validate(DefinitionEditor.Render(bundle, draft));
        Assert.Equal("gt", parsed.Units[0].Children[1].Get("ejm"));
        draft.Networks[0].Nodes[1].Attributes["ej"] = ["eq"];
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft with { EditorVersion = 4 }));
    }

    /// <summary>範囲外値、成立しない範囲、構文注入と種別外属性を拒否します。</summary>
    [Fact]
    public void RejectsInvalidValuesAndSyntaxInjection()
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle);
        var values = draft.Networks[0].Nodes[1].Attributes;
        values["ej"] = ["eq"];
        values["ejc"] = ["4294967296"];
        Assert.Throws<FormatException>(() => service.Validate(DefinitionEditor.Render(bundle, draft)));
        values.Remove("ejc");
        values["ej"] = ["ri"];
        values["ejh"] = ["1"];
        Assert.Throws<FormatException>(() => service.Validate(DefinitionEditor.Render(bundle, draft)));
        values["ej"] = ["eq;ha=y"];
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
        values["ej"] = ["eq"];
        values["te"] = ["echo injected"];
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
    }

    /// <summary>使用しない既存の条件属性は正規化も削除もせず、変更時だけ入力を検証します。</summary>
    [Fact]
    public void KeepsInactiveOriginalAttributesWithoutReinterpretingThem()
    {
        string text = Original.Replace("ej=ri;", "ej=ri;ejv=\"old variable\";eji=\"old;value\";", StringComparison.Ordinal);
        var (service, bundle) = Create(sourceText: text);
        var draft = DefinitionEditor.Project(bundle);
        Assert.Equal(bundle.Sources[0].Base64, DefinitionEditor.Render(bundle, draft)[0].Base64);
        var values = draft.Networks[0].Nodes[1].Attributes;
        values["cm"] = ["更新"];
        var sources = DefinitionEditor.Render(bundle, draft);
        Assert.Equal(text.Replace("cm=\"判定\"", "cm=\"更新\"", StringComparison.Ordinal), Encoding.UTF8.GetString(Convert.FromBase64String(sources[0].Base64)));
        service.Validate(sources);
        values["ej"] = ["veq"];
        Assert.Throws<FormatException>(() => service.Validate(DefinitionEditor.Render(bundle, draft)));
        values["ej"] = ["ri"];
        values["eji"] = ["changed;ha=y"];
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
    }

    private static (DefinitionService Service, DefinitionBundle Bundle) Create(string encoding = "utf-8", bool bom = false, string? sourceText = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var options = new RunnerOptions();
        var service = new DefinitionService(new StorageRepository(options), options);
        var source = new SourceEvidence { Mapping = new DefinitionSource { BlobPath = "input/judgment.ajs", Encoding = encoding }, Base64 = Convert.ToBase64String(Encoding.GetEncoding(encoding).GetBytes((bom ? "\uFEFF" : string.Empty) + (sourceText ?? Original))) };
        return (service, service.Validate([source]));
    }
}
