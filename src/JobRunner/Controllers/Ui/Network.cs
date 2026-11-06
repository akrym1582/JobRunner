using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>ネットの階層です。</summary>
/// <param name="Path">Pathです。</param>
/// <param name="Name">Nameです。</param>
/// <param name="Parent">Parentです。</param>
public sealed record Network(string Path, string Name, string Parent);
