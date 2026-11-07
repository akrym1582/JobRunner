using System.Text;
using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>再帰解析前の深さ検査が運用日属性をコメントと誤認しないことを確認します。</summary>
public sealed class LocalCalendarLexingTests
{
    /// <summary>年月付きの運用日・休業日指定を、引用や書換えなしで読み込みます。</summary>
    /// <param name="selector">日付の選択方法です。</param>
    [Theory]
    [InlineData("*4")]
    [InlineData("*b")]
    [InlineData("@1")]
    public void AcceptsUnquotedCalendarSelectors(string selector)
    {
        string text = $"unit=n;{{ty=n;sd=1,2026/11/{selector};st=10:00;sh=ca;}}";
        var bundle = Read(text);
        Assert.Equal("1,2026/11/" + selector, bundle.Units[0].Get("sd"));
        var draft = DefinitionEditor.Project(bundle, 9) with { LocalSources = bundle.Sources };
        Assert.Equal(Encoding.UTF8.GetBytes(text), Convert.FromBase64String(Assert.Single(DefinitionEditor.Render(bundle, draft)).Base64));
    }

    /// <summary>属性のリテラル中のコメント開始文字列を消費しません。</summary>
    /// <param name="value">引用あり/なしの属性値です。</param>
    [Theory]
    [InlineData("literal/*text")]
    [InlineData("\"literal/*text\"")]
    public void KeepsCommentLikeAttributeValues(string value)
    {
        var bundle = Read("unit=n;{ty=n;sd=0,ud;cm=" + value + ";}");
        Assert.Equal("literal/*text", bundle.Units[0].Get("cm"));
    }

    /// <summary>値の直前の本当のコメント内にある括弧は、深さに数えません。</summary>
    [Fact]
    public void IgnoresBracesInsideLeadingComment()
    {
        string text = "unit=n;{ty=n;sd=0,ud;cm=\n/*" + new string('{', 10001) + "*/\n\"keep\";}";
        Assert.Equal("keep", Read(text).Units[0].Get("cm"));
    }

    /// <summary>リテラル中の疑似コメントで、後続の深い階層を隠せません。</summary>
    [Fact]
    public void StillRejectsDeepStructureAfterLiteral()
    {
        string text = "unit=root;{ty=n;cm=literal/*;" + string.Concat(Enumerable.Range(0, 64).Select(i => $"unit=n{i};{{ty=n;")) + new string('}', 65);
        Assert.Throws<ArgumentException>(() => Read(text));
    }

    private static DefinitionBundle Read(string text)
    {
        var options = new RunnerOptions();
        var service = new DefinitionService(new StorageRepository(options), options);
        return EditorLocalFiles.Read(new EditorLocalRequest([new() { Mapping = new() { BlobPath = "calendar.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) }]), service);
    }
}
