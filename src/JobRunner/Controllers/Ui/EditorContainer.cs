namespace JobRunner.Controllers.Ui;

/// <summary>新規ルートを安全に挿入できる原文の位置です。</summary>
/// <param name="SourceIndex">原文一覧の位置です。</param>
/// <param name="ParentPath">配置先または同じ原文内のグループです。</param>
public sealed record EditorContainer(int SourceIndex, string ParentPath);
