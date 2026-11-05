using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs.Models;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Storage;

namespace JobRunner.Services;

/// <summary>入力一式の取得と検証を終えてから不変スナップショットを保存します。</summary>
/// <param name="storage">入力と管理コンテナーへ接続するアダプターです。</param>
/// <param name="options">配置一覧と実行先の明示的な対応付けです。</param>
public sealed class DefinitionService(StorageRepository storage, RunnerOptions options)
{
    /// <summary>実効設定を検証し、誤った保存先への上書きを防止します。</summary>
    public void ValidateOptions()
    {
        if (options.ScheduleTimeMode is not (24 or 48) || options.ConfirmedHorizonDays is < 1 or > 366 || options.MaxConcurrency is < 1 or > 128 || string.IsNullOrWhiteSpace(options.SchedulerId))
        {
            throw new FormatException("時間制、先読み日数、同時実行数、SchedulerIdを確認してください。");
        }

        TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
        if (options.DefinitionSources.Count == 0)
        {
            throw new FormatException("DefinitionSourcesは1件以上必要です。");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in options.DefinitionSources)
        {
            source.TargetPath = Jp1DefinitionParser.NormalizePath(source.TargetPath);
            string container = source.BlobPath.Split('/')[0];
            if (!source.BlobPath.Contains('/') || source.BlobPath.EndsWith('/') || container == options.ManagementContainer || source.Kind != "Unit")
            {
                throw new FormatException("入力Blobを管理コンテナーと分離してください。別形式Schedule/Calendarは未対応です。");
            }

            if (!seen.Add(JsonSerializer.Serialize(new { source.Kind, source.TargetPath, source.BlobPath })))
            {
                throw new FormatException("同一の入力マッピングが重複しています。");
            }
        }
    }

    /// <summary>全BlobのETagを取得後に再確認し、読み取り中の変更を検出します。</summary>
    /// <param name="cancellationToken">起動停止などの通知です。</param>
    /// <returns>全ファイルの解析と参照検証が済んだ定義版です。</returns>
    public async Task<DefinitionBundle> LoadSourcesAsync(CancellationToken cancellationToken)
    {
        ValidateOptions();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        for (int retry = 0; retry < 3; retry++)
        {
            var bundle = new DefinitionBundle();
            try
            {
                foreach (var source in options.DefinitionSources.OrderBy(s => s.Kind, StringComparer.Ordinal).ThenBy(s => s.TargetPath, StringComparer.Ordinal).ThenBy(s => s.BlobPath, StringComparer.Ordinal))
                {
                    int separator = source.BlobPath.IndexOf('/');
                    var blob = storage.Blobs.GetBlobContainerClient(source.BlobPath[..separator]).GetBlobClient(source.BlobPath[(separator + 1)..]);
                    var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
                    var content = await blob.DownloadContentAsync(new BlobDownloadOptions { Conditions = new BlobRequestConditions { IfMatch = properties.Value.ETag } }, cancellationToken);
                    byte[] bytes = content.Value.Content.ToArray();
                    var encoding = Encoding.GetEncoding(source.Encoding, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                    string text = encoding.GetString(bytes).TrimStart('\uFEFF');
                    bundle.Units.AddRange(Jp1DefinitionParser.Parse(text, source));
                    bundle.Sources.Add(new SourceEvidence { Mapping = source, ContentHash = StorageRepository.Hash(bytes), ETag = content.Value.Details.ETag.ToString(), Base64 = Convert.ToBase64String(bytes) });
                }

                foreach (var source in bundle.Sources)
                {
                    int separator = source.Mapping.BlobPath.IndexOf('/');
                    var blob = storage.Blobs.GetBlobContainerClient(source.Mapping.BlobPath[..separator]).GetBlobClient(source.Mapping.BlobPath[(separator + 1)..]);
                    await blob.GetPropertiesAsync(new BlobRequestConditions { IfMatch = new ETag(source.ETag) }, cancellationToken);
                }

                Complete(bundle);
                return bundle;
            }
            catch (RequestFailedException ex) when (ex.Status == 412 && retry < 2)
            {
                // 複数Blobを原子的に読むことはできないため、部分的な再取得ではなく一式を読み直します。
            }
        }

        throw new InvalidOperationException("入力一式の安定した読み取りに失敗しました。");
    }

    /// <summary>APIの取り込み原文を同じ検証経路で正規化します。</summary>
    /// <param name="sources">原文バイト列と配置先の一覧です。</param>
    /// <returns>検証済みの不変定義です。</returns>
    public DefinitionBundle Validate(List<SourceEvidence> sources)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        foreach (var source in sources)
        {
            source.Mapping.TargetPath = Jp1DefinitionParser.NormalizePath(source.Mapping.TargetPath);
        }

        if (sources.Select(s => JsonSerializer.Serialize(new { s.Mapping.Kind, s.Mapping.TargetPath, s.Mapping.BlobPath })).Distinct(StringComparer.Ordinal).Count() != sources.Count)
        {
            throw new FormatException("入力マッピングが重複しています。");
        }

        var bundle = new DefinitionBundle { Sources = sources.OrderBy(s => s.Mapping.Kind, StringComparer.Ordinal).ThenBy(s => s.Mapping.TargetPath, StringComparer.Ordinal).ThenBy(s => s.Mapping.BlobPath, StringComparer.Ordinal).ToList() };
        if (sources.Count == 0)
        {
            throw new FormatException("入力が空です。");
        }

        foreach (var source in bundle.Sources)
        {
            if (source.Mapping.Kind != "Unit")
            {
                throw new FormatException("別形式Schedule/Calendarは未対応です。");
            }

            source.Mapping.TargetPath = Jp1DefinitionParser.NormalizePath(source.Mapping.TargetPath);
            byte[] bytes = Convert.FromBase64String(source.Base64);
            source.ContentHash = StorageRepository.Hash(bytes);
            string text = Encoding.GetEncoding(source.Mapping.Encoding, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes).TrimStart('\uFEFF');
            bundle.Units.AddRange(Jp1DefinitionParser.Parse(text, source.Mapping));
        }

        Complete(bundle);
        return bundle;
    }

    /// <summary>有効版参照を変えずに、検証済み定義を不変Blobへ保存します。</summary>
    /// <param name="bundle">検証済みの定義一式です。</param>
    /// <param name="cancellationToken">要求中断の通知です。</param>
    /// <returns>原文とモデルの保存完了を表します。</returns>
    public async Task SaveAsync(DefinitionBundle bundle, CancellationToken cancellationToken)
    {
        // ETagは取得証跡なので、同内容の再取り込みで不変モデルが変わらないよう別Blobに保存します。
        foreach (var source in bundle.Sources)
        {
            await storage.PutImmutableAsync($"definitions/{bundle.Hash}/sources/{source.ContentHash}", new BinaryData(Convert.FromBase64String(source.Base64)), cancellationToken);
        }

        var snapshot = new DefinitionBundle { Hash = bundle.Hash, Units = bundle.Units };
        await storage.PutImmutableAsync($"definitions/{bundle.Hash}/model.json", BinaryData.FromObjectAsJson(snapshot), cancellationToken);
        await storage.PutImmutableAsync($"definitions/{bundle.Hash}/manifests/{StorageRepository.Hash(JsonSerializer.Serialize(bundle.Sources))}.json", BinaryData.FromObjectAsJson(bundle.Sources), cancellationToken);
    }

    /// <summary>登録時に固定した定義を読み、現在の入力Blob変更を混入させません。</summary>
    /// <param name="hash">保存済み定義のSHA-256です。</param>
    /// <param name="cancellationToken">読み取り中断の通知です。</param>
    /// <returns>保存形式を確認したスナップショットです。</returns>
    public async Task<DefinitionBundle> ReadAsync(string hash, CancellationToken cancellationToken)
    {
        var data = await storage.Container.GetBlobClient($"definitions/{hash}/model.json").DownloadContentAsync(cancellationToken);
        var bundle = data.Value.Content.ToObjectFromJson<DefinitionBundle>() ?? throw new InvalidDataException("定義が破損しています。");
        if (bundle.FormatVersion != 1 || bundle.Hash != hash)
        {
            throw new InvalidDataException("定義の保存形式またはハッシュ参照が一致しません。");
        }

        return bundle;
    }

    /// <summary>意味を変える設定を決定的にハッシュ化します。</summary>
    /// <returns>時間制・タイムゾーン・実行先に対応したハッシュです。</returns>
    public string SettingsHash() => StorageRepository.Hash(JsonSerializer.Serialize(new { options.ScheduleTimeMode, options.TimeZone, options.ConfirmedHorizonDays, Agents = options.LocalAgents.Order(StringComparer.Ordinal) }));

    /// <summary>親グループを補完し、全体を検証してからハッシュを確定します。</summary>
    /// <param name="bundle">すべての入力を解析した一式です。</param>
    private void Complete(DefinitionBundle bundle)
    {
        var paths = bundle.Units.Flatten().Select(u => u.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var source in bundle.Sources)
        {
            string path = source.Mapping.TargetPath;
            while (path != "/")
            {
                if (paths.Add(path))
                {
                    bundle.Units.Add(new UnitDefinition { Path = path, Name = path[(path.LastIndexOf('/') + 1)..], Type = "g", Attributes = new() { ["ty"] = ["g"] } });
                }

                path = ScheduleCalculator.Parent(path);
            }
        }

        DefinitionValidator.Validate(bundle.Units, options);
        bundle.Hash = StorageRepository.Hash(JsonSerializer.Serialize(bundle.Sources.Select(s => new { s.Mapping.Kind, s.Mapping.TargetPath, s.Mapping.BlobPath, s.Mapping.Encoding, s.ContentHash })));
    }
}
