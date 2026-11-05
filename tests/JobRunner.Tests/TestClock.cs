namespace JobRunner.Tests;

/// <summary>UTCだけを固定し、実時間待機なしで復旧の過去判定を検証します。</summary>
internal sealed class TestClock : TimeProvider
{
    /// <summary>Gets or sets. 復旧基準として観測されるUTC時刻です。リースの単調時計は進めません。</summary>
    public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    /// <summary>テストが明示的に設定したUTC時刻を返します。</summary>
    /// <returns>OS時刻に依存しない復旧基準時刻です。</returns>
    public override DateTimeOffset GetUtcNow() => Now;
}
