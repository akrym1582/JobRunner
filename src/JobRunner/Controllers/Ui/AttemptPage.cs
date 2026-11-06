using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>実行回に所属する試行を新しい順に返します。</summary>
/// <param name="UnitPath">対象ジョブです。</param>
/// <param name="CurrentAttemptId">実行回の現在の試行です。再実行待ちではnullです。</param>
/// <param name="Archived">履歴が退避済みかどうかです。</param>
/// <param name="Items">過去の試行を含む一覧です。</param>
public sealed record AttemptPage(string UnitPath, string? CurrentAttemptId, bool Archived, List<JobAttempt> Items);
