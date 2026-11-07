namespace JobRunner.Controllers.Ui;

/// <summary>コピー時点の原文を現在の下書きへ複製する要求です。</summary>
/// <param name="Draft">貼り付け先の現在の内容です。</param>
/// <param name="CopiedDraft">コピー時点の内容です。</param>
/// <param name="Ids">同じ親の選択ノードです。</param>
/// <param name="TargetNetworkId">子ノードの配置先。ルート複製ではnullです。</param>
/// <param name="SourceIndex">ルート複製先のファイルです。</param>
/// <param name="ParentPath">ルート複製先のグループです。</param>
/// <param name="TargetFile">ファイル追加後の並び替えに影響されない配置先ファイル名です。</param>
public sealed record EditorPasteRequest(EditorDraft Draft, EditorDraft CopiedDraft, List<string> Ids, string? TargetNetworkId = null, int? SourceIndex = null, string? ParentPath = null, string? TargetFile = null);
