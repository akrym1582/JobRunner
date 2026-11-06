using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>図の接続です。</summary>
/// <param name="Id">Idです。</param>
/// <param name="From">Fromです。</param>
/// <param name="To">Toです。</param>
/// <param name="Kind">Kindです。</param>
public sealed record Edge(string Id, string From, string To, string Kind);
