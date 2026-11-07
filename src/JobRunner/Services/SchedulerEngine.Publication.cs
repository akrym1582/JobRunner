using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs.Models;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Storage;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Services;

/// <summary>同じ採用領域で定義を反映し、通常予定だけを再生成します。</summary>
public sealed partial class SchedulerEngine
{
    private async Task PublishAsync(TableEntity commandRow, Operation operation, CancellationToken token)
    {
        var next = await definitions.ReadSourcesAsync(operation.Target, token);
        DefinitionPublication.ValidateCapacity(next);
        var activeRow = await storage.GetAsync("definition-active", token);
        var registrations = active is null ? [] : await storage.ListAsync($"registration:{active.Id}:", token);
        var occurrences = (await storage.ListAsync("occurrence:", token)).Where(r => StorageRepository.Read<Occurrence>(r).Namespace == active?.Id).ToList();
        if (operation.ExpectedDefinitionRevision is long expected && expected != (active?.Revision ?? 0))
        {
            throw new FormatException("運用定義の版が変わりました。差分を再確認してください。");
        }

        var now = clock.GetUtcNow();
        if (operation.ExpectedImpactHash is not null && operation.ExpectedImpactHash != DefinitionPublication.ImpactHash(activeRow, registrations, occurrences, now))
        {
            throw new FormatException("確認後に登録・実行状態が変わりました。反映の影響を再確認してください。");
        }

        if (active?.DefinitionHash == next.Hash)
        {
            operation.Status = "Completed";
            operation.CompletedAt = clock.GetUtcNow();
            operation.Result = active.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await CommitAsync([Update(commandRow, operation)], token);
            return;
        }

        var updated = new Activation { Id = active?.Id ?? Guid.NewGuid().ToString("N"), Revision = (active?.Revision ?? 0) + 1, DefinitionHash = next.Hash, SettingsHash = definitions.SettingsHash(), Phase = "Ready", Baseline = active?.Baseline ?? now };
        var plan = new OperationMutation { FormatVersion = 2, OperationId = operation.Id };
        var roots = DefinitionPublication.Roots(next);
        var byRegistration = registrations.ToDictionary(r => StorageRepository.Read<Registration>(r).Id, r => StorageRepository.Read<Registration>(r), StringComparer.Ordinal);
        var slotChanges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in occurrences.GroupBy(r => StorageRepository.Read<Occurrence>(r).RootId))
        {
            var members = group.Select(StorageRepository.Read<Occurrence>).ToList();
            var root = members.Single(o => o.Id == o.RootId);
            var registration = byRegistration[root.RegistrationId];
            bool replace = !registration.Disabled && !registration.ReplenishmentStopped && registration.Kind != "Immediate" && DefinitionPublication.Protection(members, now) is null;
            if (replace)
            {
                foreach (var row in group)
                {
                    var value = StorageRepository.Read<Occurrence>(row);
                    value.State = "Superseded";
                    value.FinishedAt = now;
                    value.Reason = "運用定義の反映により通常の未開始予定を置換しました。";
                    plan.Changes.Add(Change(row, value));
                }
            }
            else if (root.State != "Superseded")
            {
                string key = DefinitionPublication.SlotKey(registration, root.Rule, root.BusinessDate, root.OriginalDue);
                if (slotChanges.Add(key) && await storage.GetAsync(key, token) is null)
                {
                    plan.Changes.Add(new ArchivedRow(key, string.Empty, JsonSerializer.Serialize(root.Id)));
                }
            }
        }

        foreach (var row in registrations)
        {
            var value = StorageRepository.Read<Registration>(row);
            if (value.Disabled || value.Kind == "Immediate")
            {
                continue;
            }

            if (!roots.ContainsKey(value.RootPath))
            {
                value.ReplenishmentStopped = true;
            }
            else
            {
                value.ReplenishmentStopped = false;
                value.DefinitionHash = next.Hash;
                value.GenerationRevision = updated.Revision;
                value.Through = value.Start ?? BusinessToday(now).AddDays(-3);
                value.ExhaustedOn = null;

                // 間隔の基点と明示保留は維持し、同じ原予定枠の抑止記録で重複を防ぎます。
                value.NextIntervalDue = value.FixedDue;
            }

            plan.Changes.Add(Change(row, value));
        }

        foreach (var root in roots.Values)
        {
            string id = StorageRepository.Hash(root.Path);
            if (byRegistration.ContainsKey(id))
            {
                continue;
            }

            var day = BusinessToday(now);
            var value = new Registration { Id = id, Namespace = updated.Id, RootPath = root.Path, DefinitionHash = next.Hash, GenerationRevision = updated.Revision, Anchor = day, Through = day.AddDays(-1), FutureGenerations = options.ConfirmedFutureGenerations, CreatedAt = now };
            plan.Changes.Add(new ArchivedRow($"registration:{updated.Id}:{id}", string.Empty, JsonSerializer.Serialize(value)));
        }

        plan.Changes.Add(activeRow is null ? new ArchivedRow("definition-active", string.Empty, JsonSerializer.Serialize(updated)) : Change(activeRow, updated));
        operation.Result = updated.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await new OperationMutationService(storage, clock).ApplyAsync(commandRow, operation, plan, CommitAsync, leader!.Ensure, StopOwnedAsync, token, FinalizePublicationAsync);
    }

    private async Task FinalizePublicationAsync(CancellationToken token)
    {
        active = StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", token)) ?? throw new InvalidDataException("反映先の採用行が欠損しています。"));
        if (active.SettingsHash != definitions.SettingsHash() || active.FormatVersion != 6 || active.Phase != "Ready")
        {
            throw new InvalidDataException("反映計画の保存形式・実行設定が現在の実行器と一致しません。補充せず停止します。");
        }

        bundle = await definitions.ReadAsync(active.DefinitionHash, token);
        await RepairIncompleteGenerationsAsync(token);
        foreach (var row in await storage.ListAsync($"registration:{active.Id}:", token))
        {
            var registration = StorageRepository.Read<Registration>(row);
            if (!registration.Disabled && !registration.ReplenishmentStopped && registration.Kind != "Immediate" && registration.FixedDue is DateTimeOffset due && registration.IntervalMinutes is null)
            {
                var units = await ReadUnitsAsync(registration.DefinitionHash, token);
                await CreateOccurrenceAsync(registration, new SchedulePoint(1, BusinessToday(due), due), units[registration.RootPath], token);
            }
            else
            {
                await ReplenishAsync(row, token);
            }
        }

        // 操作がPendingの間は読取APIも部分状態を完成扱いしません。鏡の更新後にだけ完了します。
        var mirror = storage.Container.GetBlobClient($"management/{storage.Partition}/active.json");
        var condition = new BlobRequestConditions { IfNoneMatch = ETag.All };
        if ((await mirror.ExistsAsync(token)).Value)
        {
            var saved = await mirror.DownloadContentAsync(token);
            var previous = saved.Value.Content.ToObjectFromJson<Activation>();
            if (previous is null || previous.FormatVersion is < 1 or > 6 || previous.Id != active.Id || previous.SettingsHash != active.SettingsHash || previous.Phase != "Ready")
            {
                throw new InvalidDataException("管理Blobと反映先が一致しません。破損状態を上書きせず復元してください。");
            }

            condition = new BlobRequestConditions { IfMatch = saved.Value.Details.ETag };
        }

        leader!.Ensure();
        await mirror.UploadAsync(BinaryData.FromObjectAsJson(active), new BlobUploadOptions { Conditions = condition }, token);
        await storage.PutImmutableAsync($"management/{storage.Partition}/{active.DefinitionHash}/{active.SettingsHash}/{active.Id}/activation-v6-{active.Revision}.json", BinaryData.FromObjectAsJson(active), token);
    }
}
