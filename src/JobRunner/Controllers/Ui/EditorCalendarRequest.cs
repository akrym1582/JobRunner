namespace JobRunner.Controllers.Ui;

/// <summary>編集中の定義で月の実効カレンダーを評価します。</summary>
/// <param name="Draft">未反映の定義です。</param>
/// <param name="UnitId">グループまたはネットの安定IDです。</param>
/// <param name="Month">月初の日付です。</param>
public sealed record EditorCalendarRequest(EditorDraft Draft, string UnitId, DateOnly Month);
