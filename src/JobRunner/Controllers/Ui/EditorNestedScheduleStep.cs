using JobRunner.Scheduling;

namespace JobRunner.Controllers.Ui;

/// <summary>上位連動試算の各階層の判定です。</summary>
/// <param name="Path">編集後のネット完全名です。</param>
/// <param name="Type">通常またはリカバリーネットの種別です。</param>
/// <param name="Eligible">その階層までのスケジュール条件を満たすかです。</param>
/// <param name="Reason">採否理由です。</param>
/// <param name="Point">採用予定。対象外ならnullです。</param>
public sealed record EditorNestedScheduleStep(string Path, string Type, bool Eligible, string Reason, SchedulePoint? Point);
