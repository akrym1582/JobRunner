namespace JobRunner.Scheduling;

/// <summary>業務日と実日時を分けた予定計算結果です。</summary>
/// <param name="Rule">予定を作ったルール番号です。</param>
/// <param name="BusinessDate">カレンダー評価に使った業務日です。</param>
/// <param name="Due">タイムゾーンを適用したUTC実日時です。</param>
public sealed record SchedulePoint(int Rule, DateOnly BusinessDate, DateTimeOffset Due);
