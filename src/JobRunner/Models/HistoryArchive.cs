namespace JobRunner.Models;

/// <summary>一つのルート世代とその試行を退避し、分割削除を再開するための不変記録です。</summary>
public sealed class HistoryArchive
{
    /// <summary>Gets or sets. 後方互換を確認するための保存形式の版です。</summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>Gets or sets. 退避対象のルート世代。削除途中の探索にも使用します。</summary>
    public string RootId { get; set; } = string.Empty;

    /// <summary>Gets or sets. 退避開始を決めたUTC時刻です。</summary>
    public DateTimeOffset ArchivedAt { get; set; }

    /// <summary>Gets or sets. 世代・試行の原JSONと削除条件となるETagです。</summary>
    public List<ArchivedRow> Rows { get; set; } = [];
}
