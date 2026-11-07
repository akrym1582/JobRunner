namespace JobRunner.Scheduling;

/// <summary>親から受け継いだ業務日とネストの予定採否です。理由は編集用の説明でJP1状態ではありません。</summary>
/// <param name="Point">採用予定。対象外では親の基準を保持します。</param>
/// <param name="Enabled">スケジュール条件を満たすかです。</param>
/// <param name="Reason">採否の説明コードです。</param>
public sealed record NestedScheduleSelection(SchedulePoint Point, bool Enabled, string Reason);
