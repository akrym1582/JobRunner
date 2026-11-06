namespace JobRunner.Models;

/// <summary>省略した監視項目を既存値と区別する一時設定です。</summary>
public sealed class DelayMonitorSettings
{
    /// <summary>Gets or sets. 開始のNOT/ABSOLUTE/ROOT/TOP/OWNです。</summary>
    public string? Start { get; set; }

    /// <summary>Gets or sets. 開始時刻または相対分数です。</summary>
    public int? StartMinutes { get; set; }

    /// <summary>Gets or sets. 終了のNOT/ABSOLUTE/ROOT/TOP/OWNです。</summary>
    public string? End { get; set; }

    /// <summary>Gets or sets. 終了時刻または相対分数です。</summary>
    public int? EndMinutes { get; set; }

    /// <summary>Gets or sets. 所要時間監視の有効・無効。nullは変更しません。</summary>
    public bool? Duration { get; set; }

    /// <summary>Gets or sets. 実開始からの監視分数です。</summary>
    public int? DurationMinutes { get; set; }
}
