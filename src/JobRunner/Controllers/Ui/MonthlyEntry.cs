using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>保存済みの実行回と表示用状態です。</summary>
/// <param name="Execution">Executionです。</param>
/// <param name="Status">Statusです。</param>
/// <param name="ExecutionId">ExecutionIdです。</param>
/// <param name="NetworkPath">NetworkPathです。</param>
/// <param name="CalendarDate">CalendarDateです。</param>
/// <param name="Comment">Commentです。</param>
/// <param name="ExitCode">ExitCodeです。</param>
public sealed record MonthlyEntry(Occurrence Execution, string Status, string ExecutionId, string NetworkPath, string CalendarDate, string Comment, int? ExitCode);
