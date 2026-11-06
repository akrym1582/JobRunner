using JobRunner.Scheduling;

namespace JobRunner.Controllers.Ui;

/// <summary>実行予定や登録済み世代とは異なる下書きの計算結果です。</summary>
/// <param name="Points">ルール番号・業務日・実日時を持つ候補です。</param>
/// <param name="TimeZone">表示用の時間帯です。</param>
public sealed record EditorSchedulePreview(List<SchedulePoint> Points, string TimeZone);
