namespace JobRunner.Models;

/// <summary>全インスタンスが共有する新規開始の停止状態です。</summary>
public sealed class RuntimeControl
{
    /// <summary>Gets or sets. 制御操作の競合を検出する単調な版です。</summary>
    public long Revision { get; set; }

    /// <summary>Gets or sets a value indicating whether. 実行中プロセスの回収は停止しません。</summary>
    public bool Paused { get; set; } = true;

    /// <summary>Gets or sets. 最後に停止を適用した時刻です。</summary>
    public DateTimeOffset? PausedAt { get; set; }

    /// <summary>Gets or sets. 再開時に見送る未開始ルートの排他的境界です。</summary>
    public DateTimeOffset? SkipBefore { get; set; }
}
