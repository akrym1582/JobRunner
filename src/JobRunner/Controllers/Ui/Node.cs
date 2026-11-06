using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>図のノードです。</summary>
/// <param name="Path">Pathです。</param>
/// <param name="Name">Nameです。</param>
/// <param name="Type">Typeです。</param>
/// <param name="IsNetwork">IsNetworkです。</param>
/// <param name="IsRecovery">IsRecoveryです。</param>
/// <param name="Status">Statusです。</param>
/// <param name="Execution">Executionです。</param>
/// <param name="ExitCode">ExitCodeです。</param>
/// <param name="X">定義の水平位置（ピクセル）です。</param>
/// <param name="Y">定義の垂直位置（ピクセル）です。</param>
public sealed record Node(string Path, string Name, string Type, bool IsNetwork, bool IsRecovery, string Status, Occurrence? Execution, int? ExitCode, int X, int Y);
