using System.Globalization;
using System.Text;
using System.Text.Json;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Storage;

namespace JobRunner.Controllers.Jp1;

/// <summary>一覧で不足していた運用引数と製品・実行詳細の参照です。</summary>
public static partial class Jp1Endpoints
{
    /// <summary>保存した分数を48時間表現へ戻します。</summary>
    /// <param name="minutes">時刻または分数です。</param>
    /// <param name="mode">絶対時刻か相対分かを区別する方式です。</param>
    /// <returns>未指定は空文字です。</returns>
    private static string MonitorTime(int? minutes, string? mode) => minutes is not int value || mode is null or "NOT" ? string.Empty
        : mode == "ABSOLUTE" ? $"{value / 60:D2}:{value % 60:D2}" : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>nullと省略を変更なしとして扱います。</summary>
    /// <param name="values">引数です。</param>
    /// <param name="name">項目名です。</param>
    /// <returns>変更する値が存在する場合trueです。</returns>
    private static bool Present(JsonElement values, string name) => values.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;

    /// <summary>文字列で指定された符号付き終了コードを検証します。</summary>
    /// <param name="values">引数です。</param>
    /// <returns>32ビットの終了コードです。</returns>
    private static int ReturnCode(JsonElement values)
    {
        string text = RequiredString(values, "newReturnCode", 11);
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int code))
        {
            throw new Jp1ParameterException("newReturnCode", 3206, "終了コードは符号付き32ビット整数の文字列です。");
        }

        return code;
    }

    /// <summary>監視方式を変更する項目だけ検証し、無視する時刻を要求ハッシュへ含めません。</summary>
    /// <param name="values">HTTP引数です。</param>
    /// <returns>省略と無効を区別した監視変更です。</returns>
    private static DelayMonitorSettings ReadDelayMonitor(JsonElement values)
    {
        string? start = Present(values, "delayedStart") ? Choice(values, "delayedStart", null, "NOT", "ABSOLUTE", "ROOT", "TOP", "OWN") : null;
        string? end = Present(values, "delayedEnd") ? Choice(values, "delayedEnd", null, "NOT", "ABSOLUTE", "ROOT", "TOP", "OWN") : null;
        string? duration = Present(values, "monitoringJobnet") ? Choice(values, "monitoringJobnet", null, "NOT", "TIME_REQUIRED_FOR_EXECUTION") : null;
        if (start is null && end is null && duration is null)
        {
            throw new Jp1ParameterException("delayedStart", 3201, "監視の変更項目が必要です。");
        }

        return new DelayMonitorSettings
        {
            Start = start,
            StartMinutes = MonitorMinutes(values, "delayedStartTime", start),
            End = end,
            EndMinutes = MonitorMinutes(values, "delayedEndTime", end),
            Duration = duration is null ? null : duration != "NOT",
            DurationMinutes = MonitorMinutes(values, "timeRequiredForExecution", duration),
        };
    }

    /// <summary>絶対時刻だけ00:00を許可します。</summary>
    /// <param name="values">引数です。</param>
    /// <param name="name">時刻項目です。</param>
    /// <param name="mode">監視方式です。</param>
    /// <returns>変更しない場合はnullです。</returns>
    private static int? MonitorMinutes(JsonElement values, string name, string? mode)
    {
        if (mode is null or "NOT")
        {
            return null;
        }

        string text = RequiredString(values, name, 5);
        try
        {
            int minutes = ScheduleCalculator.ParseTime(text);
            if (mode != "ABSOLUTE" && minutes == 0)
            {
                throw new FormatException();
            }

            return minutes;
        }
        catch (FormatException)
        {
            throw new Jp1ParameterException(name, 3206, "監視時刻の形式または範囲が不正です。");
        }
    }

    /// <summary>JP1製品を偽装せず、ローカル製品のバージョンを返します。</summary>
    /// <param name="context">要求です。</param>
    /// <param name="options">ローカル接続先です。</param>
    /// <returns>製品情報または未対応プロトコルの明示です。</returns>
    private static IResult ReadSystemAsync(HttpContext context, RunnerOptions options)
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        try
        {
            if (context.Request.Query.Any(p => p.Key != "manager" || p.Value.Count != 1))
            {
                return Error(context, 400, "未対応または重複する検索引数です。", code: "JOBRUNNER_UNSUPPORTED_PARAMETER");
            }

            var query = JsonSerializer.SerializeToElement(context.Request.Query.ToDictionary(p => p.Key, p => p.Value.ToString()));
            if (RequiredString(query, "manager", 255) != options.Jp1ManagerName)
            {
                return Error(context, 404, "指定されたマネージャーはありません。");
            }

            if (context.Request.Path.Value!.EndsWith("/protocolVersion", StringComparison.Ordinal))
            {
                return Error(context, 501, "JP1 Managerには接続していません。対応範囲は/api/jobrunner/compatibilityで確認してください。", code: "JOBRUNNER_NO_JP1_PROTOCOL");
            }

            var version = typeof(Jp1Endpoints).Assembly.GetName().Version!;
            string number = $"{version.Major:D2}-{version.Minor:D2}-{Math.Max(0, version.Build):D2}";
            return Results.Json(new { productName = "JobRunner", productVersion = number, displayProductVersion = number, productVersionNumber = (version.Major * 10000) + (version.Minor * 100) + Math.Max(0, version.Build) });
        }
        catch (Jp1ParameterException ex)
        {
            return Error(context, 400, ex.Message, ex.Parameter, ex.MessageId);
        }
    }

    /// <summary>最新試行の両ログを上限付きで読み、過去試行を混ぜません。</summary>
    /// <param name="context">要求です。</param>
    /// <param name="value">対象世代です。</param>
    /// <param name="attempt">最新の保存済み試行です。</param>
    /// <param name="storage">ログ保存先です。</param>
    /// <returns>5MiB以内の詳細と完全性を返します。</returns>
    private static async Task<IResult> ExecutionDetailsAsync(HttpContext context, Occurrence value, JobAttempt? attempt, StorageRepository storage)
    {
        if (value.State is "Waiting" or "Held" or "Starting" or "Running" or "Unknown")
        {
            return Error(context, 409, "実行結果が確定していません。", code: "JOBRUNNER_STATE_CONFLICT");
        }

        using var data = new MemoryStream();
        const int maximum = 5 * 1024 * 1024;
        bool complete = attempt?.LogsComplete ?? true;
        if (attempt is not null)
        {
            foreach (string name in new[] { "stdout", "stderr" })
            {
                bool header = false;
                await foreach (var blob in storage.Container.GetBlobsAsync(prefix: $"logs/{value.Id}/{attempt.Id}/{name}/", cancellationToken: context.RequestAborted))
                {
                    if (!header)
                    {
                        byte[] label = Encoding.UTF8.GetBytes($"[{name}]\n");
                        data.Write(label, 0, (int)Math.Min(label.Length, maximum + 1 - data.Length));
                        header = true;
                    }

                    var download = await storage.Container.GetBlobClient(blob.Name).DownloadStreamingAsync(cancellationToken: context.RequestAborted);
                    using var stream = download.Value.Content;
                    byte[] buffer = new byte[8192];
                    int count;
                    while (data.Length <= maximum && (count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximum + 1 - data.Length)), context.RequestAborted)) > 0)
                    {
                        data.Write(buffer, 0, count);
                    }

                    if (data.Length > maximum)
                    {
                        return Results.Json(new { execResultDetails = Encoding.UTF8.GetString(data.GetBuffer(), 0, maximum), all = false });
                    }
                }
            }
        }

        return Results.Json(new { execResultDetails = Encoding.UTF8.GetString(data.ToArray()), all = complete });
    }
}
