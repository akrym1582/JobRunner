namespace JobRunner.Controllers.Ui;

/// <summary>本文の取得量を制限したログページです。</summary>
/// <param name="Chunks">最大16個のログ片です。</param>
/// <param name="PreviousCursor">前のページです。先頭ではnullです。</param>
/// <param name="NextCursor">追加分を取得する位置です。</param>
/// <param name="HasMore">後続に保存済みのログがあるかどうかです。</param>
/// <param name="LogsComplete">両ストリームの保存が完了したかどうかです。</param>
/// <param name="State">試行の状態です。</param>
public sealed record LogPage(List<LogChunk> Chunks, string? PreviousCursor, string NextCursor, bool HasMore, bool LogsComplete, string State);
