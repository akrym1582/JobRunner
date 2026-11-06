namespace JobRunner.Controllers.Ui;

/// <summary>元原文を変更せず下書きへ追加する空グループです。</summary>
/// <param name="SourceIndex">原文または追加ファイルの位置です。</param>
/// <param name="ParentPath">同じ原文のグループまたは配置先です。</param>
/// <param name="Name">新しいグループ名です。</param>
public sealed record EditorGroupCreation(int SourceIndex, string ParentPath, string Name);
