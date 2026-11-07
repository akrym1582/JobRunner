namespace JobRunner.Controllers.Ui;

/// <summary>同じ親のジョブ・子ネットを別ネットへ移す下書き要求です。</summary>
/// <param name="Draft">現在の下書きです。</param>
/// <param name="Ids">同じ親にある選択ユニットのIDです。</param>
/// <param name="TargetNetworkId">移動先ネットのIDです。</param>
public sealed record EditorMoveSelectionRequest(EditorDraft Draft, List<string> Ids, string TargetNetworkId);
