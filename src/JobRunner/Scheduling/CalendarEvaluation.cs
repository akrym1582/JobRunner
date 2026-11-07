namespace JobRunner.Scheduling;

/// <summary>実行エンジンが採用したカレンダー指定です。</summary>
/// <param name="Working">稼働日かどうかです。</param>
/// <param name="SourcePath">継承元。既定値の場合はnullです。</param>
/// <param name="Rule">日付・曜日・既定値の区分です。</param>
public readonly record struct CalendarEvaluation(bool Working, string? SourcePath, string Rule);
