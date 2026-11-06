using System.Globalization;
using System.Text.Json;
using Azure;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Services;
using JobRunner.Storage;
using Microsoft.AspNetCore.Http.Features;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Controllers.Jp1;

/// <summary>JP1形式の定義と状態参照を提供します。</summary>
public static partial class Jp1Endpoints
{
    /// <summary>検索時に使う世代をルート単位で選び、子と異なる世代を混ぜません。</summary>
    /// <param name="roots">対象ルートの全世代です。</param>
    /// <param name="generation">検索方式です。</param>
    /// <param name="execId">明示的な実行IDです。</param>
    /// <param name="begin">期間開始です。</param>
    /// <param name="end">期間終了です。</param>
    /// <param name="now">検索時刻です。</param>
    /// <returns>対象となるルート世代です。</returns>
    private static IEnumerable<Occurrence> SelectGenerations(IEnumerable<Occurrence> roots, string generation, string? execId, DateTimeOffset? begin, DateTimeOffset? end, DateTimeOffset now)
    {
        var list = roots.ToList();
        return generation switch
        {
            "NO" => list,
            "EXECID" => list.Where(r => r.Jp1ExecutionId == execId),
            "RESULT" => list.Where(r => r.FinishedAt.HasValue).OrderByDescending(r => r.FinishedAt).ThenBy(r => r.Id, StringComparer.Ordinal).Take(1),
            "PERIOD" => list.Where(r =>
            {
                var start = r.StartedAt ?? (r.Started ? r.OriginalDue : r.Due < now ? now : r.Due);
                var finish = r.FinishedAt ?? (r.Started ? now : start);
                return start <= end && finish >= begin;
            }),

            // VIEWSTATUSRANGE=all: 実行中、再実行待ち、次回予定の順。終了済み結果はRESULTで取得します。
            _ => list.Where(r => r.State is "Running" or "Starting" or "Unknown").OrderByDescending(r => r.RestartedAt ?? r.StartedAt ?? r.Due)
                .Concat(list.Where(r => r.Started && r.State is "Waiting" or "Held").OrderByDescending(r => r.RestartedAt ?? r.StartedAt ?? r.Due))
                .Concat(list.Where(r => !r.Started && r.State is "Waiting" or "Held").OrderBy(r => r.Due)).Take(1),
        };
    }

    /// <summary>一覧と単体のGETを、保存済み状態だけから構築します。</summary>
    /// <param name="context">HTTP要求です。</param>
    /// <param name="storage">状態と退避履歴です。</param>
    /// <param name="options">サービス識別と時刻です。</param>
    /// <param name="definitions">固定定義です。</param>
    /// <param name="clock">検索基準時刻です。</param>
    /// <returns>公式形状の状態リソースまたは入力エラーです。</returns>
    private static async Task<IResult> ReadAsync(HttpContext context, StorageRepository storage, RunnerOptions options, DefinitionService definitions, TimeProvider clock)
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "Thu, 01 Jan 1970 00:00:00 GMT";
        try
        {
            var token = context.RequestAborted;
            if (context.Request.Query.Any(p => p.Value.Count != 1))
            {
                return Error(context, 400, "クエリの二重指定は未対応です。", code: "JOBRUNNER_UNSUPPORTED_PARAMETER");
            }

            var query = JsonSerializer.SerializeToElement(context.Request.Query.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal));
            string manager = RequiredString(query, "manager", 255);
            string service = RequiredString(query, "serviceName", 30);
            if (manager != options.Jp1ManagerName || service != options.SchedulerId)
            {
                return Error(context, 404, "マネージャーまたはサービスがありません。", code: "JOBRUNNER_NOT_FOUND");
            }

            string raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path.Value!;
            string path = Uri.UnescapeDataString(raw.Split('?')[0]);
            const string detailsSuffix = "/actions/execResultDetails/invoke";
            bool details = path.EndsWith(detailsSuffix, StringComparison.Ordinal);
            if (details)
            {
                path = path[..^detailsSuffix.Length];
            }

            const string prefix = "/ajs/api/v1/objects/statuses";
            bool single = path.Length > prefix.Length + 1;
            if (details && !single)
            {
                return Error(context, 404, "ユニット完全名と実行IDが必要です。");
            }

            string? resource = single ? path[(prefix.Length + 1)..] : null;
            var allowed = details ? new[] { "manager", "serviceName" } : single ? new[] { "manager", "serviceName", "searchConnectInfo" }
                : new[] { "mode", "manager", "serviceName", "location", "searchLowerUnits", "searchTarget", "unitName", "unitNameMatchMethods", "unitType", "generation", "execID", "periodBegin", "periodEnd", "status", "delayStatus", "holdPlan", "execHost", "execHostMatchMethods", "releaseID", "releaseInfoSearchMethods" };
            if (query.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal)))
            {
                return Error(context, 400, "未対応の検索パラメーターです。", code: "JOBRUNNER_UNSUPPORTED_PARAMETER");
            }

            if (single && Choice(query, "searchConnectInfo", "NO", "NO", "YES") != "NO")
            {
                return Error(context, 400, "接続情報の取得は未対応です。", code: "JOBRUNNER_UNSUPPORTED_PARAMETER");
            }

            if (!single)
            {
                _ = Choice(query, "mode", null, "search");
            }

            string location = single ? string.Empty : RequiredString(query, "location", 930);
            string lower = single ? "NO" : Choice(query, "searchLowerUnits", "NO", "NO", "YES");
            string search = single ? "DEFINITION_AND_STATUS" : Choice(query, "searchTarget", "DEFINITION_AND_STATUS", "DEFINITION", "DEFINITION_AND_STATUS");
            string type = single ? "NO" : Choice(query, "unitType", "NO", "NO", "GROUP", "ROOT", "NET", "JOB");
            string method = single ? "NO" : Choice(query, "unitNameMatchMethods", "NO", "NO", "EQ", "BW", "EW", "NE", "CO", "NC");
            string name = string.Empty;
            if (method != "NO")
            {
                if (!query.TryGetProperty("unitName", out _))
                {
                    throw new Jp1ParameterException("unitName", 3207, "比較方法にユニット名が必要です。");
                }

                name = RequiredString(query, "unitName", 30);
            }

            bool definitionOnly = search == "DEFINITION" || type == "GROUP";
            string filter = single || definitionOnly ? "NO" : Choice(query, "status", "NO", "NO", "UNREGISTERED", "NOPLAN", "UNEXEC", "BYPASS", "TIMEWAIT", "TERMWAIT", "EXECWAIT", "HOLDING", "RUNNING", "WACONT", "ABCONT", "ABNORMAL", "INTERRUPT", "KILL", "FAIL", "UNKNOWN", "WARNING", "NORMAL", "NORMALFALSE");
            string generation = single ? "EXECID" : definitionOnly || filter == "UNREGISTERED" ? "NO" : Choice(query, "generation", "STATUS", "NO", "STATUS", "RESULT", "PERIOD", "EXECID");
            bool ignoreExecution = definitionOnly || filter == "UNREGISTERED";
            string delayFilter = single || ignoreExecution ? "NO" : Choice(query, "delayStatus", "NO", "NO", "START", "END", "YES");
            foreach (string key in new[] { "holdPlan", "execHostMatchMethods", "releaseInfoSearchMethods" })
            {
                bool ignored = key == "releaseInfoSearchMethods" ? type == "GROUP" : ignoreExecution || (key == "execHostMatchMethods" && type is "ROOT" or "NET");
                if (!ignored && query.TryGetProperty(key, out var field) && field.GetString() != "NO")
                {
                    return Error(context, 400, "未対応の検索条件です。", code: "JOBRUNNER_UNSUPPORTED_PARAMETER");
                }
            }

            string? execId = null;
            string? unitPath = null;
            if (single)
            {
                int colon = resource!.LastIndexOf(':');
                if (colon < 1)
                {
                    return Error(context, 404, "ユニット完全名と実行IDが必要です。", code: "JOBRUNNER_NOT_FOUND");
                }

                unitPath = resource[..colon];
                execId = resource[(colon + 1)..];
            }
            else if (!definitionOnly && generation == "EXECID" && filter != "UNREGISTERED")
            {
                execId = RequiredString(query, "execID", 16);
            }

            DateTimeOffset? begin = null;
            DateTimeOffset? end = null;
            if (!definitionOnly && generation == "PERIOD" && filter != "UNREGISTERED")
            {
                begin = ParameterTime(query, "periodBegin", options);
                end = ParameterTime(query, "periodEnd", options);
                if (begin > end || query.GetProperty("periodBegin").GetString()![11..13].CompareTo("23") > 0 || query.GetProperty("periodEnd").GetString()![11..13].CompareTo("23") > 0)
                {
                    throw new Jp1ParameterException("periodEnd", 3206, "検索期間が不正です。");
                }
            }

            var activeRow = await storage.GetAsync("definition-active", token);
            if (activeRow is null || StorageRepository.Read<Activation>(activeRow).Phase != "Ready")
            {
                return Error(context, 412, "初期化が完了していません。", code: "JOBRUNNER_NOT_READY");
            }

            if ((await storage.ListAsync("command:", token)).Select(StorageRepository.Read<Operation>).Any(o => o.Status == "Pending" && o.MutationPath is not null))
            {
                return Error(context, 409, "予定変更の適用中です。再取得してください。", code: "JOBRUNNER_MUTATION_IN_PROGRESS");
            }

            var active = StorageRepository.Read<Activation>(activeRow);
            var units = (await definitions.ReadAsync(active.DefinitionHash, token)).Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
            var registrations = (await storage.ListAsync($"registration:{active.Id}:", token)).Select(StorageRepository.Read<Registration>).ToList();
            var occurrences = (await storage.ListAsync("occurrence:", token)).Select(StorageRepository.Read<Occurrence>).Where(o => o.Namespace == active.Id).ToList();
            var attempts = (await storage.ListAsync("attempt:", token)).Select(StorageRepository.Read<JobAttempt>).ToDictionary(a => a.Id, StringComparer.Ordinal);
            foreach (var root in occurrences.Where(o => o.Id == o.RootId && o.State is "Archiving" or "Archived").ToList())
            {
                var content = await storage.Container.GetBlobClient(root.ArchivePath).DownloadContentAsync(token);
                var archive = content.Value.Content.ToObjectFromJson<HistoryArchive>() ?? throw new InvalidDataException("退避履歴がありません。");
                if (archive.RootId != root.Id || archive.FormatVersion != 1)
                {
                    throw new InvalidDataException("退避履歴が一致しません。");
                }

                occurrences.RemoveAll(o => o.RootId == root.Id);
                occurrences.AddRange(archive.Rows.Where(r => r.Key.StartsWith("occurrence:", StringComparison.Ordinal)).Select(r => JsonSerializer.Deserialize<Occurrence>(r.Json)!));
                foreach (var row in archive.Rows.Where(r => r.Key.StartsWith("attempt:", StringComparison.Ordinal)))
                {
                    var attempt = JsonSerializer.Deserialize<JobAttempt>(row.Json)!;
                    attempts[attempt.Id] = attempt;
                }
            }

            var activePaths = units.Keys.ToHashSet(StringComparer.Ordinal);
            var versions = new Dictionary<string, Dictionary<string, UnitDefinition>>(StringComparer.Ordinal) { [active.DefinitionHash] = new(units, StringComparer.Ordinal) };
            foreach (string hash in occurrences.Select(o => o.DefinitionHash ?? active.DefinitionHash).Distinct(StringComparer.Ordinal))
            {
                if (!versions.ContainsKey(hash))
                {
                    versions[hash] = (await definitions.ReadAsync(hash, token)).Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
                }

                if (!definitionOnly)
                {
                    foreach (var old in versions[hash])
                    {
                        units.TryAdd(old.Key, old.Value);
                    }
                }
            }

            var selected = new List<object>();
            var now = clock.GetUtcNow();
            foreach (var unit in units.Values.OrderBy(u => u.Path, StringComparer.Ordinal))
            {
                bool isRoot = unit.IsNetwork() && (!units.TryGetValue(ScheduleCalculator.Parent(unit.Path), out var parent) || parent.Type == "g");
                string simple = unit.Path[(unit.Path.LastIndexOf('/') + 1)..];
                bool nameMatch = method switch { "EQ" => simple == name, "NE" => simple != name, "BW" => simple.StartsWith(name, StringComparison.Ordinal), "EW" => simple.EndsWith(name, StringComparison.Ordinal), "CO" => simple.Contains(name, StringComparison.Ordinal), "NC" => !simple.Contains(name, StringComparison.Ordinal), _ => true };
                if (single ? unit.Path != unitPath : (!nameMatch || (lower == "NO" ? ScheduleCalculator.Parent(unit.Path) != location : !unit.Path.StartsWith(location.TrimEnd('/') + "/", StringComparison.Ordinal))
                    || (type == "GROUP" && unit.Type != "g") || (type == "NET" && !unit.IsNetwork()) || (type == "ROOT" && !isRoot) || (type == "JOB" && (unit.Type == "g" || unit.IsNetwork()))))
                {
                    continue;
                }

                string rootPath = unit.Path;
                while (units.TryGetValue(ScheduleCalculator.Parent(rootPath), out var ancestor) && ancestor.IsNetwork())
                {
                    rootPath = ancestor.Path;
                }

                bool registered = registrations.Any(r => r.RootPath == rootPath && !r.Disabled);
                if (filter == "UNREGISTERED" && registered)
                {
                    continue;
                }

                var roots = SelectGenerations(occurrences.Where(o => o.RootPath == rootPath && o.Id == o.RootId), generation, execId, begin, end, now).ToList();
                var candidates = definitionOnly || unit.Type == "g" || filter == "UNREGISTERED" ? new List<Occurrence?> { null }
                    : roots.Select(root => occurrences.SingleOrDefault(o => o.RootId == root.Id && o.UnitPath == unit.Path)).Where(o => o is not null).ToList();
                if (candidates.Count == 0 && !single && activePaths.Contains(unit.Path) && generation is "STATUS" or "NO")
                {
                    candidates.Add(null);
                }

                foreach (var candidate in candidates)
                {
                    var fixedUnits = candidate is null ? units : versions[candidate.DefinitionHash ?? active.DefinitionHash];
                    var fixedUnit = candidate is null ? unit : fixedUnits[unit.Path];
                    string status = candidate is null ? unit.Type == "g" || definitionOnly ? "NONE" : registered ? "NOPLAN" : "NOENTRY" : Jp1Status.Map(candidate, fixedUnits, occurrences, now);
                    if (filter is not ("NO" or "UNREGISTERED") && status != filter)
                    {
                        continue;
                    }

                    var root = candidate is null ? null : occurrences.Single(o => o.Id == candidate.RootId);
                    var attempt = candidate?.AttemptId is string attemptId ? attempts.GetValueOrDefault(attemptId) : null;
                    if (details && candidate is not null)
                    {
                        return await ExecutionDetailsAsync(context, candidate, attempt, storage);
                    }

                    var delay = candidate is null ? (Start: false, End: false) : DelayMonitoring.Evaluate(candidate, occurrences, options, now);
                    var nestedDelays = candidate is null ? [] : occurrences.Where(o => o.RootId == candidate.RootId && o.UnitPath.StartsWith(candidate.UnitPath + "/", StringComparison.Ordinal)).Select(o => DelayMonitoring.Evaluate(o, occurrences, options, now)).ToList();
                    bool startDelayed = delay.Start || nestedDelays.Any(d => d.Start);
                    bool endDelayed = delay.End || nestedDelays.Any(d => d.End);
                    if ((delayFilter == "START" && !startDelayed) || (delayFilter == "END" && !endDelayed) || (delayFilter == "YES" && !startDelayed && !endDelayed))
                    {
                        continue;
                    }

                    var monitor = candidate?.DelayMonitor;
                    var startLimit = candidate is null ? null : DelayMonitoring.Limit(candidate, monitor?.Start, monitor?.StartMinutes, occurrences, options);
                    var endLimit = candidate is null ? null : DelayMonitoring.Limit(candidate, monitor?.End, monitor?.EndMinutes, occurrences, options);
                    if (monitor?.Duration == true && (candidate?.RestartedAt ?? candidate?.StartedAt) is DateTimeOffset began)
                    {
                        var durationLimit = began.AddMinutes(monitor.DurationMinutes!.Value);
                        endLimit = endLimit is null || durationLimit < endLimit ? durationLimit : endLimit;
                    }

                    string noDelay = candidate is null ? "NONE" : fixedUnit.IsNetwork() ? "NO" : "UNSUPPORTED";
                    selected.Add(new
                    {
                        definition = new { unitName = unit.Path, simpleUnitName = simple, rootJobnetName = unit.Type == "g" ? string.Empty : rootPath, unitComment = fixedUnit.Get("cm"), unitType = fixedUnit.Type == "g" ? "GROUP" : fixedUnit.IsNetwork() ? isRoot ? "ROOTNET" : "NET" : fixedUnit.IsJudgment() ? "JDJOB" : fixedUnit.Type is "pj" or "rp" ? "PJOB" : "JOB", owner = fixedUnit.OwnerName ?? string.Empty, jp1ResourceGroup = fixedUnit.ResourceGroup ?? string.Empty, unitID = -1, execAgent = fixedUnit.Get("ex"), execFileName = fixedUnit.Get("sc"), parameters = fixedUnit.Get("prm"), customJobType = string.Empty, registerStatus = registered ? "YES" : "NO", recoveryUnit = fixedUnit.IsRecovery(), wait = false, jobnetReleaseUnit = false },
                        unitStatus = new { unitName = unit.Path, simpleUnitName = simple, execID = root?.Jp1ExecutionId ?? string.Empty, status, schStartTime = Stamp(candidate?.Due, options), startTime = Stamp(candidate?.StartedAt ?? attempt?.StartedAt, options), reStartTime = Stamp(candidate?.RestartedAt, options), endTime = Stamp(candidate?.FinishedAt, options), retCode = (candidate?.ManualExitCode ?? attempt?.ExitCode) is int exitCode ? unchecked((uint)exitCode).ToString(CultureInfo.InvariantCulture) : string.Empty, startDelayStatus = delay.Start ? "YES" : noDelay, endDelayStatus = delay.End ? "YES" : noDelay, nestStartDelayStatus = nestedDelays.Any(d => d.Start) ? "YES" : noDelay, nestEndDelayStatus = nestedDelays.Any(d => d.End) ? "YES" : noDelay, startDelayTime = Stamp(startLimit, options), endDelayTime = Stamp(endLimit, options), delayedStart = candidate is null ? "NONE" : monitor?.Start ?? (fixedUnit.IsNetwork() ? "NOT" : "UNSUPPORTED"), delayedStartTime = MonitorTime(monitor?.StartMinutes), delayedEnd = candidate is null ? "NONE" : monitor?.End ?? (fixedUnit.IsNetwork() ? "NOT" : "UNSUPPORTED"), delayedEndTime = MonitorTime(monitor?.EndMinutes), monitoringJobnet = candidate is null ? "NONE" : !fixedUnit.IsNetwork() ? "UNSUPPORTED" : monitor?.Duration == true ? "TIME_REQUIRED_FOR_EXECUTION_YES" : "TIME_REQUIRED_FOR_EXECUTION_NO", timeRequiredForExecution = MonitorTime(monitor?.DurationMinutes), holdAttr = candidate is null ? "NONE" : candidate.HoldOverride is bool holding ? holding ? "HOLD" : "RELEASE" : fixedUnit.Get("ha", "n") == "y" ? "YES" : "NO", changeType = candidate is null ? "NONE" : candidate.ScheduleChangeId is null ? "NO" : candidate.State == "Cancelled" ? "CANCEL" : "TIME", execHost = string.Empty },
                        release = (object?)null,
                    });
                    if (selected.Count > 1000)
                    {
                        return Error(context, 400, "検索結果が1000件を超えています。条件を絞ってください。", code: "JOBRUNNER_RESULT_LIMIT");
                    }
                }
            }

            return single ? selected.Count == 1 ? Results.Json(selected[0]) : Error(context, 404, "指定された世代がありません。", code: "JOBRUNNER_NOT_FOUND")
                : Results.Json(new { statuses = selected, all = true });
        }
        catch (Jp1ParameterException ex)
        {
            return Error(context, 400, ex.Message, ex.Parameter, ex.MessageId);
        }
        catch (RequestFailedException)
        {
            return Error(context, 412, "保存先にアクセスできません。", code: "JOBRUNNER_STORAGE_UNAVAILABLE");
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentOutOfRangeException)
        {
            return Error(context, 500, "保存状態または定義が不正です。", code: "JOBRUNNER_INVALID_STATE");
        }
    }

    /// <summary>未取得の日時は空文字として返します。</summary>
    /// <param name="value">保存した日時です。</param>
    /// <param name="options">サービス時刻です。</param>
    /// <returns>秒精度のISO日時です。</returns>
    private static string Stamp(DateTimeOffset? value, RunnerOptions options) => value is null ? string.Empty : TimeZoneInfo.ConvertTime(value.Value, TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
}
