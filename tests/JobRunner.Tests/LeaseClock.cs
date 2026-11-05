namespace JobRunner.Tests;

/// <summary>UTCの補正と単調時計の経過を独立させ、リースの期限判定を検証します。</summary>
internal sealed class LeaseClock : TimeProvider
{
    /// <summary>Gets or sets. UTC時計の補正を模擬する値です。</summary>
    public DateTimeOffset Utc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets. 単調時計の経過。UTCの補正では変更しません。</summary>
    public long ElapsedTicks { get; set; }

    /// <summary>Gets. 試験ではTimeSpanのtickをそのまま単調時計単位にします。</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>UTCだけを補正した結果を返します。</summary>
    /// <returns>設定された壁時計時刻です。</returns>
    public override DateTimeOffset GetUtcNow() => Utc;

    /// <summary>安全期限の検査に使う独立した経過値を返します。</summary>
    /// <returns>取得時からの単調時計のtickです。</returns>
    public override long GetTimestamp() => ElapsedTicks;
}
