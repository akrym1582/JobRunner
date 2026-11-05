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
            const string holdSuffix = "/actions/changeHold/invoke";
            bool register = path.StartsWith(prefix + "definitions/", StringComparison.Ordinal) && path.EndsWith(registerSuffix, StringComparison.Ordinal);
            bool hold = path.StartsWith(prefix + "statuses/", StringComparison.Ordinal) && path.EndsWith(holdSuffix, StringComparison.Ordinal);
            if (!register && !hold)
            {
                return Error(context, 404, "このAPIは未対応です。");
            }

            string resource = register ? path[(prefix.Length + "definitions/".Length)..^registerSuffix.Length] : path[(prefix.Length + "statuses/".Length)..^holdSuffix.Length];
            var body = await context.Request.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
            {
                return Error(context, 400, "parametersオブジェクトを指定してください。", "parameters");
            }

            foreach (string required in register ? new[] { "manager", "serviceName" } : new[] { "manager", "serviceName", "holdAttr" })
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

            var allowed = register ? new[] { "manager", "serviceName", "holding", "macro", "startCondition" } : new[] { "manager", "serviceName", "holdAttr" };
            if (parameters.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal))
                || (parameters.TryGetProperty("startCondition", out var condition) && condition.ValueKind != JsonValueKind.Null)
                || (parameters.TryGetProperty("macro", out var macro) && macro.ValueKind != JsonValueKind.Null && (macro.ValueKind != JsonValueKind.Array || macro.GetArrayLength() != 0)))
            {
                return Error(context, 400, "未対応の引数、起動条件またはマクロが指定されています。");
            }

            bool holding = register && parameters.TryGetProperty("holding", out var holdingValue) && holdingValue.GetBoolean();
            string holdAttr = hold ? parameters.GetProperty("holdAttr").GetString()! : string.Empty;
            if (hold && holdAttr is not ("SET" or "RELEASE"))
            {
                return Error(context, 400, "holdAttrはSETまたはRELEASEで指定してください。");
            }

            string requestId = context.Request.Headers["X-JobRunner-Request-Id"].ToString();
            if (requestId.Length > 100)
            {
                return Error(context, 400, "X-JobRunner-Request-Idは100文字以下で指定してください。");
            }

            string id = "jp1:" + (requestId.Length == 0 ? Guid.NewGuid().ToString("N") : requestId);
            string key = "command:" + StorageRepository.Hash(id);
            string hash = StorageRepository.Hash(JsonSerializer.Serialize(new { register, resource, manager, service, holding, holdAttr }));
            context.Response.Headers["X-JobRunner-Operation-Id"] = id;
            var existing = await storage.GetAsync(key, context.RequestAborted);
            if (existing is not null)
            {
                if (StorageRepository.Read<Operation>(existing).RequestHash != hash)
                {
                    return Error(context, 409, "同じ要求IDに異なる内容が指定されています。");
                }

                return await AwaitResultAsync(context, key, register, storage, clock);
            }

            var activeRow = await storage.GetAsync("definition-active", context.RequestAborted);
            if (activeRow is null || StorageRepository.Read<Activation>(activeRow).Phase != "Ready")
            {
                return Error(context, 412, "スケジューラーの初期化が完了していません。");
            }

            var active = StorageRepository.Read<Activation>(activeRow);
            string target;
            if (register)
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
                Kind = register ? "Register" : holdAttr == "SET" ? "Hold" : "Release",
                Target = target,
                RegistrationKind = "Immediate",
                Due = register ? clock.GetUtcNow() : null,
                Holding = register ? holding : null,
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

            return await AwaitResultAsync(context, key, register, storage, clock);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException)
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
