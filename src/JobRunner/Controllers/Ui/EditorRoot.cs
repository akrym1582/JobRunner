namespace JobRunner.Controllers.Ui;

/// <summary>親ネットを持たないネットの下書きと原文への配置です。</summary>
/// <param name="Node">安定IDを持つルートネットです。</param>
/// <param name="SourceIndex">原文一覧の位置です。既存ネットでは変更できません。</param>
/// <param name="ParentPath">配置先または原文内の親グループです。</param>
public sealed record EditorRoot(EditorNode Node, int SourceIndex, string ParentPath);
