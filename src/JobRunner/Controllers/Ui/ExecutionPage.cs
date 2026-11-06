using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>実行回のページです。</summary>
/// <param name="Items">Itemsです。</param>
/// <param name="Total">Totalです。</param>
public sealed record ExecutionPage(List<Occurrence> Items, int Total);
