namespace JobRunner.Models;

/// <summary>
/// 初期化ジャーナルを保持し、中断後も同じ領域への登録を再開します。
/// </summary>
public sealed class Activation
{
    /// <summary>
    /// Gets or sets. 復旧処理が理解できる保存形式か確認する番号です。
    /// </summary>
    public int FormatVersion { get; set; } = 7;

    /// <summary>
    /// Gets or sets. 初回に発行する状態領域。定義反映でも登録・履歴の領域を維持します。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets. 同じ状態領域で運用定義を更新する単調な版です。</summary>
    public long Revision { get; set; }

    /// <summary>
    /// Gets or sets. 採用した定義スナップショットのハッシュです。
    /// </summary>
    public string DefinitionHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 予定と実行の意味を変える実効設定のハッシュです。
    /// </summary>
    public string SettingsHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 登録準備中か実行可能かを区別するPreparingまたはReadyです。
    /// </summary>
    public string Phase { get; set; } = "Preparing";

    /// <summary>
    /// Gets or sets. 初期化開始時に一度固定するUTC基準時刻です。
    /// </summary>
    public DateTimeOffset Baseline { get; set; } = default;

    /// <summary>Gets or sets. 作成時の所属と固定グループIDを保持する不変カタログです。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupCatalogHash { get; set; }
}
