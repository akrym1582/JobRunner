using System.Text;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>V12の構文と、危険な定義を有効化しない条件を検証します。</summary>
public sealed class DefinitionTests
{
    /// <summary>引用文字列の区切り記号とキャストを階層構文と混同しないことを確認します。</summary>
    [Fact]
    public void PreservesQuotedCharactersAndHierarchy()
    {
        const string source = "/* 原文 */\nunit=net; { ty=n; unit=job; { ty=j; te=\"echo #\"a;b{c}###\"\"; } }";
        var units = Jp1DefinitionParser.Parse(source, new DefinitionSource { TargetPath = "/業務/日次" });
        Assert.Equal("/業務/日次/net/job", units[0].Children[0].Path);
        Assert.Equal("echo \"a;b{c}#\"", units[0].Children[0].Get("te"));
    }

    /// <summary>引用文字列内の末尾空白が環境変数等の値から失われないことを確認します。</summary>
    [Fact]
    public void KeepsWhitespaceInsideQuotedValue()
    {
        var units = Jp1DefinitionParser.Parse("unit=j;{ty=j;env=\"VALUE= trailing \";te=\"true\";}", new DefinitionSource());
        Assert.Equal("VALUE= trailing ", units[0].Get("env"));
    }

    /// <summary>未知の実行属性は黙って無視せず、型に関係なく有効化を拒否します。</summary>
    /// <param name="attribute">未対応の挙動を指定する属性です。</param>
    [Theory]
    [InlineData("un=other;")]
    [InlineData("unknown=y;")]
    [InlineData("ex=remote;")]
    public void RejectsUnsupportedExecutionAttributes(string attribute)
    {
        var units = Jp1DefinitionParser.Parse($"unit=job; {{ ty=j; te=\"true\"; {attribute} }}", new DefinitionSource());
        Assert.Throws<FormatException>(() => DefinitionValidator.Validate(units, new RunnerOptions()));
    }

    /// <summary>未閉鎖の文字列や括弧、循環を受け入れると復旧不能になるため拒否します。</summary>
    /// <param name="source">拒否すべき定義原文です。</param>
    [Theory]
    [InlineData("unit=x; { ty=n;")]
    [InlineData("/* comment */ unit=x;{ty=n;}")]
    [InlineData("unit=x; { ty=n; te=\"unterminated; }")]
    [InlineData("unit=x; { ty=n; ar=(f=a,t=b,seq); ar=(f=b,t=a,seq); unit=a; {ty=j;te=\"true\";} unit=b;{ty=j;te=\"true\";} }")]
    [InlineData("unit=x; { ty=n; ar=(f=missing,t=a,seq); unit=a;{ty=j;te=\"true\";} }")]
    public void RejectsMalformedAndCyclicDefinitions(string source)
    {
        Assert.Throws<FormatException>(() => DefinitionValidator.Validate(Jp1DefinitionParser.Parse(source, new DefinitionSource()), new RunnerOptions()));
    }

    /// <summary>配置順序とETagだけの変更で再登録せず、配置先の変更は検出します。</summary>
    [Fact]
    public void ManifestHashIgnoresOrderAndETagButTracksPlacement()
    {
        var options = new RunnerOptions();
        var service = new DefinitionService(new StorageRepository(options), options);
        var a = Source("a", "/first");
        var b = Source("b", "/second");
        string original = service.Validate([a, b]).Hash;
        a.ETag = "different";
        Assert.Equal(original, service.Validate([b, a]).Hash);
        a.Mapping.TargetPath = "/changed";
        Assert.NotEqual(original, service.Validate([a, b]).Hash);
    }

    /// <summary>同名ユニットの後勝ち上書きが起きないことを確認します。</summary>
    [Fact]
    public void RejectsDuplicateFullNamesAcrossFiles()
    {
        var options = new RunnerOptions();
        var service = new DefinitionService(new StorageRepository(options), options);
        Assert.Throws<FormatException>(() => service.Validate([Source("a", "/"), Source("b", "/")]));
    }

    /// <summary>所有者はメタデータとして保持し、所有者実行の指定だけ明示的なローカル対応を要求します。</summary>
    [Fact]
    public void ParsesHeaderWithoutImplicitUserSwitch()
    {
        var units = Jp1DefinitionParser.Parse("unit=net,0000,owner,group_1;{ty=n;}", new DefinitionSource());
        DefinitionValidator.Validate(units, new RunnerOptions());
        Assert.Equal("owner", units[0].OwnerName);
        Assert.Equal("group_1", units[0].ResourceGroup);
        Assert.Equal("0000", units[0].PermissionMode);
        units[0].PermissionMode = "4000";
        Assert.Throws<FormatException>(() => DefinitionValidator.Validate(units, new RunnerOptions()));
        DefinitionValidator.Validate(units, new RunnerOptions { LocalUsers = ["owner"] });
    }

    /// <summary>許可モードと資源グループを任意テキストとして受け入れないことを確認します。</summary>
    /// <param name="header">公式の構文制約に違反するヘッダーです。</param>
    [Theory]
    [InlineData("net,8888,owner,group")]
    [InlineData("net,0000,owner,bad-group")]
    [InlineData("net,0000,owner,group,extra")]
    public void RejectsInvalidHeader(string header)
    {
        Assert.Throws<FormatException>(() => Jp1DefinitionParser.Parse($"unit={header};{{ty=n;}}", new DefinitionSource()));
    }

    /// <summary>API入力用に原文バイト列を保持した証跡を作ります。</summary>
    /// <param name="file">入力ファイルの識別名です。</param>
    /// <param name="target">配置先の親です。</param>
    /// <returns>同じ内容を異なる場所へ配置できる入力です。</returns>
    private static SourceEvidence Source(string file, string target) => new()
    {
        Mapping = new DefinitionSource { BlobPath = "inputs/" + file, TargetPath = target },
        Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=net;{ty=n;}")),
    };
}
