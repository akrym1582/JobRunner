using JobRunner.Controllers.Jp1;
using Xunit;

namespace JobRunner.Tests;

/// <summary>一般的な正規表現や状態の表示変更と混同しない検索契約です。</summary>
public sealed class SearchSemanticsTests
{
    /// <summary>公式例と文字単位・エスケープ・全体一致を確認します。</summary>
    /// <param name="name">比較される名前です。</param>
    /// <param name="pattern">JP1のREです。</param>
    /// <param name="matches">期待する一致です。</param>
    [Theory]
    [InlineData("AB", "A?", true)]
    [InlineData("A", "A?", false)]
    [InlineData("ABC", "A?", false)]
    [InlineData("B", "B*", true)]
    [InlineData("B?*\\", "B*", true)]
    [InlineData("C?", "C\\?", true)]
    [InlineData("CA", "C\\?", false)]
    [InlineData("*", "\\*", true)]
    [InlineData("\\", "\\\\", true)]
    [InlineData("あ😀", "??", true)]
    [InlineData("Ab", "a*", false)]
    [InlineData("x.A", "*.?", true)]
    [InlineData("A[1]", "A[1]", true)]
    [InlineData("A1", "A[1]", false)]
    [InlineData("abcde", "*b*d*", true)]
    public void UsesJp1Wildcards(string name, string pattern, bool matches) => Assert.Equal(matches, Jp1Search.MatchesName(name, pattern));

    /// <summary>文字が続かないエスケープを比較対象の有無にかかわらず拒否します。</summary>
    [Fact]
    public void RejectsIncompleteEscape() => Assert.ThrowsAny<FormatException>(() => Jp1Search.ValidatePattern("A\\"));

    /// <summary>すべての実装済み状態について、包含と除外を照合します。</summary>
    /// <param name="filter">集合検索の名前です。</param>
    /// <param name="expected">含める状態の一覧です。</param>
    [Theory]
    [InlineData("GRP_WAIT", "TIMEWAIT,TERMWAIT,EXECWAIT")]
    [InlineData("GRP_RUN", "RUNNING,WACONT,ABCONT")]
    [InlineData("GRP_ABNORMAL", "ABNORMAL,INTERRUPT,KILL,FAIL,UNKNOWN")]
    [InlineData("GRP_NORMAL", "NORMAL,NORMALFALSE")]
    public void StateGroupsExcludeOtherStates(string filter, string expected)
    {
        string[] statuses = "NONE,NOENTRY,NOPLAN,UNEXEC,BYPASS,TIMEWAIT,TERMWAIT,EXECWAIT,HOLDING,RUNNING,WACONT,ABCONT,ABNORMAL,INTERRUPT,KILL,FAIL,UNKNOWN,WARNING,NORMAL,NORMALFALSE".Split(',');
        Assert.Equal(expected.Split(',').Order(), statuses.Where(s => Jp1Search.MatchesStatus(s, filter)).Order());
    }
}
