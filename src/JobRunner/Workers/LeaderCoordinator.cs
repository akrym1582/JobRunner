using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs.Specialized;
using JobRunner.Storage;

namespace JobRunner.Workers;

/// <summary>有限Blobリースと単調時計を使い、新規起動の権限を管理します。</summary>
/// <param name="storage">同一パーティションのleader行とロックBlobへ接続します。</param>
/// <param name="clock">UTCの変更に影響されないリース期限の単調時計です。</param>
public sealed class LeaderCoordinator(StorageRepository storage, TimeProvider clock) : IAsyncDisposable
{
    /// <summary>稼働系権限の喪失を更新処理と実行器へ同時に通知します。</summary>
    private readonly CancellationTokenSource lost = new();

    /// <summary>現在取得した有限リースのクライアント。定義の版とは独立です。</summary>
    private BlobLeaseClient? lease;

    /// <summary>ジョブ実行に妨げられない独立したリース更新タスクです。</summary>
    private Task? renewal;

    /// <summary>最後に成功した要求の開始時点を単調時計の値で保持します。</summary>
    private long renewedAt;

    /// <summary>更新成功を確認できているかをスレッド間で共有する印です。</summary>
    private bool valid;

    /// <summary>Gets. プロセスごとに異なる所有者IDです。PIDの再利用には依存しません。</summary>
    public string Owner { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets. Tableで確定した稼働系の世代番号です。</summary>
    public long Epoch { get; private set; }

    /// <summary>Gets. 更新失敗時に実行器と状態更新を中断する通知です。</summary>
    public CancellationToken Lost => lost.Token;

    /// <summary>Gets a value indicating whether. 確認済みのリースが安全余裕内にある場合だけtrueです。</summary>
    public bool IsLeader => Volatile.Read(ref valid) && clock.GetElapsedTime(Interlocked.Read(ref renewedAt)) < TimeSpan.FromSeconds(40) && !lost.IsCancellationRequested;

    /// <summary>リースを取得した後、Tableの所有者とEpochを条件付きで確定します。</summary>
    /// <param name="cancellationToken">サービス停止の通知です。</param>
    /// <returns>他インスタンスが保持している場合falseです。</returns>
    public async Task<bool> AcquireAsync(CancellationToken cancellationToken)
    {
        string path = $"locks/{storage.Partition}/leader";
        await storage.PutImmutableAsync(path, BinaryData.FromString("JobRunner lease v1"), cancellationToken);
        lease = storage.Container.GetBlobClient(path).GetBlobLeaseClient();
        long started = clock.GetTimestamp();
        try
        {
            await lease.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return false;
        }

        Interlocked.Exchange(ref renewedAt, started);
        var row = await storage.GetAsync("leader", cancellationToken);
        Epoch = (row?.GetInt64("Epoch") ?? 0) + 1;
        var next = new TableEntity(storage.Partition, "leader") { ["Owner"] = Owner, ["Epoch"] = Epoch };
        if (row is null)
        {
            await storage.Table.AddEntityAsync(next, cancellationToken);
        }
        else
        {
            await storage.Table.UpdateEntityAsync(next, row.ETag, TableUpdateMode.Replace, cancellationToken);
        }

        Volatile.Write(ref valid, true);
        Ensure();
        renewal = RenewAsync(cancellationToken);
        return true;
    }

    /// <summary>権限が不明な場合は例外にして、OS起動直前の検査に使います。</summary>
    public void Ensure()
    {
        if (!IsLeader)
        {
            Volatile.Write(ref valid, false);
            lost.Cancel();
            throw new InvalidOperationException("リースの安全期限を過ぎたか、稼働系権限を失いました。");
        }
    }

    /// <summary>更新処理を停止し、保持しているリースの解放を試みます。</summary>
    /// <returns>更新タスクと解放処理の終了を表します。</returns>
    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref valid, false);
        await lost.CancelAsync();
        if (renewal is not null)
        {
            await renewal;
        }

        if (lease is not null && Epoch > 0)
        {
            try
            {
                await lease.ReleaseAsync();
            }
            catch (RequestFailedException)
            {
                // 解放不能でもリースには期限があります。解放成功を仮定して後続処理を開始しません。
            }
        }

        lost.Dispose();
    }

    /// <summary>予定計算やジョブ実行とは別の待機経路でリースを更新します。</summary>
    /// <param name="stopping">アプリの終了通知です。</param>
    /// <returns>終了または最初の更新失敗までのタスクです。</returns>
    private async Task RenewAsync(CancellationToken stopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, lost.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), clock, linked.Token);
                Ensure();
                long started = clock.GetTimestamp();
                await lease!.RenewAsync(cancellationToken: linked.Token);

                // 長時間停止から戻った応答を新しい権限と誤認しないよう、古い期限も検査します。
                Ensure();
                Interlocked.Exchange(ref renewedAt, started);
            }
        }
        catch (Exception ex) when (ex is RequestFailedException or OperationCanceledException or InvalidOperationException)
        {
            Volatile.Write(ref valid, false);
            await lost.CancelAsync();
        }
    }
}
