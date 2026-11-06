namespace JobRunner.Controllers.Ui;

/// <summary>省略と明示値を区別する予定・カレンダー属性です。</summary>
/// <param name="Id">対象ユニットの安定IDです。</param>
/// <param name="Attributes">指定した属性だけを持ち、繰り返し順序を保持します。</param>
public sealed record EditorAttributes(string Id, Dictionary<string, List<string>> Attributes);
