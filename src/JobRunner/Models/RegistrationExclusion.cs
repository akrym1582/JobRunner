namespace JobRunner.Models;

/// <summary>期間解除を補充後も維持する、終了日を含む範囲です。</summary>
/// <param name="DateType">暦日または実行日です。</param>
/// <param name="Begin">開始日です。</param>
/// <param name="End">終了日です。</param>
public sealed record RegistrationExclusion(string DateType, DateOnly Begin, DateOnly End);
