using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>全体停止と運用定義の状態です。</summary>
/// <param name="Control">適用済みの停止状態です。</param>
/// <param name="Active">未登録時はnullです。</param>
/// <param name="Phase">応答したWorkerの状態です。</param>
/// <param name="Pending">受付済みで未完了の全体制御・反映操作です。</param>
public sealed record RuntimeStatus(RuntimeControl Control, Activation? Active, string Phase, List<Operation> Pending);
