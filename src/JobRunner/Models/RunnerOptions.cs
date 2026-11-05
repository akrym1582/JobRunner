namespace JobRunner.Models;

/// <summary>
/// 起動設定を保持し、入力定義と管理情報の保存先を分離します。
/// </summary>
public sealed class RunnerOptions
{
    /// <summary>
    /// Gets or sets. BlobとTableの接続文字列。ログやAPIへ返さない秘密設定です。
    /// </summary>
    public string ConnectionString { get; set; } = "UseDevelopmentStorage=true";

    /// <summary>
    /// Gets or sets. 定義スナップショットとログを保存する専用コンテナーです。
    /// </summary>
    public string ManagementContainer { get; set; } = "jobrunner-management";

    /// <summary>
    /// Gets or sets. 状態とleader行を同じパーティションへ格納するテーブル名です。
    /// </summary>
    public string TableName { get; set; } = "JobRunner";

    /// <summary>
    /// Gets or sets. 全インスタンスが共有する排他と状態の識別子です。
    /// </summary>
    public string SchedulerId { get; set; } = "default";

    /// <summary>
    /// Gets or sets. 業務日を実日時へ変換する明示的なタイムゾーンです。
    /// </summary>
    public string TimeZone { get; set; } = "Asia/Tokyo";

    /// <summary>
    /// Gets or sets. ルート予定の時間制。24または48を指定します。
    /// </summary>
    public int ScheduleTimeMode { get; set; } = 24;

    /// <summary>
    /// Gets or sets. 自動確定登録を先読みする日数です。
    /// </summary>
    public int ConfirmedHorizonDays { get; set; } = 32;

    /// <summary>
    /// Gets or sets. 同一インスタンスで並行実行できるプロセス数の上限です。
    /// </summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>
    /// Gets or sets. 配置先と入力Blobの対応。配列の順番に意味はありません。
    /// </summary>
    public List<DefinitionSource> DefinitionSources { get; set; } = [];

    /// <summary>
    /// Gets or sets. ローカル実行へ明示的に対応付ける実行エージェント名です。
    /// </summary>
    public List<string> LocalAgents { get; set; } = [];

    /// <summary>Gets or sets. 履歴をBlobへ退避するまでの日数。nullは自動退避しません。</summary>
    public int? HistoryArchiveAfterDays { get; set; } = null;

    /// <summary>Gets or sets. 結果が確定した試行ログの保持日数。Unknownは期限に関係なく保持します。</summary>
    public int? LogRetentionDays { get; set; } = null;

    /// <summary>Gets or sets. 完了した操作本文を退避する日数。再送判定の墓標は削除しません。</summary>
    public int? OperationArchiveAfterDays { get; set; } = null;

    /// <summary>Gets or sets. 所有者実行指定を現在のローカルOSユーザーへ明示的に対応付けるJP1ユーザー名です。</summary>
    public List<string> LocalUsers { get; set; } = [];

    /// <summary>Gets or sets. 互換APIで受け付けるローカルマネージャー名。別名へのリモート実行はしません。</summary>
    public string Jp1ManagerName { get; set; } = "localhost";

    /// <summary>Gets or sets. 自動確定登録で維持する未来世代数。nullは日数方式を維持します。</summary>
    public int? ConfirmedFutureGenerations { get; set; } = null;
}
