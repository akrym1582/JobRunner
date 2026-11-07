using JobRunner.Scheduling;

namespace JobRunner.Controllers.Ui;

/// <summary>日付ごとの実効値と継承元です。</summary>
/// <param name="Date">業務日です。</param>
/// <param name="Evaluation">実行エンジンの評価です。</param>
public sealed record EditorCalendarDay(DateOnly Date, CalendarEvaluation Evaluation);
