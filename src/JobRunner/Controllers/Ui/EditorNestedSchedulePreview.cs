namespace JobRunner.Controllers.Ui;

/// <summary>上位の予定と組み合わせたネストの下書き試算です。登録済み世代ではありません。</summary>
/// <param name="RootNetworkId">ルートの安定IDです。</param>
/// <param name="RootPath">編集後のルート完全名です。</param>
/// <param name="Rows">ルート候補ごと、またはルート予定のない業務日の採否です。</param>
/// <param name="TimeZone">表示用時間帯です。</param>
public sealed record EditorNestedSchedulePreview(string RootNetworkId, string RootPath, List<EditorNestedScheduleRow> Rows, string TimeZone);
