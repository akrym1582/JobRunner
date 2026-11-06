using System.Text;
using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>実行属性の省略・旧版・境界と原文保持を検証します。</summary>
public sealed class DefinitionEditorExecutionTests
{
    private const string Text = "/* 保持 */\r\nunit=g;{ty=g;ex=\"local\";cl=su;unit=net,0000,owner,team;{ty=n;sd=0,ud;ha=y;mp=n;sy=1,09:00;ey=1,C60;fd=60;ex=\"local\";ar=(f=lead,t=judge);\r\nunit=job;{ty=j;cm=\"保持\";te=\"true\";ha=y;jd=cod;wth=1;tho=5;etm=15;ex=\"local\";}\r\nunit=sub;{ty=n;sy=1,M30;unit=recovery;{ty=rp;te=\"true\";}}unit=lead;{ty=j;te=\"true\";}unit=judge;{ty=jdj;ej=ef;ejf=\"/tmp/not-evaluated\";ha=y;}}}\r\n";

    /// <summary>無変更のバイト一致と、旧API要求で追加属性を消さないことを確認します。</summary>
    /// <param name="version">下書きの版です。</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void OldRequestsPreserveExecutionAttributes(int version)
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle, version);
        Assert.Equal(bundle.Sources[0].Base64, DefinitionEditor.Render(bundle, draft)[0].Base64);
        draft.Networks[0].Nodes[0].Attributes["te"] = ["echo changed"];
        var sources = DefinitionEditor.Render(bundle, draft);
        Assert.Equal(Encoding.UTF8.GetBytes(Text.Replace("te=\"true\";ha=y", "te=\"echo changed\";ha=y", StringComparison.Ordinal)), Convert.FromBase64String(sources[0].Base64));
        Assert.Equal("15", service.Validate(sources).Units.Flatten().Single(u => u.Name == "job").Get("etm"));
    }

    /// <summary>複数の実行属性だけを置換し、BOM・改行・ヘッダーと未編集属性を保持します。</summary>
    /// <param name="encodingName">原文文字コードです。</param>
    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    [InlineData("shift_jis")]
    public void EditsOnlyRequestedAttributesAndReimports(string encodingName)
    {
        var (service, bundle) = Create(encodingName, bom: encodingName != "shift_jis");
        var draft = DefinitionEditor.Project(bundle, 5);
        var job = Entry(draft, "/g/net/job");
        job["etm"] = ["1440"];
        job["tho"] = ["2147483647"];
        job["wth"] = ["2147483647"];
        job["jd"] = ["nm"];
        job["ha"] = ["n"];
        Entry(draft, "/g/net")["sy"] = ["144,47:59", "1,C1"];
        Entry(draft, "/g/net")["fd"] = ["2879"];
        Entry(draft, "/g/net")["mp"] = ["y"];
        Entry(draft, "/g")["ex"] = ["a#\";{}"];
        Entry(draft, "/g/net/judge")["ex"] = ["local"];
        var sources = DefinitionEditor.Render(bundle, draft);
        var units = service.Validate(sources).Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        Assert.Equal("1440", units["/g/net/job"].Get("etm"));
        Assert.Equal("y", units["/g/net/judge"].Get("ha"));
        Assert.Equal("a#\";{}", units["/g"].Get("ex"));
        Assert.Equal(["144,47:59", "1,C1"], units["/g/net"].Attributes["sy"]);
        string rendered = Encoding.GetEncoding(encodingName).GetString(Convert.FromBase64String(sources[0].Base64));
        if (encodingName != "shift_jis")
        {
            Assert.StartsWith("\uFEFF", rendered, StringComparison.Ordinal);
        }

        Assert.Contains("unit=net,0000,owner,team;", rendered, StringComparison.Ordinal);
        Assert.Contains("cm=\"保持\";te=\"true\";", rendered, StringComparison.Ordinal);
        Assert.Contains("unit=sub;{ty=n;sy=1,M30;unit=recovery;{ty=rp;te=\"true\";}}", rendered, StringComparison.Ordinal);
        Assert.Equal(sources[0].Base64, DefinitionEditor.Render(service.Validate(sources), DefinitionEditor.Project(service.Validate(sources), 5))[0].Base64);
    }

    /// <summary>省略による削除を明示値とは分け、親子の改名後にも元の対象へ適用します。</summary>
    [Fact]
    public void OmissionAndRenamingKeepStableTargets()
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle, 5);
        draft.Roots![0] = draft.Roots[0] with { Node = draft.Roots[0].Node with { Name = "renamed" } };
        draft.Networks[0].Nodes[0] = draft.Networks[0].Nodes[0] with { Name = "changed" };
        Entry(draft, "/g/net/job").Clear();
        Entry(draft, "/g/net").Remove("ha");
        Entry(draft, "/g/net/sub")["ey"] = ["144,U2879"];
        var units = service.Validate(DefinitionEditor.Render(bundle, draft)).Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        Assert.False(units["/g/renamed"].Attributes.ContainsKey("ha"));
        foreach (string key in new[] { "ha", "jd", "wth", "tho", "etm", "ex" })
        {
            Assert.False(units["/g/renamed/changed"].Attributes.ContainsKey(key));
        }

        Assert.Equal("144,U2879", units["/g/renamed/sub"].Get("ey"));
    }

    /// <summary>新規ルートの保留初期値を維持し、明示値と省略で解除できます。</summary>
    /// <param name="hold">明示する値、または省略です。</param>
    [Theory]
    [InlineData("y")]
    [InlineData("n")]
    [InlineData(null)]
    public void NewRootHoldingCanBeEdited(string? hold)
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle, 5);
        draft.Roots!.Add(new(new("new:root", "added", "n", [], null, null), 0, "/g"));
        draft.Networks.Add(new("new:root", [], []));
        draft.Schedules!.Add(new("new:root", new() { ["sd"] = ["0,ud"] }));
        draft.Execution!.Add(new("new:root", hold is null ? [] : new() { ["ha"] = [hold] }));
        var added = service.Validate(DefinitionEditor.Render(bundle, draft)).Units.Flatten().Single(u => u.Name == "added");
        Assert.Equal(hold ?? string.Empty, added.Get("ha"));
        Assert.Equal("0,ud", added.Get("sd"));
    }

    /// <summary>既存の意味検証と編集時の構文検証で境界外を拒否します。</summary>
    /// <param name="path">対象です。</param>
    /// <param name="key">属性です。</param>
    /// <param name="value">不正値です。</param>
    [Theory]
    [InlineData("/g/net/job", "etm", "0")]
    [InlineData("/g/net/job", "etm", "1441")]
    [InlineData("/g/net/job", "tho", "2147483648")]
    [InlineData("/g/net/job", "wth", "-1")]
    [InlineData("/g/net/job", "wth", "6")]
    [InlineData("/g/net/job", "jd", "mdf")]
    [InlineData("/g/net/job", "ha", "w")]
    [InlineData("/g/net/job", "etm", "1;ha=y")]
    [InlineData("/g/net", "mp", "maybe")]
    [InlineData("/g/net", "fd", "0")]
    [InlineData("/g/net", "fd", "2880")]
    [InlineData("/g/net", "sy", "0,09:00")]
    [InlineData("/g/net", "sy", "145,09:00")]
    [InlineData("/g/net", "sy", "1,48:00")]
    [InlineData("/g/net", "sy", "1,09:60")]
    [InlineData("/g/net", "sy", "1,M30")]
    [InlineData("/g/net", "ey", "1,U30")]
    [InlineData("/g/net/sub", "ey", "1,U0")]
    [InlineData("/g/net/sub", "ey", "1,C2880")]
    [InlineData("/g/net", "ex", "remote")]
    [InlineData("/g/net", "ex", "")]
    [InlineData("/g/net/judge", "ha", "n")]
    [InlineData("/g", "mp", "y")]
    public void RejectsInvalidValues(string path, string key, string value)
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle, 5);
        Entry(draft, path)[key] = [value];
        var error = Record.Exception(() => service.Validate(DefinitionEditor.Render(bundle, draft)));
        Assert.True(error is ArgumentException or FormatException, error?.ToString() ?? "不正値が受理されました。");
    }

    /// <summary>要求の欠落・重複・空配列で属性が意図せず消えないよう拒否します。</summary>
    [Fact]
    public void RejectsIncompleteOrDuplicateProjection()
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle, 5);
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft with { Execution = null }));
        draft.Execution!.Add(draft.Execution[0]);
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
        draft.Execution.RemoveAt(draft.Execution.Count - 1);
        Entry(draft, "/g/net/job")["ha"] = [];
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
        Entry(draft, "/g/net/job")["ha"] = ["y", "n"];
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
        Entry(draft, "/g/net/job")["ha"] = ["y"];
        Entry(draft, "/g/net")["sy"] = ["1,09:00", "1,C30"];
        Assert.Throws<FormatException>(() => service.Validate(DefinitionEditor.Render(bundle, draft)));
    }

    /// <summary>全通常・リカバリージョブで明示した最小値と異常終了判定を扱います。</summary>
    /// <param name="type">対象種別です。</param>
    [Theory]
    [InlineData("j")]
    [InlineData("pj")]
    [InlineData("rj")]
    [InlineData("rp")]
    public void SupportsEveryProcessJobType(string type)
    {
        var (service, original) = Create();
        original.Sources[0].Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Text.Replace("ty=j;cm", "ty=" + type + ";cm", StringComparison.Ordinal)));
        var bundle = service.Validate(original.Sources);
        var draft = DefinitionEditor.Project(bundle, 5);
        var values = Entry(draft, "/g/net/job");
        values["jd"] = ["ab"];
        values["wth"] = ["0"];
        values["tho"] = ["0"];
        values["etm"] = ["1"];
        Entry(draft, "/g/net")["fd"] = ["1"];
        var unit = service.Validate(DefinitionEditor.Render(bundle, draft)).Units.Flatten().Single(u => u.Name == "job");
        Assert.Equal(type, unit.Type);
        Assert.Equal("ab", unit.Get("jd"));
        Assert.Equal("1", unit.Get("etm"));
    }

    private static Dictionary<string, List<string>> Entry(EditorDraft draft, string id) => draft.Execution!.Single(e => e.Id == id).Attributes;

    private static (DefinitionService Service, DefinitionBundle Bundle) Create(string encodingName = "utf-8", bool bom = false)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var options = new RunnerOptions { LocalAgents = ["local", "a#\";{}"] };
        var service = new DefinitionService(new StorageRepository(options), options);
        var source = new SourceEvidence { Mapping = new() { BlobPath = "input/jobs", Encoding = encodingName }, Base64 = Convert.ToBase64String(Encoding.GetEncoding(encodingName).GetBytes((bom ? "\uFEFF" : string.Empty) + Text)) };
        return (service, service.Validate([source]));
    }
}
