using Azure;
using Azure.Data.Tables;
using JobRunner.Models;
using JobRunner.Storage;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Services;

/// <summary>99行ずつの操作を再開し、途中の部分状態ではジョブを開始させません。</summary>
/// <param name="storage">変更計画と実行状態の保存先です。</param>
/// <param name="clock">操作の完了時刻を記録します。</param>
public sealed class OperationMutationService(StorageRepository storage, TimeProvider clock)
{
    /// <summary>操作参照を保存してから適用を始め、応答喪失時も同じ計画で再開します。</summary>
    /// <param name="row">受付済み操作です。</param>
    /// <param name="operation">操作の要求内容です。</param>
    /// <param name="plan">すべての変更後データと変更前ETagです。</param>
    /// <param name="commit">稼働系の条件付き更新経路です。</param>
    /// <param name="ensure">Blob保存前の権限確認です。</param>
    /// <param name="stop">この所有者のプロセスだけに停止を通知します。</param>
    /// <param name="token">権限喪失または停止の通知です。</param>
    /// <returns>変更と操作完了の保存を表します。</returns>
    public async Task ApplyAsync(TableEntity row, Operation operation, OperationMutation plan, Func<IEnumerable<TableTransactionAction>, CancellationToken, Task> commit, Action ensure, Func<string, Task> stop, CancellationToken token)
    {
        var data = BinaryData.FromObjectAsJson(plan);
        string path = $"mutations/{storage.Partition}/{StorageRepository.Hash(operation.Id)}/{StorageRepository.Hash(data.ToArray())}.json";
        ensure();
        await storage.PutImmutableAsync(path, data, token);
        operation.MutationPath = path;
        await commit([new TableTransactionAction(TableTransactionActionType.UpdateReplace, storage.Entity(row.RowKey, operation), row.ETag)], token);
        await ResumeAsync((await storage.GetAsync(row.RowKey, token))!, commit, stop, token);
    }

    /// <summary>更新済み行を識別して残りだけ適用し、ETag不一致を無視しません。</summary>
    /// <param name="row">計画への参照を持つPending操作です。</param>
    /// <param name="commit">leader行を含める更新経路です。</param>
    /// <param name="stop">所有するプロセスの停止通知です。</param>
    /// <param name="token">権限喪失または停止の通知です。</param>
    /// <returns>分割操作の完了を表します。</returns>
    public async Task ResumeAsync(TableEntity row, Func<IEnumerable<TableTransactionAction>, CancellationToken, Task> commit, Func<string, Task> stop, CancellationToken token)
    {
        var operation = StorageRepository.Read<Operation>(row);
        var data = await storage.Container.GetBlobClient(operation.MutationPath).DownloadContentAsync(token);
        var plan = data.Value.Content.ToObjectFromJson<OperationMutation>();
        if (plan is null || plan.FormatVersion != 1 || plan.OperationId != operation.Id)
        {
            throw new InvalidDataException("操作の変更計画が欠損または不一致です。");
        }

        foreach (string id in plan.StopIds)
        {
            await stop(id);
        }

        var batch = new List<TableTransactionAction>();
        foreach (var change in plan.Changes)
        {
            var current = await storage.GetAsync(change.Key, token) ?? throw new InvalidDataException("操作対象が欠損しています。");
            if (current.GetString("Json") == change.Json)
            {
                continue;
            }

            if (current.ETag.ToString() != change.ETag)
            {
                throw new InvalidDataException("操作の途中に対象行が変更されました。自動上書きはしません。");
            }

            current["Json"] = change.Json;
            batch.Add(new TableTransactionAction(TableTransactionActionType.UpdateReplace, current, new ETag(change.ETag)));
            if (batch.Count == 99)
            {
                await commit(batch, token);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await commit(batch, token);
        }

        operation.Status = "Completed";
        operation.CompletedAt = clock.GetUtcNow();
        await commit([new TableTransactionAction(TableTransactionActionType.UpdateReplace, storage.Entity(row.RowKey, operation), row.ETag)], token);
    }
}
