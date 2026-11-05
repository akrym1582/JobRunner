using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using JobRunner.Models;

namespace JobRunner.Storage;

/// <summary>
/// 本番とAzuriteで共通のAzure SDKを使い、条件付き更新以外の上書きを禁止します。
/// </summary>
public sealed class StorageRepository
{
    /// <summary>BlobとTableの両方へ適用する送信要求数の計測です。</summary>
    private readonly RequestCountingPolicy requestCounter = new();

    /// <summary>Initializes a new instance of the <see cref="StorageRepository"/> class. 接続先と名前空間を固定したStorageアダプターを作ります。</summary>
    /// <param name="options">接続設定とスケジューラーの識別子です。</param>
    public StorageRepository(RunnerOptions options)
    {
        var blobOptions = new BlobClientOptions(BlobClientOptions.ServiceVersion.V2023_11_03);
        blobOptions.AddPolicy(requestCounter, HttpPipelinePosition.PerRetry);
        blobOptions.Retry.MaxRetries = 0;
        blobOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
        Blobs = new BlobServiceClient(options.ConnectionString, blobOptions);
        Container = Blobs.GetBlobContainerClient(options.ManagementContainer);
        var tableOptions = new TableClientOptions();
        tableOptions.AddPolicy(requestCounter, HttpPipelinePosition.PerRetry);
        tableOptions.Retry.MaxRetries = 0;
        tableOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
        Table = new TableClient(options.ConnectionString, options.TableName, tableOptions);
        Partition = Hash(options.SchedulerId);
    }

    /// <summary>Gets. 入力コンテナーも読み取るためのBlobサービスです。</summary>
    public BlobServiceClient Blobs { get; }

    /// <summary>Gets. 入力と分離した管理用コンテナーです。</summary>
    public BlobContainerClient Container { get; }

    /// <summary>Gets. 状態とleaderを同じトランザクションへ含める制御テーブルです。</summary>
    public TableClient Table { get; }

    /// <summary>Gets. 禁止文字を含まないSchedulerIdのハッシュです。</summary>
    public string Partition { get; }

    /// <summary>Gets. このインスタンスのBlob/Table送信数。API照会による要求も含みます。</summary>
    public long RequestCount => requestCounter.Count;

    /// <summary>バイト列を安定したSHA-256の小文字16進数へ変換します。</summary>
    /// <param name="bytes">ハッシュ対象の原文などです。</param>
    /// <returns>64文字の安全なキーです。</returns>
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>構造化済みテキストをUTF-8でハッシュ化します。</summary>
    /// <param name="text">境界を明示したJSON等です。</param>
    /// <returns>決定的なキーとして使用できるSHA-256です。</returns>
    public static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    /// <summary>保存形式の破損を空状態で置き換えずに検出します。</summary>
    /// <typeparam name="T">期待する保存モデルです。</typeparam>
    /// <param name="entity">Storageから取得した行です。</param>
    /// <returns>復元したモデルです。</returns>
    public static T Read<T>(TableEntity entity) => JsonSerializer.Deserialize<T>(entity.GetString("Json")) ?? throw new InvalidDataException("保存状態が破損しています。");

    /// <summary>JSONをTable行へ格納します。ETagは後続の読み取りから取得します。</summary>
    /// <typeparam name="T">保存するモデルの型です。</typeparam>
    /// <param name="key">禁止文字を含まない行キーです。</param>
    /// <param name="value">保存するモデルです。</param>
    /// <returns>このスケジューラーのパーティションを持つ行です。</returns>
    public TableEntity Entity<T>(string key, T value)
    {
        string json = JsonSerializer.Serialize(value);
        if (Encoding.Unicode.GetByteCount(json) > 60000)
        {
            throw new InvalidOperationException("Table行の安全な文字列サイズ上限を超えました。");
        }

        return new TableEntity(Partition, key) { ["Json"] = json };
    }

    /// <summary>コンテナーとTableを用意します。既存データは変更しません。</summary>
    /// <param name="cancellationToken">サービス停止の通知です。</param>
    /// <returns>両Storageの応答が得られたときに完了します。</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await Container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await Table.CreateIfNotExistsAsync(cancellationToken);
    }

    /// <summary>存在しない行と通信失敗を区別して取得します。</summary>
    /// <param name="key">取得する行キーです。</param>
    /// <param name="cancellationToken">処理中断の通知です。</param>
    /// <returns>存在すればETag付きの行、存在しなければnullです。</returns>
    public async Task<TableEntity?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var result = await Table.GetEntityIfExistsAsync<TableEntity>(Partition, key, cancellationToken: cancellationToken);
        return result.HasValue ? result.Value : null;
    }

    /// <summary>行キーの接頭辞で状態を列挙します。別スケジューラーを混ぜません。</summary>
    /// <param name="prefix">列挙対象の接頭辞です。</param>
    /// <param name="cancellationToken">処理中断の通知です。</param>
    /// <returns>ETagを保持した現在の行です。</returns>
    public async Task<List<TableEntity>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        string upper = prefix + "~";
        string filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition} and RowKey ge {prefix} and RowKey lt {upper}");
        var rows = new List<TableEntity>();
        await foreach (var row in Table.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken))
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>稼働系の世代とETagを含めて状態を原子的に更新します。</summary>
    /// <param name="owner">書き込み元のインスタンスです。</param>
    /// <param name="epoch">この処理が取得した稼働系世代です。</param>
    /// <param name="changes">追加またはETag付き更新。最大99操作です。</param>
    /// <param name="cancellationToken">権限喪失または停止の通知です。</param>
    /// <returns>leaderと対象行の更新が一括確定したときに完了します。</returns>
    public async Task FencedAsync(string owner, long epoch, IEnumerable<TableTransactionAction> changes, CancellationToken cancellationToken)
    {
        var leader = await GetAsync("leader", cancellationToken) ?? throw new InvalidOperationException("leader行がありません。");
        if (leader.GetString("Owner") != owner || leader.GetInt64("Epoch") != epoch)
        {
            throw new InvalidOperationException("稼働系の世代が変わったため書き込みを拒否しました。");
        }

        var actions = changes.ToList();
        if (actions.Count is < 1 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(changes), "状態変更は1〜99操作へ分割してください。");
        }

        leader["Touched"] = Guid.NewGuid().ToString("N");
        actions.Insert(0, new TableTransactionAction(TableTransactionActionType.UpdateReplace, leader, leader.ETag));
        await Table.SubmitTransactionAsync(actions, cancellationToken);
    }

    /// <summary>不変Blobを作成し、再試行では同じ内容か照合します。</summary>
    /// <param name="path">管理コンテナー内の保存パスです。</param>
    /// <param name="data">変更しないバイト列です。</param>
    /// <param name="cancellationToken">中断の通知です。</param>
    /// <returns>同じ内容が永続化された時点で完了します。</returns>
    public async Task PutImmutableAsync(string path, BinaryData data, CancellationToken cancellationToken)
    {
        var blob = Container.GetBlobClient(path);
        try
        {
            await blob.UploadAsync(data, new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            var current = await blob.DownloadContentAsync(cancellationToken);
            if (!current.Value.Content.ToMemory().Span.SequenceEqual(data.ToMemory().Span))
            {
                throw new InvalidDataException("不変Blobの内容が一致しません。", ex);
            }
        }
    }
}
