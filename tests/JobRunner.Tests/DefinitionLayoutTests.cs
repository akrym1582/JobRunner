using JobRunner.Controllers.Ui;
using JobRunner.Models;
using Xunit;

namespace JobRunner.Tests;

/// <summary>表示座標は推測せず、JP1のel形式を読み取ります。</summary>
public sealed class DefinitionLayoutTests
{
    /// <summary>範囲端と定義順に反する位置を補正せず保持します。</summary>
    [Fact]
    public void PreservesExplicitCoordinates()
    {
        var net = Network("first,j,+16000 +10000", "second,j,+0 +0");
        var result = DefinitionLayout.Read(net);
        Assert.Equal((16000, 10000), result["/net/first"]);
        Assert.Equal((0, 0), result["/net/second"]);
    }

    /// <summary>欠落・形式・範囲・名前・種別・重複指定を図のエラーにします。</summary>
    /// <param name="value">不正なelです。</param>
    [Theory]
    [InlineData("")]
    [InlineData("first,j,+16001 +48")]
    [InlineData("first,j,+80 +10001")]
    [InlineData("first,j,-80 +48")]
    [InlineData("first,j,+80")]
    [InlineData("first,j,+999999999999 +48")]
    [InlineData("first,n,+80 +48")]
    [InlineData("missing,j,+80 +48")]
    [InlineData("second,j,+80 +48")]
    public void RejectsInvalidCoordinates(string value)
    {
        Assert.Throws<InvalidDataException>(() => DefinitionLayout.Read(Network(value, "second,j,+240 +48")));
    }

    private static UnitDefinition Network(params string[] elements) => new()
    {
        Path = "/net",
        Type = "n",
        Attributes = new() { ["el"] = elements.ToList() },
        Children = [new() { Path = "/net/first", Name = "first", Type = "j" }, new() { Path = "/net/second", Name = "second", Type = "j" }],
    };
}
