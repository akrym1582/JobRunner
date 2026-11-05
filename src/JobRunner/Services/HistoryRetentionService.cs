using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using JobRunner.Models;
using JobRunner.Storage;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Services;

/// <summary>完了履歴を先に保存し、条件付きでTable詳細と期限切れログを整理します。</summary>
/// <param name="storage">稼働系と同じ制御Tableと管理Blobです。</param>
/// <param name="options">未指定なら自動整理しない保持期間です。</param>
/// <param name="clock">UTCの保持期限を判定する時計です。</param>
public sealed class HistoryRetentionService(StorageRepository storage, RunnerOptions options, TimeProvider clock)
{
    /// <summary>退避した操作も元の結果で返し、墓標を通常の操作結果として公開しません。</summary>
    /// <param name="row">要求IDに対応するTable行です。</param>
    /// <param name="repository">退避先へ接続するStorageです。</param>
    /// <param name="cancellationToken">要求中断の通知です。</param>
    /// <returns>原要求と完了結果です。退避Blob欠損はエラーにします。</returns>
    public static async Task<Operation> ReadOperationAsync(TableEntity row, StorageRepository repository, CancellationToken cancellationToken)
    {
        var operation = StorageRepository.Read<Operation>(row);
        if (operation.ArchivePath is not null)
        {
            var blob = await repository.Container.GetBlobClient(operation.ArchivePath).DownloadContentAsync(cancellationToken);
            return blob.Value.Content.ToObjectFromJson<Operation>() ?? throw new InvalidDataException("操作の退避データが破損しています。");
        }

        return operation;
    }

    /// <summary>完了履歴・ログ・操作をそれぞれの保持期限で整理します。</summary>
    /// <param name="commit">leader行を含む条件付きトランザクションの実行経路です。</param>
    /// <param name="ensureAuthority">Blob変更の前にも呼び出すリース権限確認です。</param>
    /// <param name="cancellationToken">権限喪失またはアプリ停止の通知です。</param>
    /// <returns>今回の期限対象の整理が終わると完了します。</returns>
    public async Task SweepAsync(Func<IEnumerable<TableTransactionAction>, CancellationToken, Task> commit, Action ensureAuthority, CancellationToken cancellationToken)
    {
        var rows = await storage.ListAsync("occurrence:", cancellationToken);
        var attempts = await storage.ListAsync("attempt:", cancellationToken);
        var groups = rows.GroupBy(r => StorageRepository.Read<Occurrence>(r).RootId).ToList();
        foreach (var group in groups)
        {
            var rootRow = group.FirstOrDefault(r => StorageRepository.Read<Occurrence>(r).Id == group.Key);
            if (rootRow is null)
            {
                continue;
            }

            var root = StorageRepository.Read<Occurrence>(rootRow);
            if (root.State is "Archiving" or "Archived")
            {
                var archive = await ReadArchiveAsync(root, cancellationToken);
                if (root.State == "Archiving")
                {
                    await FinishArchiveAsync(rootRow, root, archive, commit, cancellationToken);
                }

                await ExpireLogsAsync(archive.Rows.Where(r => r.Key.StartsWith("attempt:", StringComparison.Ordinal)).Select(r => JsonSerializer.Deserialize<JobAttempt>(r.Json)!), ensureAuthority, cancellationToken);
                continue;
            }

            if (options.HistoryArchiveAfterDays is not int days || !OldEnough(root.FinishedAt, days) || !group.All(r => IsTerminal(StorageRepository.Read<Occurrence>(r).State)))
            {
                continue;
            }

            var ids = group.Select(r => StorageRepository.Read<Occurrence>(r).Id).ToHashSet(StringComparer.Ordinal);
            var relatedAttempts = attempts.Where(r => ids.Contains(StorageRepository.Read<JobAttempt>(r).OccurrenceId)).ToList();
            if (relatedAttempts.Any(r => !IsTerminal(StorageRepository.Read<JobAttempt>(r).State)))
            {
                continue;
            }

            var snapshot = new HistoryArchive
            {
                RootId = root.Id,
                ArchivedAt = clock.GetUtcNow(),
                Rows = group.Concat(relatedAttempts).OrderBy(r => r.RowKey, StringComparer.Ordinal).Select(r => new ArchivedRow(r.RowKey, r.ETag.ToString(), r.GetString("Json"))).ToList(),
            };
            var data = BinaryData.FromObjectAsJson(snapshot);
            string path = $"history/{storage.Partition}/{root.Id}/{StorageRepository.Hash(data.ToArray())}.json";
            ensureAuthority();
            await storage.PutImmutableAsync(path, data, cancellationToken);
            root.State = "Archiving";
            root.ArchivePath = path;
            await commit([Replace(rootRow, root)], cancellationToken);
            rootRow = (await storage.GetAsync(rootRow.RowKey, cancellationToken))!;
            await FinishArchiveAsync(rootRow, root, snapshot, commit, cancellationToken);
            await ExpireLogsAsync(relatedAttempts.Select(StorageRepository.Read<JobAttempt>), ensureAuthority, cancellationToken);
        }

        await ExpireLogsAsync(attempts.Select(StorageRepository.Read<JobAttempt>), ensureAuthority, cancellationToken);
        if (options.OperationArchiveAfterDays is int operationDays)
        {
            foreach (var row in await storage.ListAsync("command:", cancellationToken))
            {
                var operation = StorageRepository.Read<Operation>(row);
                if (operation.Status is not ("Completed" or "Rejected") || operation.ArchivePath is not null || !OldEnough(operation.CompletedAt, operationDays))
                {
                    continue;
                }

                var data = BinaryData.FromObjectAsJson(operation);
                string path = $"operations/{storage.Partition}/{StorageRepository.Hash(operation.Id)}/{StorageRepository.Hash(data.ToArray())}.json";
                ensureAuthority();
                await storage.PutImmutableAsync(path, data, cancellationToken);
                var tombstone = new Operation
                {
                    Id = operation.Id,
                    RequestHash = operation.RequestHash,
                    Status = operation.Status,
                    Result = operation.Result,
                    StateNamespace = operation.StateNamespace,
                    CreatedAt = operation.CreatedAt,
                    CompletedAt = operation.CompletedAt,
                    ArchivePath = path,
                };
                await commit([Replace(row, tombstone)], cancellationToken);
            }
        }
    }

    /// <summary>結果不明と実行可能な状態を、保持期限だけで削除対象にしません。</summary>
    /// <param name="state">保存済みの世代または試行状態です。</param>
    /// <returns>業務結果が確定した状態であればtrueです。</returns>
    private static bool IsTerminal(string state) => state is "Succeeded" or "Warning" or "Failed" or "TimedOut" or "Cancelled" or "Skipped" or "NotScheduled";

    /// <summary>境界時刻を含めて保持日数を満たすか確認します。</summary>
    /// <param name="finished">確定時刻。旧形式などで欠落している場合は整理しません。</param>
    /// <param name="days">正の保持日数です。</param>
    /// <returns>期限に達した場合だけtrueです。</returns>
    private bool OldEnough(DateTimeOffset? finished, int days) => finished is not null && finished <= clock.GetUtcNow().AddDays(-days);

    /// <summary>墓標が参照する退避版を検証して読みます。</summary>
    /// <param name="root">退避中または退避済みルートです。</param>
    /// <param name="cancellationToken">中断の通知です。</param>
    /// <returns>ルートIDと形式版を検証した履歴です。</returns>
    private async Task<HistoryArchive> ReadArchiveAsync(Occurrence root, CancellationToken cancellationToken)
    {
        if (root.ArchivePath is null)
        {
            throw new InvalidDataException("退避先の参照が欠損しています。");
        }

        var data = await storage.Container.GetBlobClient(root.ArchivePath).DownloadContentAsync(cancellationToken);
        var archive = data.Value.Content.ToObjectFromJson<HistoryArchive>();
        if (archive is null || archive.FormatVersion != 1 || archive.RootId != root.Id)
        {
            throw new InvalidDataException("履歴の退避形式またはルートIDが一致しません。");
        }

        return archive;
    }

    /// <summary>保存済みスナップショットのETagを使い、削除を小さく再開可能に進めます。</summary>
    /// <param name="rootRow">退避中ルートの現在行です。</param>
    /// <param name="root">退避先を保持するルートです。</param>
    /// <param name="snapshot">削除より先に保存した原状態です。</param>
    /// <param name="commit">稼働系の条件付き更新経路です。</param>
    /// <param name="cancellationToken">権限喪失の通知です。</param>
    /// <returns>詳細の削除とルート墓標の完了を表します。</returns>
    private async Task FinishArchiveAsync(TableEntity rootRow, Occurrence root, HistoryArchive snapshot, Func<IEnumerable<TableTransactionAction>, CancellationToken, Task> commit, CancellationToken cancellationToken)
    {
        var pending = new List<TableTransactionAction>();
        foreach (var saved in snapshot.Rows.Where(r => r.Key != rootRow.RowKey))
        {
            var row = await storage.GetAsync(saved.Key, cancellationToken);
            if (row is null)
            {
                continue;
            }

            if (row.ETag.ToString() != saved.ETag)
            {
                throw new InvalidDataException("退避後に行が変更されたため削除を中断しました。");
            }

            pending.Add(new TableTransactionAction(TableTransactionActionType.Delete, row, row.ETag));
            if (pending.Count == 99)
            {
                await commit(pending, cancellationToken);
                pending.Clear();
            }
        }

        if (pending.Count > 0)
        {
            await commit(pending, cancellationToken);
        }

        root.State = "Archived";
        await commit([Replace(rootRow, root)], cancellationToken);
    }

    /// <summary>既知の終了試行のログだけを、履歴退避とは独立した期限で削除します。</summary>
    /// <param name="attempts">通常Tableまたは履歴Blobから復元した試行です。</param>
    /// <param name="ensureAuthority">削除直前の稼働系検査です。</param>
    /// <param name="cancellationToken">権限喪失の通知です。</param>
    /// <returns>期限切れログの削除完了を表します。</returns>
    private async Task ExpireLogsAsync(IEnumerable<JobAttempt> attempts, Action ensureAuthority, CancellationToken cancellationToken)
    {
        if (options.LogRetentionDays is not int days)
        {
            return;
        }

        foreach (var attempt in attempts.Where(a => IsTerminal(a.State) && OldEnough(a.FinishedAt, days)))
        {
            await foreach (var blob in storage.Container.GetBlobsAsync(prefix: $"logs/{attempt.OccurrenceId}/{attempt.Id}/", cancellationToken: cancellationToken))
            {
                ensureAuthority();
                await storage.Container.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: cancellationToken);
            }
        }
    }

    /// <summary>取得したETagを維持して置換操作を作り、競合を無視しません。</summary>
    /// <typeparam name="T">新しい保存モデルです。</typeparam>
    /// <param name="row">取得時の行です。</param>
    /// <param name="value">新しい内容です。</param>
    /// <returns>ワイルドカードを使わないTable操作です。</returns>
    private TableTransactionAction Replace<T>(TableEntity row, T value) => new(TableTransactionActionType.UpdateReplace, storage.Entity(row.RowKey, value), row.ETag);
}
