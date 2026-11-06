using System.Globalization;
using Azure;
using Azure.Storage.Blobs.Models;
using JobRunner.Models;
using JobRunner.Storage;

namespace JobRunner.Controllers.Ui;

/// <summary>試行履歴と範囲を制限したログを読み取ります。</summary>
public static partial class UiEndpoints
{
    private const int LogPageSize = 16;
    private const int MaxChunkBytes = 32768;

    private static async Task<AttemptPage> ReadAttemptsAsync(string rootId, string occurrenceId, StorageRepository storage, CancellationToken token)
    {
        var target = await LogTargetAsync(rootId, occurrenceId, storage, token);

        // 再実行待ちではAttemptIdが消えていても、過去の試行は一覧に残します。
        var attempts = target.Attempts ?? (await storage.ListAsync("attempt:", token)).Select(StorageRepository.Read<JobAttempt>).ToList();
        return new AttemptPage(target.Occurrence.UnitPath, target.Occurrence.AttemptId, target.Attempts is not null, attempts.Where(a => a.OccurrenceId == occurrenceId).OrderByDescending(a => a.StartedAt).ThenByDescending(a => a.Id, StringComparer.Ordinal).ToList());
    }

    private static async Task<LogPage> ReadLogsAsync(string rootId, string occurrenceId, string attemptId, string stream, string? cursor, StorageRepository storage, CancellationToken token)
    {
        if (stream is not "stdout" and not "stderr")
        {
            throw new ArgumentException("ストリームはstdoutまたはstderrを指定してください。");
        }

        string direction = "before";
        int boundary = int.MaxValue;
        if (cursor is not null)
        {
            var parts = cursor.Split(':');
            if (parts.Length != 2 || parts[0] is not "before" and not "after" || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out boundary))
            {
                throw new ArgumentException("ログのカーソルが不正です。");
            }

            direction = parts[0];
        }

        var target = await LogTargetAsync(rootId, occurrenceId, storage, token);
        JobAttempt? attempt;
        if (target.Attempts is not null)
        {
            attempt = target.Attempts.SingleOrDefault(a => a.Id == attemptId);
        }
        else
        {
            var row = await storage.GetAsync("attempt:" + attemptId, token);
            attempt = row is null ? null : StorageRepository.Read<JobAttempt>(row);
        }

        if (attempt is null || attempt.OccurrenceId != occurrenceId)
        {
            throw new ArgumentException("対象実行回に所属する試行を指定してください。");
        }

        ValidateLogId(occurrenceId);
        ValidateLogId(attemptId);
        string prefix = $"logs/{occurrenceId}/{attemptId}/{stream}/";
        var selected = new SortedDictionary<int, string>();
        int? first = null;
        int? last = null;

        // 本文を全取得せず名前だけ列挙し、前方/後方それぞれ最大16個に絞ります。
        await foreach (var blob in storage.Container.GetBlobsAsync(prefix: prefix, cancellationToken: token))
        {
            string name = blob.Name[prefix.Length..];
            if (!name.EndsWith(".txt", StringComparison.Ordinal) || !int.TryParse(name[..^4], NumberStyles.None, CultureInfo.InvariantCulture, out int sequence))
            {
                continue;
            }

            first = Math.Min(first ?? sequence, sequence);
            last = Math.Max(last ?? sequence, sequence);
            if (cursor is null || (direction == "before" ? sequence < boundary : sequence > boundary))
            {
                selected[sequence] = blob.Name;
                if (selected.Count > LogPageSize)
                {
                    selected.Remove(direction == "before" ? selected.Keys.First() : selected.Keys.Last());
                }
            }
        }

        var chunks = new List<LogChunk>();
        foreach (var (sequence, name) in selected)
        {
            try
            {
                // 外部から過大なBlobが置かれても応答の本文上限を超えません。
                var download = await storage.Container.GetBlobClient(name).DownloadContentAsync(new BlobDownloadOptions { Range = new HttpRange(0, MaxChunkBytes + 1) }, token);
                if (download.Value.Content.ToMemory().Length > MaxChunkBytes)
                {
                    throw new InvalidDataException("保存されたログ片が読み取り上限を超えています。");
                }

                chunks.Add(new LogChunk(sequence, download.Value.Content.ToString()));
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // 一覧取得と本文取得の間に保持期限で削除されたログを無いものとして扱います。
            }
        }

        int end = selected.Count > 0 ? selected.Keys.Last() : cursor is null ? 0 : boundary;
        string? previous = selected.Count > 0 && first < selected.Keys.First() ? "before:" + selected.Keys.First().ToString(CultureInfo.InvariantCulture) : null;

        // 空のストリームは再度末尾を読むため、クライアント側でカーソルを進めません。
        return new LogPage(chunks, previous, "after:" + end.ToString(CultureInfo.InvariantCulture), last > end, attempt.LogsComplete, attempt.State);
    }

    private static async Task<(Occurrence Occurrence, List<JobAttempt>? Attempts)> LogTargetAsync(string rootId, string occurrenceId, StorageRepository storage, CancellationToken token)
    {
        var active = await ActiveAsync(storage, token);
        var rootRow = await storage.GetAsync("occurrence:" + rootId, token) ?? throw new KeyNotFoundException("実行回がありません。");
        var root = StorageRepository.Read<Occurrence>(rootRow);
        if (root.Id != root.RootId || root.Namespace != active.Id)
        {
            throw new ArgumentException("現在の採用領域のルート実行回を指定してください。");
        }

        Occurrence? occurrence;
        List<JobAttempt>? attempts = null;
        if (root.ArchivePath is not null)
        {
            var archive = await ReadArchiveAsync(root, storage, token);
            occurrence = archive.Occurrences.SingleOrDefault(o => o.Id == occurrenceId);
            attempts = archive.Attempts;
        }
        else
        {
            var row = await storage.GetAsync("occurrence:" + occurrenceId, token);
            occurrence = row is null ? null : StorageRepository.Read<Occurrence>(row);
        }

        if (occurrence is null || occurrence.RootId != rootId || occurrence.Namespace != active.Id)
        {
            throw new ArgumentException("対象ルートに所属する実行回を指定してください。");
        }

        return (occurrence, attempts);
    }

    private static void ValidateLogId(string id)
    {
        if (id.Length == 0 || id is "." or ".." || id.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new ArgumentException("ログ識別子が不正です。");
        }
    }
}
