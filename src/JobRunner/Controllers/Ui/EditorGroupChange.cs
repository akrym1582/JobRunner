namespace JobRunner.Controllers.Ui;

/// <summary>元の完全名を安定IDとする、同じ親でのグループ改名または子孫削除です。</summary>
/// <param name="Id">追加適用後・改名前のグループ完全名です。</param>
/// <param name="Name">改名後の名前です。削除時は省略します。</param>
/// <param name="Delete">子孫を含めて削除するかどうかです。</param>
public sealed record EditorGroupChange(string Id, string? Name = null, bool Delete = false);
