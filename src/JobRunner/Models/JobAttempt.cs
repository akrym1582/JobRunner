using System.Text.Json.Serialization;

namespace JobRunner.Models;

/// <summary>
/// プロセス起動の試行を世代と分離し、再実行で過去の結果を上書きしません。
/// </summary>
public sealed class JobAttempt
{
    /// <summary>
    /// Gets or sets. 起動前に発行して保存する試行識別子です。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. この試行の対象世代IDです。
    /// </summary>
    public string OccurrenceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. OSプロセスを起動したインスタンスの識別子です。
    /// </summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 起動権を取得した稼働系の世代番号です。
    /// </summary>
    public long Epoch { get; set; } = 0;

    /// <summary>
    /// Gets or sets. 起動中・実行中・完了・結果不明を区別する状態です。
    /// </summary>
    public string State { get; set; } = "Starting";

    /// <summary>
    /// Gets or sets. 診断用PID。再起動後のプロセス所有権の証明には使いません。
    /// </summary>
    public int? ProcessId { get; set; } = null;

    /// <summary>
    /// Gets or sets. OSから収集した終了コード。未収集ならnullです。
    /// </summary>
    public int? ExitCode { get; set; } = null;

    /// <summary>
    /// Gets or sets. 試行を作成したUTC日時です。
    /// </summary>
    public DateTimeOffset StartedAt { get; set; } = default;

    /// <summary>
    /// Gets or sets. 結果を収集したUTC日時。結果不明ならnullです。
    /// </summary>
    public DateTimeOffset? FinishedAt { get; set; } = null;

    /// <summary>Gets or sets. 手動解決の根拠。自動収集した結果ではnullです。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResolutionReason { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether. 両出力のBlob保存を完了した場合だけtrueにします。
    /// </summary>
    public bool LogsComplete { get; set; } = false;

    /// <summary>Gets or sets. この試行だけの確定した出力変数です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? OutputVariables { get; set; }

    /// <summary>Gets or sets. OS起動失敗など、確定した詳細状態です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusDetail { get; set; }

    /// <summary>Gets or sets. 出力JSONを受理できなかった理由です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputError { get; set; }
}
