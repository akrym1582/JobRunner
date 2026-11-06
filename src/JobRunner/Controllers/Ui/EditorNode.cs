namespace JobRunner.Controllers.Ui;

/// <summary>名前を変えてもIdは元の完全名を保持する編集ノードです。</summary>
/// <param name="Id">元の完全名またはnew:で始まる追加IDです。</param>
/// <param name="Name">編集後の単純名です。</param>
/// <param name="Type">既存種別は変更できません。</param>
/// <param name="Attributes">編集可能な属性だけです。</param>
/// <param name="X">親elの水平位置です。</param>
/// <param name="Y">親elの垂直位置です。</param>
public sealed record EditorNode(string Id, string Name, string Type, Dictionary<string, List<string>> Attributes, int? X, int? Y);
