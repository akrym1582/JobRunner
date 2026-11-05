using System.Text.Json.Serialization;

namespace JobRunner.Models;

/// <summary>
/// API要求と結果を同じ行で管理し、HTTP再送による二重操作を防ぎます。
/// </summary>
public sealed class Operation
{
    /// <summary>
    /// Gets or sets. 受付時の採用ID。処理前に版が切り替わった場合、別版へ操作を誤適用しません。
    /// </summary>
    public string StateNamespace { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. クライアントが発行する冪等性キーです。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. Activate、Register、Unregister、Hold、Release、Move、Restore、Cancel、Stop、Rerun、Resolveです。
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 定義ハッシュ、登録IDまたは世代IDです。
    /// </summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 同じIDへの異なる要求を拒否するための内容ハッシュです。
    /// </summary>
    public string RequestHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 未処理・完了・拒否を表す操作状態です。
    /// </summary>
    public string Status { get; set; } = "Pending";

    /// <summary>
    /// Gets or sets. 処理結果または拒否理由です。
    /// </summary>
    public string? Result { get; set; } = null;

    /// <summary>
    /// Gets or sets. 結果不明を解決する際に指定する対象試行IDです。
    /// </summary>
    public string? AttemptId { get; set; } = null;

    /// <summary>
    /// Gets or sets. 手動操作で競合を検出するための期待状態です。
    /// </summary>
    public string? ExpectedState { get; set; } = null;

    /// <summary>
    /// Gets or sets. 結果不明の解決で必須となる業務上の根拠です。
    /// </summary>
    public string? Reason { get; set; } = null;

    /// <summary>
    /// Gets or sets. 手動解決後のSucceededまたはFailedです。
    /// </summary>
    public string? Resolution { get; set; } = null;

    /// <summary>
    /// Gets or sets. 日時変更または即時登録の実日時です。
    /// </summary>
    public DateTimeOffset? Due { get; set; } = null;

    /// <summary>
    /// Gets or sets. 登録要求のConfirmed、Planned、Immediateです。
    /// </summary>
    public string RegistrationKind { get; set; } = "Confirmed";

    /// <summary>
    /// Gets or sets. 登録期間の排他的上限。省略時は継続補充します。
    /// </summary>
    public DateOnly? End { get; set; } = null;

    /// <summary>Gets or sets. サーバーが受付時に記録するUTC時刻。要求ハッシュには含めません。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CreatedAt { get; set; } = null;

    /// <summary>Gets or sets. 稼働系が要求の完了または拒否を保存したUTC時刻です。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CompletedAt { get; set; } = null;

    /// <summary>Gets or sets. 原要求を退避した不変Blobパス。冪等性の墓標に保持します。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArchivePath { get; set; } = null;

    /// <summary>Gets or sets. 登録でルート保留を明示上書きする指定。nullは定義に従います。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Holding { get; set; } = null;

    /// <summary>Gets or sets. 確定登録で維持する未開始ルート数。nullは期間方式です。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FutureGenerations { get; set; } = null;

    /// <summary>Gets or sets. ネット全体を先頭から再実行する場合はTOPを明示します。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RerunMethod { get; set; } = null;

    /// <summary>Gets or sets. 分割操作の不変変更計画。サーバーだけが設定します。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MutationPath { get; set; } = null;

    /// <summary>Gets or sets. 補充用定義を更新する際の新しい保存済み版です。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefinitionHash { get; set; }

    /// <summary>Gets or sets. 補充用定義の更新前に一致を要求する版です。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExpectedDefinitionHash { get; set; }

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

    /// <summary>Gets or sets. 再実行時のHOLD、WARNINGオプションです。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RerunOptions { get; set; }
}
