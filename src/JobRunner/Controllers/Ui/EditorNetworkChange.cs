namespace JobRunner.Controllers.Ui;

/// <summary>属性を削除しないネットの移動・種別変更です。</summary>
/// <param name="Id">ネットの安定IDです。</param>
/// <param name="ParentPath">同一原文の移動先グループ。グループ改名前の完全名です。</param>
/// <param name="Type">子ネットの変更後種別n/rnです。</param>
public sealed record EditorNetworkChange(string Id, string? ParentPath = null, string? Type = null);
