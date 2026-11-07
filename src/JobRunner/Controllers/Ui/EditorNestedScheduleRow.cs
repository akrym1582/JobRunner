using JobRunner.Scheduling;

namespace JobRunner.Controllers.Ui;

/// <summary>一つのルート候補に対する対象ネストの採否です。</summary>
/// <param name="BusinessDate">判定する業務日です。</param>
/// <param name="Root">ルート候補。予定のない日はnullです。</param>
/// <param name="Selected">採用したネスト予定。対象外ならnullです。</param>
/// <param name="Eligible">スケジュール条件上の対象かです。</param>
/// <param name="Reason">編集画面用の採否理由です。</param>
/// <param name="Chain">ルートから対象までの判定経路です。</param>
public sealed record EditorNestedScheduleRow(DateOnly BusinessDate, SchedulePoint? Root, SchedulePoint? Selected, bool Eligible, string Reason, List<EditorNestedScheduleStep> Chain);
