using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>実行回と固定定義の図です。</summary>
/// <param name="UnitPath">UnitPathです。</param>
/// <param name="DefinitionHash">DefinitionHashです。</param>
/// <param name="Nodes">Nodesです。</param>
/// <param name="Edges">Edgesです。</param>
/// <param name="Root">Rootです。</param>
/// <param name="CanChange">CanChangeです。</param>
/// <param name="CanRestore">CanRestoreです。</param>
public sealed record Graph(string UnitPath, string DefinitionHash, List<Node> Nodes, List<Edge> Edges, Occurrence? Root, bool CanChange, bool CanRestore);
