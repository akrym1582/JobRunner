namespace JobRunner.Controllers.Ui;

/// <summary>登録を行わない下書き予定試算の条件です。</summary>
/// <param name="Draft">試算する下書きです。</param>
/// <param name="NetworkId">ルートネットの安定IDです。</param>
/// <param name="Anchor">省略年月を解決する仮の登録基準日です。</param>
/// <param name="From">開始業務日です。</param>
/// <param name="Until">終了業務日で、この日は含みません。</param>
public sealed record EditorScheduleRequest(EditorDraft Draft, string NetworkId, DateOnly Anchor, DateOnly From, DateOnly Until);
