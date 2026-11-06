using System.Globalization;
using JobRunner.Controllers.Jp1;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Services;
using JobRunner.Storage;

namespace JobRunner.Controllers.Ui;

/// <summary>月間一覧は保存済み予定を読むだけで、未来世代を新規生成しません。</summary>
public static partial class UiEndpoints
{
    private static async Task<MonthlySchedule> ReadMonthlyAsync(string month, string rootPath, StorageRepository storage, DefinitionService definitions, RunnerOptions options, CancellationToken token)
    {
        if (month.Length != 7 || !DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var begin) || begin.Year == 9999 || !rootPath.StartsWith('/'))
        {
            throw new ArgumentException("対象月はyyyy-MM形式、対象パスは完全名で指定してください。");
        }

        var until = begin.AddMonths(1);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
        var active = await ActiveAsync(storage, token);
        var current = (await definitions.ReadAsync(active.DefinitionHash, token)).Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        if (rootPath != "/" && !current.ContainsKey(rootPath))
        {
            throw new KeyNotFoundException("対象ユニットがありません。");
        }

        bool InScope(string path) => rootPath == "/" || path == rootPath || path.StartsWith(rootPath + "/", StringComparison.Ordinal);
        MonthlyUnit ToUnit(UnitDefinition unit) => new(unit.Path, unit.Name, ScheduleCalculator.Parent(unit.Path), unit.Type, unit.Get("cm"), unit.IsNetwork());
        var rows = current.Values.Where(u => InScope(u.Path)).ToDictionary(u => u.Path, ToUnit, StringComparer.Ordinal);
        var versions = new Dictionary<string, Dictionary<string, UnitDefinition>>(StringComparer.Ordinal) { [active.DefinitionHash] = current };
        var stored = (await storage.ListAsync("occurrence:", token)).Select(StorageRepository.Read<Occurrence>).Where(o => o.Namespace == active.Id).ToList();
        var attempts = (await storage.ListAsync("attempt:", token)).Select(StorageRepository.Read<JobAttempt>).ToList();
        var entries = new List<MonthlyEntry>();
        foreach (var root in stored.Where(o => o.Id == o.RootId && (InScope(o.RootPath) || rootPath.StartsWith(o.RootPath + "/", StringComparison.Ordinal))))
        {
            // 子ネットの独立日時・一時変更が月境界を越えるため、ルート日時だけでは絞り込みません。
            var snapshot = await ReadRootAsync(root, storage, token, stored, attempts);
            var values = snapshot.Occurrences;
            var selected = values.Where(o => InScope(o.UnitPath)).Select(o => (Value: o, Date: DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o.Due, zone).DateTime)))
                .Where(v => v.Date >= begin && v.Date < until).ToList();
            if (selected.Count == 0)
            {
                continue;
            }

            var savedRoot = values.Single(o => o.Id == root.Id);
            string hash = savedRoot.DefinitionHash ?? active.DefinitionHash;
            if (!versions.TryGetValue(hash, out var units))
            {
                units = (await definitions.ReadAsync(hash, token)).Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
                versions.Add(hash, units);
            }

            foreach (var (value, date) in selected)
            {
                var unit = units[value.UnitPath];
                rows.TryAdd(unit.Path, ToUnit(unit));

                // 新版で削除されたユニットも、その世代の親階層と一緒に表示します。
                string parent = ScheduleCalculator.Parent(unit.Path);
                while (parent != "/" && InScope(parent) && units.TryGetValue(parent, out var ancestor))
                {
                    rows.TryAdd(parent, ToUnit(ancestor));
                    parent = ScheduleCalculator.Parent(parent);
                }

                var attempt = snapshot.Attempts.FirstOrDefault(a => a.Id == value.AttemptId);
                entries.Add(new MonthlyEntry(value, Jp1Status.Map(value, units, values, DateTimeOffset.UtcNow), savedRoot.Jp1ExecutionId ?? root.Id, unit.IsNetwork() ? unit.Path : ScheduleCalculator.Parent(unit.Path), date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), unit.Get("cm"), value.ManualExitCode ?? attempt?.ExitCode));
            }
        }

        return new MonthlySchedule(month, options.TimeZone, rows.Values.OrderBy(u => u.Path, StringComparer.Ordinal).ToList(), entries.OrderBy(e => e.Execution.Due).ThenBy(e => e.Execution.UnitPath, StringComparer.Ordinal).ThenBy(e => e.Execution.Id, StringComparer.Ordinal).ToList());
    }
}
