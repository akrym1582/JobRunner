using System.Globalization;
using System.Text.Json;
using Azure.Data.Tables;
using JobRunner.Models;
using JobRunner.Storage;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Services;

/// <summary>実行結果を推測せずに中断・監視・手動状態を変更します。</summary>
public sealed partial class SchedulerEngine
{
    /// <summary>対象状態を確認してから、共通の永続変更計画へ渡します。</summary>
    /// <param name="command">受付済み操作行です。</param>
    /// <param name="operation">操作要求です。</param>
    /// <param name="row">対象の世代行です。</param>
    /// <param name="target">対象の保存状態です。</param>
    /// <param name="token">停止通知です。</param>
    /// <returns>この操作を処理した場合trueです。</returns>
    private async Task<bool> ApplyJp1OperationAsync(TableEntity command, Operation operation, TableEntity row, Occurrence target, CancellationToken token)
    {
        if (operation.Kind is not ("Interrupt" or "ChangeStatus" or "ChangeDelayMonitor"))
        {
            return false;
        }

        var units = await ReadUnitsAsync(target.DefinitionHash, token);
        var unit = units[target.UnitPath];
        var plan = new OperationMutation { OperationId = operation.Id };
        if (operation.Kind == "Interrupt")
        {
            if (target.Id != target.RootId || !unit.IsNetwork() || target.State != "Running")
            {
                throw new FormatException("中断は実行中のルートネットだけに適用できます。");
            }

            target.InterruptRequested = true;
            foreach (var childRow in await storage.ListAsync("occurrence:", token))
            {
                var child = StorageRepository.Read<Occurrence>(childRow);
                if (child.RootId == target.Id && child.Id != target.Id && child.State is "Waiting" or "Held")
                {
                    child.State = "NotScheduled";
                    child.FinishedAt = clock.GetUtcNow();
                    child.Reason = "ルート中断要求により後続を開始しません。";
                    plan.Changes.Add(Change(childRow, child));
                }
            }
        }
        else if (operation.Kind == "ChangeDelayMonitor")
        {
            var request = operation.DelayMonitor ?? throw new FormatException("監視の変更項目が必要です。");
            if (!unit.IsNetwork() || target.State is not ("Waiting" or "Held" or "Running") || (target.Started && request.Start is not null)
                || (request.Start is null && request.End is null && request.Duration is null))
            {
                throw new FormatException("監視設定を変更できるネット状態ではありません。");
            }

            foreach (var (mode, minutes) in new[] { (request.Start, request.StartMinutes), (request.End, request.EndMinutes) })
            {
                if (mode is not (null or "NOT" or "ABSOLUTE" or "ROOT" or "TOP" or "OWN")
                    || (mode is not (null or "NOT") && (minutes is null or < 0 or > 2879 || (mode != "ABSOLUTE" && minutes == 0)))
                    || (target.Id == target.RootId && mode is "ROOT" or "TOP"))
                {
                    throw new FormatException("監視方式または時刻が不正です。");
                }
            }

            if (request.Duration == true && request.DurationMinutes is null or < 1 or > 2879)
            {
                throw new FormatException("所要時間は1〜2879分です。");
            }

            var settings = target.DelayMonitor ?? new DelayMonitorSettings();
            if (request.Start is not null)
            {
                settings.Start = request.Start;
                settings.StartMinutes = request.Start == "NOT" ? null : request.StartMinutes;
            }

            if (request.End is not null)
            {
                settings.End = request.End;
                settings.EndMinutes = request.End == "NOT" ? null : request.EndMinutes;
            }

            if (request.Duration.HasValue)
            {
                settings.Duration = request.Duration;
                settings.DurationMinutes = request.Duration == true ? request.DurationMinutes : null;
            }

            target.DelayMonitor = settings;
            _ = DelayMonitoring.Evaluate(target, (await storage.ListAsync("occurrence:", token)).Select(StorageRepository.Read<Occurrence>), options, clock.GetUtcNow());
        }
        else
        {
            if (operation.AttemptId != target.AttemptId || unit.IsNetwork() || target.State is "Starting" or "Running" or "Unknown" or "Archiving" or "Archived" or "Superseded"
                || (operation.NewStatus is null && operation.NewReturnCode is null) || (unit.IsJudgment() && operation.NewStatus is not null))
            {
                throw new FormatException("このジョブの状態は変更できません。結果不明には根拠付きResolveが必要です。");
            }

            if (operation.NewStatus == "RETURNCODE")
            {
                int code = operation.NewReturnCode ?? throw new FormatException("終了コードによる判定にはコードが必要です。");
                int abnormal = int.Parse(unit.Get("tho", "0"), CultureInfo.InvariantCulture);
                int warning = int.Parse(unit.Get("wth", abnormal.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
                target.State = unit.Get("jd", "cod") switch { "nm" => "Succeeded", "ab" => "Failed", _ => code < 0 || code > abnormal ? "Failed" : code > warning ? "Warning" : "Succeeded" };
            }
            else if (operation.NewStatus is not null)
            {
                target.State = operation.NewStatus switch { "NORMAL" => "Succeeded", "WARNING" => "Warning", "FAIL" or "ABNORMAL" => "Failed", "BYPASS" => "NotScheduled", _ => throw new FormatException("変更後の状態が不正です。") };
            }

            target.ManualExitCode = operation.NewReturnCode ?? target.ManualExitCode;
            if (operation.NewStatus is not null)
            {
                target.StatusDetail = operation.NewStatus;
                target.Started = true;
                target.FinishedAt ??= clock.GetUtcNow();
            }

            target.Reason = "明示的な状態変更: " + operation.Id;
        }

        plan.Changes.Add(new ArchivedRow(row.RowKey, row.ETag.ToString(), JsonSerializer.Serialize(target)));
        await new OperationMutationService(storage, clock).ApplyAsync(command, operation, plan, CommitAsync, leader!.Ensure, StopOwnedAsync, token);
        return true;
    }
}
