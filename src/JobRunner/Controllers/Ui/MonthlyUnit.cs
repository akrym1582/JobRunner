using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>月間一覧の階層行です。</summary>
/// <param name="Path">Pathです。</param>
/// <param name="Name">Nameです。</param>
/// <param name="Parent">Parentです。</param>
/// <param name="Type">Typeです。</param>
/// <param name="Comment">Commentです。</param>
/// <param name="IsNetwork">IsNetworkです。</param>
public sealed record MonthlyUnit(string Path, string Name, string Parent, string Type, string Comment, bool IsNetwork);
