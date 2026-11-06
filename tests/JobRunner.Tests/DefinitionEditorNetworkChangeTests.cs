using System.Text;
using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>ネット操作の継承・接続と無関係な原文の保持を確認します。</summary>
public sealed class DefinitionEditorNetworkChangeTests
{
    private const string Network = "unit=net,0000,owner,team;{ty=n;sd=1,1;st=1,07:00;cy=1,(1,d);cm=\"/a/net 原文\";el=pre,j,+80 +48;el=child,n,+240 +48;ar=(f=pre,t=child);unit=pre;{ty=j;te=\"echo /a/net\";}unit=child;{ty=n;cm=\"keep\";unit=job;{ty=j;te=\"echo 子\";}}}";
    private const string Text = "/* outside */\r\nunit=a;{ty=g;cl=su;stt=06:00;el=net,n,+80 +48;" + Network + "}\r\nunit=b;{ty=g;cl=su;stt=06:00;}\r\n";

    /// <summary>ネットブロックと他の表記を保持し、移動前後で同じ予定を計算します。</summary>
    /// <param name="encoding">文字コードです。</param>
    /// <param name="bom">BOMです。</param>
    [Theory]
    [InlineData("utf-8", false)]
    [InlineData("utf-8", true)]
    [InlineData("utf-16", true)]
    [InlineData("utf-16BE", true)]
    [InlineData("shift_jis", false)]
    public void MovesRootPreservingBlockAndCalendar(string encoding, bool bom)
    {
        var service = Service();
        var bundle = service.Validate([Source("a.ajs", Text, encoding, bom), Source("untouched.ajs", "unit=extra;{ty=g;}")]);
        var draft = DefinitionEditor.Project(bundle, 8);
        Assert.Equal(bundle.Sources.Select(s => s.Base64), DefinitionEditor.Render(bundle, draft).Select(s => s.Base64));
        var before = Trial(bundle, draft);
        Assert.NotEmpty(before);
        draft.NetworkChanges!.Add(new("/a/net", "/b"));
        var output = DefinitionEditor.Render(bundle, draft);
        var imported = service.Validate(output);
        Assert.Equal(before, Trial(imported, draft));
        Assert.Equal(bundle.Sources[1].Base64, output[1].Base64);
        Assert.Equal(bundle.Sources[0].Mapping, output[0].Mapping);
        string text = Encoding.GetEncoding(encoding).GetString(Convert.FromBase64String(output[0].Base64));
        Assert.Contains(Network, text, StringComparison.Ordinal);
        Assert.Contains("/* outside */\r\nunit=a;{ty=g;cl=su;stt=06:00;}", text, StringComparison.Ordinal);
        var all = imported.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        Assert.Contains("/b/net/child/job", all.Keys);
        Assert.Equal("net,n,+80 +48", all["/b"].Get("el"));
        Assert.Equal("echo /a/net", all["/b/net/pre"].Get("te"));
        Assert.Equal("owner", all["/b/net"].OwnerName);
        Assert.Equal(bundle.Sources[0].Base64, Source("a.ajs", Text, encoding, bom).Base64);
    }

    /// <summary>親の改名、移動、子ネットの切替を併用し、接続と属性を落としません。</summary>
    [Fact]
    public void ConvertsNestedNetworkAndMovesRenamedRoot()
    {
        var service = Service();
        var bundle = service.Validate([Source("a.ajs", Text)]);
        var draft = DefinitionEditor.Project(bundle, 8);
        draft.GroupChanges!.Add(new("/b", "renamed"));
        draft.NetworkChanges!.AddRange([new("/a/net", "/b"), new("/a/net/child", Type: "rn")]);
        var output = service.Validate(DefinitionEditor.Render(bundle, draft));
        var all = output.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        Assert.Equal("rn", all["/renamed/net/child"].Type);
        Assert.Equal("n", all["/renamed/net"].Type);
        Assert.Equal("keep", all["/renamed/net/child"].Get("cm"));
        Assert.Equal("j", all["/renamed/net/child/job"].Type);
        Assert.Equal("(f=pre,t=child)", all["/renamed/net"].Get("ar"));
        Assert.Contains("child,rn,+240 +48", all["/renamed/net"].Attributes["el"]);
        var reverse = DefinitionEditor.Project(output, 8);
        reverse.NetworkChanges!.Add(new("/renamed/net/child", Type: "n"));
        Assert.Equal("n", service.Validate(DefinitionEditor.Render(output, reverse)).Units.Flatten().Single(u => u.Path.EndsWith("/child", StringComparison.Ordinal)).Type);
    }

    /// <summary>カレンダー、所有者、階層、ファイルと表示参照の意味を推測しません。</summary>
    /// <param name="target">移動先の定義です。</param>
    [Theory]
    [InlineData("unit=b;{ty=g;cl=sa;stt=06:00;}")]
    [InlineData("unit=b;{ty=g;cl=su;stt=00:00;}")]
    [InlineData("unit=b,0000,other,team;{ty=g;cl=su;stt=06:00;}")]
    [InlineData("unit=b;{ty=n;}")]
    public void RejectsChangedInheritanceOrNetworkDestination(string target)
    {
        var bundle = Service().Validate([Source("a.ajs", Text[..Text.IndexOf("unit=b", StringComparison.Ordinal)] + target)]);
        var draft = DefinitionEditor.Project(bundle, 8);
        draft.NetworkChanges!.Add(new("/a/net", "/b"));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
    }

    /// <summary>子ネット移動、ファイル越境、衝突、不明な表示参照は拒否します。</summary>
    [Fact]
    public void RejectsUnsupportedMovesAndCollisions()
    {
        var service = Service();
        var bundle = service.Validate([Source("a.ajs", Text), Source("b.ajs", "unit=other;{ty=g;cl=su;stt=06:00;}")]);
        foreach (var operation in new EditorNetworkChange[] { new("/a/net/child", "/b"), new("/a/net", "/other"), new("/a/net", "/missing"), new("/a/net", Type: "rn"), new("/a/net", Type: "j") })
        {
            var draft = DefinitionEditor.Project(bundle, 8);
            draft.NetworkChanges!.Add(operation);
            Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
        }

        foreach (string text in new[] { Text.Replace("stt=06:00;}\r\n", "stt=06:00;unit=net;{ty=n;}}\r\n", StringComparison.Ordinal), Text.Replace("el=net,n,+80 +48;", "el=net;", StringComparison.Ordinal) })
        {
            var input = service.Validate([Source("a.ajs", text)]);
            var draft = DefinitionEditor.Project(input, 8);
            draft.NetworkChanges!.Add(new("/a/net", "/b"));
            Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(input, draft));
        }
    }

    /// <summary>開始できないリカバリー化と不正な後続を、接続を消さずに拒否します。</summary>
    [Fact]
    public void RejectsAmbiguousOrInvalidRecoveryConnections()
    {
        var service = Service();
        foreach (string text in new[]
        {
            "unit=root;{ty=n;unit=child;{ty=n;}}",
            "unit=root;{ty=n;ar=(f=pre,t=judge);ar=(f=judge,t=child,con);unit=pre;{ty=j;te=\"true\";}unit=judge;{ty=jdj;ej=gt;ejc=0;}unit=child;{ty=n;}}",
            "unit=root;{ty=n;ar=(f=pre,t=child);ar=(f=child,t=last);unit=pre;{ty=j;te=\"true\";}unit=child;{ty=n;}unit=last;{ty=j;te=\"true\";}}",
        })
        {
            var bundle = service.Validate([Source("a.ajs", text)]);
            var draft = DefinitionEditor.Project(bundle, 8);
            draft.NetworkChanges!.Add(new("/root/child", Type: "rn"));
            var error = Record.Exception(() => service.Validate(DefinitionEditor.Render(bundle, draft)));
            Assert.True(error is ArgumentException or FormatException, error?.ToString() ?? "不正な切替を受理しました。");
            Assert.Equal(text, Encoding.UTF8.GetString(Convert.FromBase64String(bundle.Sources[0].Base64)));
        }
    }

    /// <summary>旧版では将来の操作を適用しません。</summary>
    [Fact]
    public void OldVersionIgnoresOperationsAndPendingNewRootCanMove()
    {
        var service = Service();
        var bundle = service.Validate([Source("a.ajs", Text)]);
        var old = DefinitionEditor.Project(bundle, 7) with { NetworkChanges = [new("/a/net", "/b")] };
        Assert.Equal(bundle.Sources[0].Base64, DefinitionEditor.Render(bundle, old)[0].Base64);
        Assert.NotEmpty(Trial(bundle, old));
        var draft = DefinitionEditor.Project(bundle, 8);
        draft.Roots!.Add(new(new("new:root", "newroot", "n", [], null, null), 0, "/a"));
        draft.Networks.Add(new("new:root", [], []));
        draft.Schedules!.Add(new("new:root", new() { ["sd"] = ["0,ud"] }));
        draft.Execution!.Add(new("new:root", new() { ["ha"] = ["y"] }));
        draft.NetworkChanges!.Add(new("new:root", "/b"));
        Assert.Equal("y", service.Validate(DefinitionEditor.Render(bundle, draft)).Units.Flatten().Single(u => u.Path == "/b/newroot").Get("ha"));
    }

    /// <summary>配置先が別ファイルのグループでも、原文を他ファイルの文字位置へ挿入しません。</summary>
    [Fact]
    public void MovesToOwnFileBoundaryWithoutEditingIndependentParent()
    {
        var service = Service();
        var owner = Source("owner.ajs", "/* independent parent with a different length */\nunit=parent;{ty=g;}");
        var local = Source("local.ajs", "/* keep */\nunit=inside;{ty=g;unit=net;{ty=n;cm=\"retained\";}}");
        local.Mapping.TargetPath = "/parent";
        var bundle = service.Validate([owner, local]);
        var draft = DefinitionEditor.Project(bundle, 8);
        draft.NetworkChanges!.Add(new("/parent/inside/net", "/parent"));
        var output = DefinitionEditor.Render(bundle, draft);
        Assert.Equal(owner.Base64, output[0].Base64);
        Assert.Equal(local.Mapping, output[1].Mapping);
        Assert.Contains("unit=inside;{ty=g;}\nunit=net;{ty=n;cm=\"retained\";}", Encoding.UTF8.GetString(Convert.FromBase64String(output[1].Base64)), StringComparison.Ordinal);
        Assert.Contains(service.Validate(output).Units.Flatten(), u => u.Path == "/parent/net");
        var withDisplay = Source("local.ajs", "unit=inside;{ty=g;el=net,n,+80 +48;unit=net;{ty=n;}}");
        withDisplay.Mapping.TargetPath = "/parent";
        bundle = service.Validate([owner, withDisplay]);
        draft = DefinitionEditor.Project(bundle, 8);
        draft.NetworkChanges!.Add(new("/parent/inside/net", "/parent"));
        Assert.Contains("el", Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft)).Message, StringComparison.Ordinal);
    }

    private static List<DateTimeOffset> Trial(DefinitionBundle bundle, EditorDraft draft) => DefinitionEditor.PreviewSchedule(bundle, new(draft, "/a/net", new(2026, 11, 1), new(2026, 11, 1), new(2026, 11, 10)), new()).Points.Select(p => p.Due).ToList();

    private static SourceEvidence Source(string name, string text, string encoding = "utf-8", bool bom = false)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return new() { Mapping = new() { BlobPath = name, Encoding = encoding, TargetPath = "/" }, Base64 = Convert.ToBase64String(Encoding.GetEncoding(encoding).GetBytes((bom ? "\uFEFF" : string.Empty) + text)) };
    }

    private static DefinitionService Service()
    {
        var options = new RunnerOptions();
        return new(new StorageRepository(options), options);
    }
}
