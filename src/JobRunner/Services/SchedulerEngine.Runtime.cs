using System.Text.Json;
using Azure.Data.Tables;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Storage;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Services;

/// <summary>入力ファイルから独立した起動と、永続的な全体停止を担当します。</summary>
public sealed partial class SchedulerEngine
{
    private RuntimeControl runtime = new();

    private static ArchivedRow Change<T>(TableEntity row, T value) => new(row.RowKey, row.ETag.ToString(), JsonSerializer.Serialize(value));

    private static string GenerationId(Registration registration, SchedulePoint point)
    {
        string legacy = StorageRepository.Hash(JsonSerializer.Serialize(new { registration.Namespace, registration.Id, point.Rule, point.BusinessDate, point.Due }));
        return registration.GenerationRevision == 0 ? legacy : StorageRepository.Hash(legacy + ":" + registration.GenerationRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task RestoreRuntimeAsync(CancellationToken token)
    {
        var row = await storage.GetAsync("runtime-control", token);
        var saved = await storage.GetAsync("definition-active", token);
        if (row is null)
        {
            if (saved is not null && StorageRepository.Read<Activation>(saved).FormatVersion >= 6)
            {
                throw new InvalidDataException("全体停止の保存状態が欠損しています。自動再開はしません。");
            }

            runtime = new RuntimeControl { Paused = saved is null, PausedAt = saved is null ? clock.GetUtcNow() : null };
            await CommitAsync([Add("runtime-control", runtime)], token);
        }
        else
        {
            runtime = StorageRepository.Read<RuntimeControl>(row);
        }
    }

    private async Task EnsureEmptyInstallationAsync(CancellationToken token)
    {
        if ((await storage.Container.GetBlobClient($"management/{storage.Partition}/active.json").ExistsAsync(token)).Value
            || (await storage.ListAsync("registration:", token)).Count != 0
            || (await storage.ListAsync("occurrence:", token)).Count != 0)
        {
            throw new InvalidDataException("運用データがあるのに有効定義が欠損しています。初期化せず復元してください。");
        }
    }

    private async Task PinLegacyDefinitionsAsync(Activation activation, CancellationToken token)
    {
        // 全プロセス停止後の移行です。形式番号を最後に進めるため、中断後も同じ版へ固定できます。
        var changes = new List<TableTransactionAction>();
        foreach (var row in await storage.ListAsync("registration:" + activation.Id + ":", token))
        {
            var value = StorageRepository.Read<Registration>(row);
            if (value.DefinitionHash is null)
            {
                value.DefinitionHash = activation.DefinitionHash;
                changes.Add(Update(row, value));
            }
        }

        foreach (var row in await storage.ListAsync("occurrence:", token))
        {
            var value = StorageRepository.Read<Occurrence>(row);
            if (value.Namespace == activation.Id)
            {
                value.DefinitionHash ??= activation.DefinitionHash;

                // 旧形式には個別保留と登録時の明示保留を区別する印がないため、上書きせず保護します。
                var units = await ReadUnitsAsync(value.DefinitionHash, token);
                var definedMonitor = units[value.UnitPath].IsNetwork() ? DefinitionDelayMonitor.Read(units[value.UnitPath], value.Rule) : null;
                value.IndividuallyModified |= value.HoldOverride is not null || JsonSerializer.Serialize(value.DelayMonitor) != JsonSerializer.Serialize(definedMonitor);
                changes.Add(Update(row, value));
            }
        }

        foreach (var row in await storage.ListAsync("command:", token))
        {
            var operation = StorageRepository.Read<Operation>(row);
            if (operation.StateNamespace == activation.Id && operation.Status == "Pending" && operation.Kind == "Register" && operation.AcceptedDefinitionRevision is null)
            {
                operation.AcceptedDefinitionRevision = activation.Revision;
                changes.Add(Update(row, operation));
            }
        }

        foreach (var batch in changes.Chunk(99))
        {
            await CommitAsync(batch, token);
        }
    }

    private async Task ApplyRuntimeAsync(TableEntity commandRow, Operation operation, CancellationToken token)
    {
        var row = await storage.GetAsync("runtime-control", token) ?? throw new InvalidDataException("全体停止の状態が欠損しています。");
        var control = StorageRepository.Read<RuntimeControl>(row);
        if (operation.ExpectedControlRevision != control.Revision)
        {
            throw new FormatException("停止状態の版が変わりました。状態を再確認してください。");
        }

        bool pause = operation.Kind == "PauseAll";
        if (!pause && active is null)
        {
            throw new FormatException("運用定義を反映してから再開してください。");
        }

        var plan = new OperationMutation { FormatVersion = 2, OperationId = operation.Id };
        if (control.Paused != pause)
        {
            control.Revision++;
            control.Paused = pause;
            if (pause)
            {
                control.PausedAt = clock.GetUtcNow();
            }
            else
            {
                var cutoff = clock.GetUtcNow();
                control.SkipBefore = control.SkipBefore > cutoff ? control.SkipBefore : cutoff;
                foreach (var occurrenceRow in await storage.ListAsync("occurrence:", token))
                {
                    var value = StorageRepository.Read<Occurrence>(occurrenceRow);
                    if (value.Namespace == active!.Id && value.Id == value.RootId && !value.Started && value.State is "Waiting" or "Held" && value.Due < control.SkipBefore)
                    {
                        value.State = "Skipped";
                        value.FinishedAt = cutoff;
                        value.Reason = "全体停止の再開時刻より前の未開始予定です。";
                        plan.Changes.Add(Change(occurrenceRow, value));
                    }
                }
            }
        }

        plan.Changes.Add(Change(row, control));
        operation.Result = pause ? "Paused" : "Running";
        await new OperationMutationService(storage, clock).ApplyAsync(commandRow, operation, plan, CommitAsync, leader!.Ensure, StopOwnedAsync, token);
        runtime = control;
    }
}
