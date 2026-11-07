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

    /// <summary>Gets or sets. この世代の結果が確定したUTC時刻。未開始や結果不明ではnullです。</summary>
    public DateTimeOffset? FinishedAt { get; set; } = null;

    /// <summary>Gets or sets. 退避済みルート一式の不変Blobパス。退避中の復旧にも使います。</summary>
    public string? ArchivePath { get; set; } = null;

    /// <summary>Gets or sets. 互換API用の不透明な実行ID。ルート世代に保存し、子はRootIdから参照します。</summary>
    public string? Jp1ExecutionId { get; set; } = null;

    /// <summary>Gets or sets. 子世代の生成が完了したかを示します。旧形式のnullは従来の検査を使います。</summary>
    public bool? Materialized { get; set; } = null;

    /// <summary>Gets or sets. この登録または世代が参照する不変定義版。旧形式では採用版です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? DefinitionHash { get; set; }

    /// <summary>Gets or sets. 次から再実行で明示的に通過する先行世代IDです。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? BypassedPredecessors { get; set; }

    /// <summary>Gets or sets. ONLY方式では上位の終了状態を変えず、選択した部分木だけ実行します。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? StandaloneRerun { get; set; }

    /// <summary>Gets or sets. スケジュール除外を条件不成立の非実行と区別します。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? ScheduleExcluded { get; set; }

    /// <summary>Gets or sets. 保存した先行終了コードに対する条件判定結果です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? ConditionMatched { get; set; }

    /// <summary>Gets or sets. 起動失敗など、内部状態だけでは区別できない終了理由です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusDetail { get; set; }

    /// <summary>Gets or sets. 未開始予定の最初の変更操作。復元対象の追加世代と関連付けます。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ScheduleChangeId { get; set; }

    /// <summary>Gets or sets. ADDで作成した元の予定変更の識別子です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AddedByChangeId { get; set; }

    /// <summary>Gets or sets. 最初の一時変更前の予定時刻です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? BeforeChangeDue { get; set; }

    /// <summary>Gets or sets. 最初の一時変更前の待機・保留・非実行状態です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? BeforeChangeState { get; set; }

    /// <summary>Gets or sets. 最初の開始日時。再実行でも保持します。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Gets or sets. 最新の再実行開始日時です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? RestartedAt { get; set; }

    /// <summary>Gets or sets. 運用操作で上書きした保留属性。nullは定義に従います。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? HoldOverride { get; set; }

    /// <summary>Gets or sets. 一時変更前の保留属性です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? BeforeChangeHoldOverride { get; set; }

    /// <summary>Gets or sets. ルートの中断要求。稼働中試行の回収後に中断を確定します。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? InterruptRequested { get; set; }

    /// <summary>Gets or sets. 元試行を変更せず保持する手動の終了コードです。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? ManualExitCode { get; set; }

    /// <summary>Gets or sets. この世代に一時設定した遅延監視です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DelayMonitorSettings? DelayMonitor { get; set; }

    /// <summary>Gets or sets. 通常予定を再計算する版。0は旧世代IDを維持します。</summary>
    public long GenerationRevision { get; set; }

    /// <summary>Gets or sets a value indicating whether. 個別運用操作を含む世代は定義反映で置換しません。</summary>
    public bool IndividuallyModified { get; set; }
}
