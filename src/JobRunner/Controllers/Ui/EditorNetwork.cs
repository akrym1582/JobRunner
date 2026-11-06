namespace JobRunner.Controllers.Ui;

/// <summary>一つの既存ネットの下書きです。</summary>
/// <param name="Path">変更しないネット完全名です。</param>
/// <param name="Nodes">直下ユニットです。</param>
/// <param name="Edges">接続です。</param>
public sealed record EditorNetwork(string Path, List<EditorNode> Nodes, List<EditorEdge> Edges);
