using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Microsoft.AspNetCore.Http.Features;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Controllers.Jp1;

/// <summary>確認済みのV12 HTTP契約を、稼働系の共通操作キューへ変換します。</summary>
public static class Jp1Endpoints
{
    /// <summary>即時登録と保留変更を公開します。未対応操作を成功扱いしません。</summary>
    /// <param name="app">既存のAPIホストです。</param>
    public static void Map(WebApplication app)
    {
        app.MapPost("/ajs/api/v1/objects/{**tail}", HandleAsync);
    }

    /// <summary>公式のエラー形状を作り、独自のエラーIDはJobRunner由来と明示します。</summary>
    /// <param name="context">要求URIと応答ヘッダーです。</param>
    /// <param name="status">公式のHTTPステータス分類です。</param>
    /// <param name="message">失敗理由です。</param>
    /// <param name="parameter">必須引数不足の項目。その他のエラーはnullです。</param>
    /// <returns>applicationをajsとしたJSONエラーです。</returns>
    private static IResult Error(HttpContext context, int status, string message, string? parameter = null)
    {
        var body = new
        {
            errorSource = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}",
            message,
            messageID = parameter is null ? "JOBRUNNER-" + status : "KNAK3201-E",
            application = "ajs",
            extensions = parameter is null
            ? new[] { new { key = "implementation", value = "JobRunner" } }
            : new[] { new { key = "error_type", value = "PARAMETER" }, new { key = "error_code", value = "ERROR_PARAM_EMPTY" }, new { key = "error_parameter", value = parameter } },
        };
        return Results.Json(body, statusCode: status);
    }

    /// <summary>符号化パスを一度だけ復号し、必須引数と実装範囲を検証します。</summary>
    /// <param name="context">HTTP要求です。</param>
    /// <param name="storage">受付と結果照会の共有Tableです。</param>
    /// <param name="options">ローカルマネージャー名とサービス名です。</param>
    /// <param name="definitions">対象ルート定義を確認するサービスです。</param>
    /// <param name="clock">登録時刻と待機期限に使用する時計です。</param>
    /// <returns>稼働系の適用を確認した200、または公式形状のエラーです。</returns>
    private static async Task<IResult> HandleAsync(HttpContext context, StorageRepository storage, RunnerOptions options, DefinitionService definitions, TimeProvider clock)
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "Thu, 01 Jan 1970 00:00:00 GMT";
        try
        {
            string raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path.Value!;
            string path = Uri.UnescapeDataString(raw.Split('?')[0]);
            const string prefix = "/ajs/api/v1/objects/";
            const string registerSuffix = "/actions/registerImmediateExec/invoke";
            const string fixedSuffix = "/actions/registerFixedExec/invoke";
            const string holdSuffix = "/actions/changeHold/invoke";
            int actionIndex = path.LastIndexOf("/actions/", StringComparison.Ordinal);
            string action = actionIndex >= 0 && path.EndsWith("/invoke", StringComparison.Ordinal) ? path[(actionIndex + 9)..^7] : string.Empty;
            bool register = path.StartsWith(prefix + "definitions/", StringComparison.Ordinal) && path.EndsWith(registerSuffix, StringComparison.Ordinal);
            bool fixedRegistration = path.StartsWith(prefix + "definitions/", StringComparison.Ordinal) && path.EndsWith(fixedSuffix, StringComparison.Ordinal);
            bool unregister = path.StartsWith(prefix + "definitions/", StringComparison.Ordinal) && action == "cancelRegistration";
            bool hold = path.StartsWith(prefix + "statuses/", StringComparison.Ordinal) && path.EndsWith(holdSuffix, StringComparison.Ordinal);
            bool statusOperation = path.StartsWith(prefix + "statuses/", StringComparison.Ordinal) && action is "cancelExecSchedule" or "restoreChangedSchedule" or "kill" or "rerun" or "changeStartTime" or "execImmediate";
            if (!register && !fixedRegistration && !hold && !unregister && !statusOperation)
            {
                return Error(context, 404, "このAPIは未対応です。");
            }

            string resource = path[(prefix.Length + (register || fixedRegistration || unregister ? "definitions/".Length : "statuses/".Length))..actionIndex];
            var body = await context.Request.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
            {
                return Error(context, 400, "parametersオブジェクトを指定してください。", "parameters");
            }

            foreach (string required in hold ? new[] { "manager", "serviceName", "holdAttr" } : new[] { "manager", "serviceName" })
            {
                if (!parameters.TryGetProperty(required, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return Error(context, 400, required + "を指定してください。", required);
                }
            }

            string manager = parameters.GetProperty("manager").GetString()!;
            string service = parameters.GetProperty("serviceName").GetString()!;
            if (Encoding.UTF8.GetByteCount(manager) > 255 || Encoding.UTF8.GetByteCount(service) > 30)
            {
                return Error(context, 400, "マネージャー名またはサービス名が上限を超えています。");
            }

            if (manager != options.Jp1ManagerName || service != options.SchedulerId)
            {
                return Error(context, 404, "ローカルのマネージャー名またはサービス名が一致しません。");
            }

            var allowed = fixedRegistration ? new[] { "manager", "serviceName", "futureGeneration", "passedDaemonStarts", "holding", "macro", "fixedScheduleFrom", "fixedScheduleTo", "startTime" } : register ? new[] { "manager", "serviceName", "holding", "macro", "startCondition" } : new[] { "manager", "serviceName", "holdAttr" };
            if (unregister || statusOperation)
            {
                allowed = action switch
                {
                    "rerun" => ["manager", "serviceName", "rerunMethod", "rerunOptions"],
                    "changeStartTime" => ["manager", "serviceName", "kindTime", "absoluteTime", "changeLower", "pushedAhead"],
                    "execImmediate" => ["manager", "serviceName", "changeLower", "pushedAhead"],
                    _ => ["manager", "serviceName"],
                };
            }

            if (parameters.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal))
                || (parameters.TryGetProperty("startCondition", out var condition) && condition.ValueKind != JsonValueKind.Null)
                || (parameters.TryGetProperty("macro", out var macro) && macro.ValueKind != JsonValueKind.Null && (macro.ValueKind != JsonValueKind.Array || macro.GetArrayLength() != 0)))
            {
                return Error(context, 400, "未対応の引数、起動条件またはマクロが指定されています。");
            }

            int? futureGenerations = null;
            DateOnly? fixedFrom = null;
            DateOnly? fixedUntil = null;
            DateTimeOffset? fixedDue = null;
            string? passed = null;
            if (fixedRegistration)
            {
                fixedFrom = ReadDate(parameters, "fixedScheduleFrom");
                var fixedTo = ReadDate(parameters, "fixedScheduleTo");
                if (fixedFrom.HasValue != fixedTo.HasValue || fixedFrom > fixedTo)
                {
                    return Error(context, 400, "確定期間の開始日と終了日を順序どおりに指定してください。");
                }

                fixedUntil = fixedTo?.AddDays(1);
                if (parameters.TryGetProperty("futureGeneration", out var generation) && generation.ValueKind != JsonValueKind.Null)
                {
                    if (generation.ValueKind != JsonValueKind.Number || !generation.TryGetInt32(out int count) || count is < 0 or > 99)
                    {
                        return Error(context, 400, "futureGenerationは0〜99です。");
                    }

                    futureGenerations = count == 0 ? null : count;
                }

                if (parameters.TryGetProperty("startTime", out var start) && start.ValueKind != JsonValueKind.Null)
                {
                    fixedDue = ReadLocalTime(start.GetString()!, options);
                }

                if ((fixedDue is not null && (fixedFrom is not null || futureGenerations is not null))
                    || (fixedDue is null && fixedFrom is null && futureGenerations is null))
                {
                    return Error(context, 400, "期間・未来世代数または単独の日時を指定してください。");
                }

                passed = parameters.TryGetProperty("passedDaemonStarts", out var policy) && policy.ValueKind != JsonValueKind.Null ? policy.GetString() : fixedDue is not null ? "IMMEDIATE" : null;
                if (passed is not ("NEXTTIME" or "IMMEDIATE"))
                {
                    return Error(context, 400, "passedDaemonStartsはNEXTTIMEまたはIMMEDIATEを指定してください。", passed is null ? "passedDaemonStarts" : null);
                }
            }

            bool holding = (register || fixedRegistration) && parameters.TryGetProperty("holding", out var holdingValue) && holdingValue.GetBoolean();
            string holdAttr = hold ? parameters.GetProperty("holdAttr").GetString()! : string.Empty;
            if (hold && holdAttr is not ("SET" or "RELEASE"))
            {
                return Error(context, 400, "holdAttrはSETまたはRELEASEで指定してください。");
            }

            DateTimeOffset? moveDue = null;
            string? rerunMethod = null;
            List<string>? rerunFlags = null;
            if (action is "changeStartTime" or "execImmediate")
            {
                if (!parameters.TryGetProperty("changeLower", out var lower) || lower.GetString() != "SHIFT"
                    || !parameters.TryGetProperty("pushedAhead", out var ahead) || ahead.GetString() != "MOVE")
                {
                    return Error(context, 400, "現在の対応範囲はchangeLower=SHIFT、pushedAhead=MOVEです。");
                }

                if (action == "changeStartTime")
                {
                    if (!parameters.TryGetProperty("kindTime", out var kind) || kind.GetString() != "ABSOLUTETIME" || !parameters.TryGetProperty("absoluteTime", out var time))
                    {
                        return Error(context, 400, "kindTime=ABSOLUTETIMEとabsoluteTimeを指定してください。");
                    }

                    moveDue = ReadLocalTime(time.GetString()!, options);
                }
            }

            if (action == "rerun")
            {
                if (!parameters.TryGetProperty("rerunMethod", out var method) || method.ValueKind != JsonValueKind.String)
                {
                    return Error(context, 400, "rerunMethodを指定してください。", "rerunMethod");
                }

                rerunMethod = method.GetString();
                if (parameters.TryGetProperty("rerunOptions", out var rerunOptions) && rerunOptions.ValueKind != JsonValueKind.Null)
                {
                    if (rerunOptions.ValueKind != JsonValueKind.Array || rerunOptions.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || v.GetString() is not ("HOLD" or "WARNING")))
                    {
                        return Error(context, 400, "再実行オプションが不正です。");
                    }

                    rerunFlags = rerunOptions.EnumerateArray().Select(v => v.GetString()!).Distinct().Order(StringComparer.Ordinal).ToList();
                }
            }

            string requestId = context.Request.Headers["X-JobRunner-Request-Id"].ToString();
            if (requestId.Length > 100)
            {
                return Error(context, 400, "X-JobRunner-Request-Idは100文字以下で指定してください。");
            }

            string id = "jp1:" + (requestId.Length == 0 ? Guid.NewGuid().ToString("N") : requestId);
            string key = "command:" + StorageRepository.Hash(id);
            string hash = fixedRegistration
                ? StorageRepository.Hash(JsonSerializer.Serialize(new { fixedRegistration, resource, manager, service, holding, futureGenerations }))
                : StorageRepository.Hash(JsonSerializer.Serialize(new { register, resource, manager, service, holding, holdAttr }));
            if (statusOperation || unregister)
            {
                hash = StorageRepository.Hash(JsonSerializer.Serialize(new { action, resource, manager, service, moveDue, rerunMethod, rerunFlags }));
            }

            if (fixedRegistration && (fixedFrom is not null || fixedDue is not null || passed != "NEXTTIME"))
            {
                hash = StorageRepository.Hash(JsonSerializer.Serialize(new { BaseHash = hash, fixedFrom, fixedUntil, fixedDue, passed }));
            }

            context.Response.Headers["X-JobRunner-Operation-Id"] = id;
            var existing = await storage.GetAsync(key, context.RequestAborted);
            if (existing is not null)
            {
                if (StorageRepository.Read<Operation>(existing).RequestHash != hash)
                {
                    return Error(context, 409, "同じ要求IDに異なる内容が指定されています。");
                }

                return await AwaitResultAsync(context, key, register || fixedDue is not null, storage, clock);
            }

            var activeRow = await storage.GetAsync("definition-active", context.RequestAborted);
            if (activeRow is null || StorageRepository.Read<Activation>(activeRow).Phase != "Ready")
            {
                return Error(context, 412, "スケジューラーの初期化が完了していません。");
            }

            var active = StorageRepository.Read<Activation>(activeRow);
            string target;
            if (register || fixedRegistration || unregister)
            {
                var bundle = await definitions.ReadAsync(active.DefinitionHash, context.RequestAborted);
                var units = bundle.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
                if (!units.TryGetValue(resource, out var unit) || unit.Type != "n" || (units.TryGetValue(Scheduling.ScheduleCalculator.Parent(resource), out var parent) && parent.Type != "g"))
                {
                    return Error(context, 404, "指定されたルートジョブネットがありません。");
                }

                target = resource;
            }
            else
            {
                int colon = resource.LastIndexOf(':');
                if (colon < 1)
                {
                    return Error(context, 404, "ユニット完全名と実行IDを指定してください。");
                }

                var occurrences = (await storage.ListAsync("occurrence:", context.RequestAborted)).Select(StorageRepository.Read<Occurrence>).ToList();
                var root = occurrences.SingleOrDefault(o => o.Id == o.RootId && o.Jp1ExecutionId == resource[(colon + 1)..]);
                var occurrence = root is null ? null : occurrences.SingleOrDefault(o => o.RootId == root.Id && o.UnitPath == resource[..colon]);
                if (occurrence is null)
                {
                    return Error(context, 404, "指定された実行世代がありません。");
                }

                target = occurrence.Id;
            }

            var operation = new Operation
            {
                Id = id,
                RequestHash = hash,
                StateNamespace = active.Id,
                Kind = register || fixedRegistration ? "Register" : unregister ? "UnregisterRoot" : hold ? holdAttr == "SET" ? "Hold" : "Release" : action switch { "cancelExecSchedule" => "Cancel", "restoreChangedSchedule" => "Restore", "kill" => "Stop", "rerun" => "Rerun", "changeStartTime" => "Move", _ => "RunNow" },
                Target = target,
                RegistrationKind = fixedRegistration ? "Confirmed" : "Immediate",
                FutureGenerations = futureGenerations,
                Start = fixedFrom,
                End = fixedUntil,
                FixedDue = fixedDue,
                PassedDaemonStarts = passed,
                Due = register ? clock.GetUtcNow() : moveDue,
                RerunMethod = rerunMethod,
                RerunOptions = rerunFlags,
                Holding = register || fixedRegistration ? holding : null,
                CreatedAt = clock.GetUtcNow(),
            };
            try
            {
                await storage.Table.AddEntityAsync(storage.Entity(key, operation), context.RequestAborted);
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                var current = await storage.GetAsync(key, context.RequestAborted);
                if (current is null || StorageRepository.Read<Operation>(current).RequestHash != hash)
                {
                    return Error(context, 409, "並行した同一IDの要求内容が一致しません。");
                }
            }

            return await AwaitResultAsync(context, key, register || fixedDue is not null, storage, clock);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException or FormatException or ArgumentException)
        {
            return Error(context, 400, "JSONまたは引数の型が不正です。");
        }
        catch (InvalidDataException)
        {
            return Error(context, 500, "管理状態が欠損または破損しています。操作IDを使って確認してください。");
        }
        catch (RequestFailedException)
        {
            return Error(context, 412, "Storageへ到達できません。操作IDで結果を再確認してください。");
        }
    }

    /// <summary>公式の年月日表記と年の範囲を検証します。</summary>
    /// <param name="parameters">要求パラメーターです。</param>
    /// <param name="name">対象のキーです。</param>
    /// <returns>指定がない場合nullです。</returns>
    private static DateOnly? ReadDate(JsonElement parameters, string name)
    {
        if (!parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (!DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Year is < 1994 or > 2036)
        {
            throw new FormatException("年月日は1994〜2036年のYYYY-MM-DDです。");
        }

        return date;
    }

    /// <summary>47時までのローカル予定をタイムゾーンでUTCに変換します。</summary>
    /// <param name="value">YYYY-MM-DDThh:mm表記です。</param>
    /// <param name="options">サービスの時間帯です。</param>
    /// <returns>確定した実日時です。</returns>
    private static DateTimeOffset ReadLocalTime(string value, RunnerOptions options)
    {
        if (value.Length != 16 || value[10] != 'T' || !DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Year is < 1994 or > 2036)
        {
            throw new FormatException("日時の書式が不正です。");
        }

        int minutes = Scheduling.ScheduleCalculator.ParseTime(value[11..]);
        var local = date.ToDateTime(TimeOnly.MinValue).AddMinutes(minutes);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
        {
            throw new FormatException("夏時間の重複・欠落時刻です。");
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }

    /// <summary>保存された要求を稼働系が適用するまで待ち、独自202を返さないようにします。</summary>
    /// <param name="context">応答と要求中断の情報です。</param>
    /// <param name="key">受付済み操作の行キーです。</param>
    /// <param name="register">即時登録でexecIDを返す場合trueです。</param>
    /// <param name="storage">結果の保存先です。</param>
    /// <param name="clock">待機経過を単調時計で確認します。</param>
    /// <returns>完了時の200または拒否・待機期限のエラーです。</returns>
    private static async Task<IResult> AwaitResultAsync(HttpContext context, string key, bool register, StorageRepository storage, TimeProvider clock)
    {
        long started = clock.GetTimestamp();
        while (clock.GetElapsedTime(started) < TimeSpan.FromSeconds(30))
        {
            var row = await storage.GetAsync(key, context.RequestAborted) ?? throw new InvalidDataException("受付済み要求が欠損しています。");
            var operation = await HistoryRetentionService.ReadOperationAsync(row, storage, context.RequestAborted);
            if (operation.Status == "Rejected")
            {
                return Error(context, 409, operation.Result ?? "操作が拒否されました。");
            }

            if (operation.Status == "Completed")
            {
                if (!register)
                {
                    return Results.Text(string.Empty, "text/plain", statusCode: 200);
                }

                var root = (await storage.ListAsync("occurrence:", context.RequestAborted)).Select(StorageRepository.Read<Occurrence>).SingleOrDefault(o => o.Id == o.RootId && o.Namespace == operation.StateNamespace && o.RegistrationId == operation.Result);
                if (root?.Jp1ExecutionId is null)
                {
                    return Error(context, 500, "登録結果の実行IDが見つかりません。操作IDで確認してください。");
                }

                return Results.Json(new { execID = root.Jp1ExecutionId });
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), clock, context.RequestAborted);
        }

        return Error(context, 500, "処理の完了確認がタイムアウトしました。操作は取り消していません。操作IDで再照会してください。");
    }
}
