namespace JobRunner.Controllers.Ui;

/// <summary>直下のノードID間の接続です。</summary>
/// <param name="From">接続元IDです。</param>
/// <param name="To">接続先IDです。</param>
/// <param name="Conditional">条件接続かどうかです。</param>
public sealed record EditorEdge(string From, string To, bool Conditional);
