namespace JobRunner.Models;

/// <summary>
/// ルート世代と子ユニットの実行状態を永続化し、タイマーから独立して復元します。
/// </summary>
public sealed class Occurrence
{
    /// <summary>
    /// Gets or sets. 登録・ルール・業務日・ユニットから決定的に作る世代IDです。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. この世代を所有する採用IDです。
    /// </summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 予定を生成した実行登録の識別子です。
    /// </summary>
    public string RegistrationId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 同じネット世代に所属するユニットをまとめるIDです。
    /// </summary>
    public string RootId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 定義スナップショット上の実行ユニット完全名です。
    /// </summary>
    public string UnitPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 多重実行制御の対象となるルート完全名です。
    /// </summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 生成元のスケジュールルール番号です。
    /// </summary>
    public int Rule { get; set; } = 1;

    /// <summary>
    /// Gets or sets. 休業日評価に用いた業務日。UTCの日付と区別します。
    /// </summary>
    public DateOnly BusinessDate { get; set; } = default;

    /// <summary>
    /// Gets or sets. タイムゾーン変換済みの実行予定UTC日時です。
    /// </summary>
    public DateTimeOffset Due { get; set; } = default;

    /// <summary>
    /// Gets or sets. 一時変更を解除するときに戻す確定予定日時です。
    /// </summary>
    public DateTimeOffset OriginalDue { get; set; } = default;

    /// <summary>
    /// Gets or sets. Waiting、Held、Starting、Runningまたは終了状態です。
    /// </summary>
    public string State { get; set; } = "Waiting";

    /// <summary>
    /// Gets or sets a value indicating whether. ルート開始後の後続を復旧時に過去予定として除外しないための印です。
    /// </summary>
    public bool Started { get; set; } = false;

    /// <summary>
    /// Gets or sets. 最新の実行試行。再実行では別IDを発行します。
    /// </summary>
    public string? AttemptId { get; set; } = null;

    /// <summary>
    /// Gets or sets. スキップや明示解決などの操作理由です。
    /// </summary>
    public string? Reason { get; set; } = null;
}
