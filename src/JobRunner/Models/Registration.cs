namespace JobRunner.Models;

/// <summary>
/// 登録種別と補充済み範囲を固定し、再起動で解除済み登録を復活させません。
/// </summary>
public sealed class Registration
{
    /// <summary>
    /// Gets or sets. 操作または自動登録から決定的に生成する識別子です。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 採用ID。別定義の登録と状態を混ぜないために使用します。
    /// </summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 独立に実行登録するルートジョブネットの完全名です。
    /// </summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. Confirmed、Planned、Immediateの登録種別です。
    /// </summary>
    public string Kind { get; set; } = "Confirmed";

    /// <summary>
    /// Gets or sets. 省略された年月や相対日の解決に用いる固定業務日です。
    /// </summary>
    public DateOnly Anchor { get; set; } = default;

    /// <summary>
    /// Gets or sets. この日付未満の予定は補充済みであることを示すカーソルです。
    /// </summary>
    public DateOnly Through { get; set; } = default;

    /// <summary>
    /// Gets or sets. 明示的な登録期限。nullは自動補充を継続します。
    /// </summary>
    public DateOnly? End { get; set; } = null;

    /// <summary>
    /// Gets or sets a value indicating whether. 解除された登録を補充や再起動で復活させないための印です。
    /// </summary>
    public bool Disabled { get; set; } = false;

    /// <summary>Gets or sets. ルートの保留を登録時に明示上書きする指定。nullは定義に従います。</summary>
    public bool? Holding { get; set; } = null;

    /// <summary>Gets or sets. 維持する未開始ルート世代数。nullは従来の日数補充です。</summary>
    public int? FutureGenerations { get; set; } = null;

    /// <summary>Gets or sets. 世代数方式の初回予定を選ぶ固定UTC基準です。</summary>
    public DateTimeOffset? CreatedAt { get; set; } = null;

    /// <summary>Gets or sets. 予定が不足した探索結果を同日に繰り返さないための日付です。</summary>
    public DateOnly? ExhaustedOn { get; set; } = null;

    /// <summary>Gets or sets. この登録または世代が参照する不変定義版。旧形式では採用版です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? DefinitionHash { get; set; }

    /// <summary>Gets or sets. 確定期間の開始業務日。Endと組み合わせます。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? Start { get; set; }

    /// <summary>Gets or sets. 定義の予定に依存しない単一の確定日時です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? FixedDue { get; set; }

    /// <summary>Gets or sets. 未開始の過去予定をIMMEDIATEで実行、NEXTTIMEで除外します。旧形式はNEXTTIMEです。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PassedDaemonStarts { get; set; }

    /// <summary>Gets or sets. 固定日時から繰り返す間隔（分）。独自管理APIの指定です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? IntervalMinutes { get; set; }

    /// <summary>Gets or sets. 間隔実行の排他的な終了日時です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? IntervalUntil { get; set; }

    /// <summary>Gets or sets. 次の未生成予定。応答喪失時は同じ決定的なキーで再開します。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? NextIntervalDue { get; set; }

    /// <summary>Gets or sets. 期間解除の除外範囲。補充時にも適用します。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<RegistrationExclusion>? Exclusions { get; set; }
}
