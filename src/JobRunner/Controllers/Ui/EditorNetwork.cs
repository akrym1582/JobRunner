namespace JobRunner.Controllers.Ui;

/// <summary>一つのネットの下書きです。</summary>
/// <param name="Path">改名前の完全名またはnew:で始まる安定IDです。</param>
/// <param name="Nodes">直下ユニットです。</param>
/// <param name="Edges">接続です。</param>
public sealed record EditorNetwork(string Path, List<EditorNode> Nodes, List<EditorEdge> Edges);
