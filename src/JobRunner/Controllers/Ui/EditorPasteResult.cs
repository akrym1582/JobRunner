namespace JobRunner.Controllers.Ui;

/// <summary>確認可能な複製結果です。反映はクライアントの一つのundo操作です。</summary>
/// <param name="Document">複製後の下書きと原文所属です。</param>
/// <param name="ClonedIds">新しい安定IDです。</param>
/// <param name="TargetPath">選択するネットです。</param>
/// <param name="Warnings">除外した外部接続と継承の変更です。</param>
public sealed record EditorPasteResult(EditorDocument Document, List<string> ClonedIds, string TargetPath, List<string> Warnings);
