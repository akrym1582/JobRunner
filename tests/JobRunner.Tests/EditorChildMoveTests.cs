using System.Text;
using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>接続を含む選択移動の原文保持と拒否を検証します。</summary>
public sealed class EditorChildMoveTests
{
    /// <summary>通常・条件接続、子孫、外部参照、座標を別ファイルへ運びます。</summary>
    /// <param name="encoding">移動先文字コードです。</param>
    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("shift_jis")]
    public void MovesSelectionWithInternalEdgesAndNestedFiles(string encoding)
    {
        var service = Service();
        const string text = "unit=a;{ty=n;sd=0,ud;ar=(f=pre,t=j,seq);ar=(f=j,t=judge,seq);ar=(f=judge,t=n,con);ar=(f=n,t=post,seq);el=j,j,+16000 +10000;unit=pre;{ty=j;te=\"true\";}unit=j;{ty=j;te=\"echo 日本語\";cm=\"keep\";}unit=judge;{ty=jdj;ej=eq;ejc=0;}unit=n;{ty=n;de=y;jc=\"/calendar\";}unit=post;{ty=j;te=\"true\";}}unit=calendar;{ty=g;cl=su;}";
        var bundle = service.Validate([Source("a", text), Source("b", "/* unchanged */\nunit=b;{ty=n;sd=0,ud;el=keep,j,+16000 +10000;unit=keep;{ty=j;te=\"true\";}}", encoding), Source("nested", "unit=inner;{ty=n;de=y;}", "utf-8", "/a/n")]);
        var original = DefinitionEditor.Project(bundle, 9);
        var moved = DefinitionEditor.MoveSelection(bundle, new(original, ["/a/n", "/a/judge", "/a/j"], "/b"), service, new());
        var result = service.Validate(DefinitionEditor.Render(bundle, moved.Draft));
        var units = result.Units.Flatten().ToDictionary(u => u.Path);
        Assert.DoesNotContain("/a/j", units.Keys);
        Assert.Contains("/b/n/inner", units.Keys);
        Assert.Equal("/calendar", units["/b/n"].Get("jc"));
        Assert.Equal("keep", units["/b/j"].Get("cm"));
        Assert.Equal(new[] { "(f=j,t=judge,seq)", "(f=judge,t=n,con)" }, units["/b"].Attributes["ar"]);
        Assert.Empty(units["/a"].Attributes.GetValueOrDefault("ar") ?? []);
        Assert.Equal("j,j,+16000 +10000", units["/b"].Attributes["el"][1]);
        Assert.Equal(bundle.Sources[2].Base64, result.Sources.Single(s => s.Mapping.BlobPath == "nested").Base64);
        Assert.Equal("/b/n", result.Sources.Single(s => s.Mapping.BlobPath == "nested").Mapping.TargetPath);
        Assert.Equal("/a", moved.Draft.RootOrigins!["/a"]);
        Assert.Equal("/b", moved.Draft.RootOrigins["/b"]);
        Assert.Equal(2, moved.Diagnostics!.Count(d => d.StartsWith("外部接続を切断", StringComparison.Ordinal)));
        Assert.Contains(moved.Diagnostics!, d => d.StartsWith("座標重複", StringComparison.Ordinal));
        Assert.Contains(moved.Diagnostics!, d => d.Contains("/calendar", StringComparison.Ordinal));
        Assert.Equal(bundle.Sources.Select(s => s.Base64), DefinitionEditor.Render(bundle, original).Select(s => s.Base64));
        var again = DefinitionEditor.MoveSelection(bundle, new(moved.Draft, ["/b/j", "/b/judge", "/b/n"], "/a"), service, new());
        Assert.Contains(service.Validate(DefinitionEditor.Render(bundle, again.Draft)).Units.Flatten(), u => u.Path == "/a/n/inner");
    }

    /// <summary>同一ファイルで通常・リカバリー・判定ユニットの種別を変えません。</summary>
    /// <param name="type">移動種別です。</param>
    [Theory]
    [InlineData("j")]
    [InlineData("pj")]
    [InlineData("rj")]
    [InlineData("rp")]
    [InlineData("n")]
    [InlineData("rn")]
    public void PreservesTypeWithoutLayout(string type)
    {
        var service = Service();
        string attributes = type is "j" or "pj" or "rj" or "rp" ? "te=\"true\";" : "de=y;";
        var bundle = service.Validate([Source("a", $"unit=a;{{ty=n;sd=0,ud;unit=x;{{ty={type};{attributes}}}}}unit=b;{{ty=n;sd=0,ud;}}")]);
        var moved = DefinitionEditor.MoveSelection(bundle, new(DefinitionEditor.Project(bundle, 9), ["/a/x"], "/b"), service, new());
        Assert.Equal(type, service.Validate(DefinitionEditor.Render(bundle, moved.Draft)).Units.Flatten().Single(u => u.Path == "/b/x").Type);
    }

    /// <summary>関係が崩れる判定先行や衝突を、元の下書きを変更せず拒否します。</summary>
    [Fact]
    public void RejectsInvalidSelectionsAtomically()
    {
        var service = Service();
        var bundle = service.Validate([Source("a", "unit=a;{ty=n;sd=0,ud;ar=(f=j,t=d,seq);unit=j;{ty=j;te=\"true\";}unit=d;{ty=jdj;ej=eq;ejc=0;}unit=n;{ty=n;}}unit=b;{ty=n;sd=0,ud;unit=j;{ty=j;te=\"true\";}}")]);
        var draft = DefinitionEditor.Project(bundle, 9);
        foreach (var request in new EditorMoveSelectionRequest[] { new(draft, [], "/b"), new(draft, ["/a/d", "/a/d"], "/b"), new(draft, ["/a/d"], "/b"), new(draft, ["/a/j"], "/b"), new(draft, ["/a/n"], "/a/n"), new(draft, ["/a/n"], "/a"), new(draft, ["/a/n", "/b/j"], "/b"), new(draft, ["/a"], "/b") })
        {
            Assert.Throws<ArgumentException>(() => DefinitionEditor.MoveSelection(bundle, request, service, new()));
        }

        Assert.Equal(bundle.Sources[0].Base64, Assert.Single(DefinitionEditor.Render(bundle, draft)).Base64);
    }

    /// <summary>受理したCP932バイトとBOMを維持し、非可逆な符号位置は拒否します。</summary>
    [Fact]
    public void PreservesAcceptedBytesAndBom()
    {
        var service = Service();
        var prefix = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("/* unchanged BOM */\nunit=b;{ty=n;sd=0,ud;}"));
        var source = Source("a", "unit=a;{ty=n;sd=0,ud;unit=j;{\n/*≒*/\nty=j;te=\"true\";}}", "shift_jis");
        var raw = Convert.FromBase64String(source.Base64);
        int index = Array.FindIndex(raw, b => b == 0x81);
        if (index < 0)
        {
            index = Array.FindIndex(raw, b => b == 0x87);
        }

        Assert.True(index >= 0);
        raw[index] = 0x87;
        raw[index + 1] = 0x90;
        source.Base64 = Convert.ToBase64String(raw);
        Assert.Throws<DecoderFallbackException>(() => service.Validate([source]));
        raw[index] = 0x81;
        raw[index + 1] = 0xe0;
        source.Base64 = Convert.ToBase64String(raw);
        var bundle = service.Validate([source, new() { Mapping = new() { BlobPath = "b", Encoding = "utf-8" }, Base64 = Convert.ToBase64String(prefix.ToArray()) }]);

        // 同じ文字コード内なら受理した本文バイトをそのまま移動します。
        var same = service.Validate([source, Source("b", "/*≒*/\nunit=b;{ty=n;sd=0,ud;}", "shift_jis")]);
        var moved = DefinitionEditor.MoveSelection(same, new(DefinitionEditor.Project(same, 9), ["/a/j"], "/b"), service, new());
        var result = DefinitionEditor.Render(same, moved.Draft);
        Assert.Contains("81E0", Convert.ToHexString(Convert.FromBase64String(result.Single(s => s.Mapping.BlobPath == "b").Base64)), StringComparison.Ordinal);
        var utf = DefinitionEditor.MoveSelection(bundle, new(DefinitionEditor.Project(bundle, 9), ["/a/j"], "/b"), service, new());
        Assert.True(Convert.FromBase64String(DefinitionEditor.Render(bundle, utf.Draft).Single(s => s.Mapping.BlobPath == "b").Base64).AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
    }

    /// <summary>省略カレンダーは移動先から継承し、明示参照と上位ルール値は保持します。</summary>
    [Fact]
    public void KeepsExplicitCalendarAndUpperRuleWhileInheritanceChanges()
    {
        var service = Service();
        var bundle = service.Validate([Source("a", "unit=g1;{ty=g;cl=su;unit=a;{ty=n;sd=0,ud;unit=x;{ty=n;de=y;}unit=fixed;{ty=n;de=n;sd=1,2026/11/2;ln=1,2;jc=\"/calendar\";}}}unit=g2;{ty=g;cl=mo;unit=b;{ty=n;sd=0,ud;}}unit=calendar;{ty=g;cl=tu;}")]);
        var before = bundle.Units.Flatten().ToDictionary(u => u.Path);
        var moved = DefinitionEditor.MoveSelection(bundle, new(DefinitionEditor.Project(bundle, 9), ["/g1/a/x", "/g1/a/fixed"], "/g2/b"), service, new());
        var result = service.Validate(DefinitionEditor.Render(bundle, moved.Draft));
        var after = result.Units.Flatten().ToDictionary(u => u.Path);
        var day = new DateOnly(2026, 11, 2);
        Assert.True(ScheduleCalculator.EvaluateCalendar(day, before["/g1/a/x"], before).Working);
        Assert.False(ScheduleCalculator.EvaluateCalendar(day, after["/g2/b/x"], after).Working);
        Assert.Equal("/calendar", after["/g2/b/fixed"].Get("jc"));
        Assert.Equal("1,2", after["/g2/b/fixed"].Get("ln"));
        Assert.Equal(ScheduleCalculator.EvaluateCalendar(day, before["/g1/a/fixed"], before).Working, ScheduleCalculator.EvaluateCalendar(day, after["/g2/b/fixed"], after).Working);
        Assert.Contains(moved.Diagnostics!, d => d.Contains("ln=1,2", StringComparison.Ordinal));
    }

    /// <summary>表現不能文字を置換せず、移動要求全体を拒否します。</summary>
    [Fact]
    public void RejectsUnrepresentableCrossEncoding()
    {
        var service = Service();
        var bundle = service.Validate([Source("a", "unit=a;{ty=n;sd=0,ud;unit=j;{ty=j;te=\"echo 😀\";}}"), Source("b", "unit=b;{ty=n;sd=0,ud;}", "shift_jis")]);
        var draft = DefinitionEditor.Project(bundle, 9);
        Assert.Throws<EncoderFallbackException>(() => DefinitionEditor.MoveSelection(bundle, new(draft, ["/a/j"], "/b"), service, new()));
        Assert.Equal(bundle.Sources.Select(s => s.Base64), DefinitionEditor.Render(bundle, draft).Select(s => s.Base64));
    }

    /// <summary>リカバリー判定を含む接続を保持し、リカバリーネットにも移動できます。</summary>
    [Fact]
    public void MovesRecoveryJudgmentIntoRecoveryNetwork()
    {
        var service = Service();
        var bundle = service.Validate([Source("a", "unit=a;{ty=n;sd=0,ud;ar=(f=j,t=d,seq);ar=(f=d,t=n,con);unit=j;{ty=rj;te=\"true\";}unit=d;{ty=rjdj;ej=eq;ejc=0;}unit=n;{ty=rn;}}unit=b;{ty=n;sd=0,ud;unit=recovery;{ty=rn;}}")]);
        var moved = DefinitionEditor.MoveSelection(bundle, new(DefinitionEditor.Project(bundle, 9), ["/a/j", "/a/d", "/a/n"], "/b/recovery"), service, new());
        var units = service.Validate(DefinitionEditor.Render(bundle, moved.Draft)).Units.Flatten().ToDictionary(u => u.Path);
        Assert.Equal("rjdj", units["/b/recovery/d"].Type);
        Assert.Equal(new[] { "(f=j,t=d,seq)", "(f=d,t=n,con)" }, units["/b/recovery"].Attributes["ar"]);
    }

    /// <summary>原文が別の単独ジョブを親ネットの子へ黙って取り込みません。</summary>
    [Fact]
    public void RejectsStandaloneJobOutsideEditorTree()
    {
        var service = Service();
        var bundle = service.Validate([Source("parents", "unit=a;{ty=n;sd=0,ud;}unit=b;{ty=n;sd=0,ud;}"), Source("job", "/* keep comment */\nunit=j;{ty=j;te=\"true\";}\n", "utf-8", "/a")]);
        var draft = DefinitionEditor.Project(bundle, 9);
        var error = Assert.Throws<ArgumentException>(() => DefinitionEditor.MoveSelection(bundle, new(draft, ["/a/j"], "/b"), service, new()));
        Assert.Contains("選択したユニットがありません", error.Message, StringComparison.Ordinal);
        Assert.Equal(bundle.Sources.Select(s => s.Base64), DefinitionEditor.Render(bundle, draft).Select(s => s.Base64));
    }

    /// <summary>多数選択も一つの候補として運び、各コマンドを保持します。</summary>
    /// <param name="count">選択するジョブ数です。</param>
    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public void MovesLargeSelection(int count)
    {
        var service = Service();
        string jobs = string.Concat(Enumerable.Range(0, count).Select(i => $"unit=j{i};{{ty=j;te=\"echo 日本語{i}\";}}"));
        var bundle = service.Validate([Source("a", "unit=a;{ty=n;sd=0,ud;" + jobs + "}unit=b;{ty=n;sd=0,ud;}")]);
        var moved = DefinitionEditor.MoveSelection(bundle, new(DefinitionEditor.Project(bundle, 9), Enumerable.Range(0, count).Select(i => "/a/j" + i).ToList(), "/b"), service, new());
        var result = service.Validate(DefinitionEditor.Render(bundle, moved.Draft));
        Assert.Empty(result.Units.Single(u => u.Path == "/a").Children);
        var children = result.Units.Single(u => u.Path == "/b").Children;
        Assert.Equal(count, children.Count);
        Assert.All(children, u => Assert.Equal("echo 日本語" + u.Name[1..], u.Get("te")));
    }

    private static SourceEvidence Source(string name, string text, string encoding = "utf-8", string target = "/")
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return new() { Mapping = new() { BlobPath = name, Encoding = encoding, TargetPath = target }, Base64 = Convert.ToBase64String(Encoding.GetEncoding(encoding).GetBytes(text)) };
    }

    private static DefinitionService Service() => new(new StorageRepository(new RunnerOptions { ConnectionString = "UseDevelopmentStorage=true" }), new RunnerOptions());
}
