using JobRunner.Controllers.Ui;
using JobRunner.Models;
using JobRunner.Scheduling;

namespace JobRunner.Editing;

/// <summary>運用予定とは独立したカレンダーの月表示です。</summary>
public static partial class DefinitionEditor
{
    /// <summary>元のIDを現在のパスへ解決し、実行と同じ評価を返します。</summary>
    /// <param name="compiled">検証済みの下書き定義です。</param>
    /// <param name="request">月と対象の指定です。</param>
    /// <returns>一か月の実効稼働日と根拠です。</returns>
    public static List<EditorCalendarDay> CalendarMonth(DefinitionBundle compiled, EditorCalendarRequest request)
    {
        if (request.Month.Day != 1 || request.Month.Year is < 1994 or > 2036)
        {
            throw new ArgumentException("カレンダー表示は1994〜2036年の月初を指定してください。");
        }

        var paths = EditorPaths(request.Draft);
        var changes = (request.Draft.GroupChanges ?? []).ToDictionary(c => c.Id, StringComparer.Ordinal);
        string? path = paths.GetValueOrDefault(request.UnitId) ?? MapGroupPath(request.UnitId, changes);
        var all = compiled.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        if (path is null || !all.TryGetValue(path, out var unit))
        {
            throw new ArgumentException("カレンダーの対象がありません。");
        }

        return Enumerable.Range(0, DateTime.DaysInMonth(request.Month.Year, request.Month.Month)).Select(offset =>
        {
            var day = request.Month.AddDays(offset);
            return new EditorCalendarDay(day, ScheduleCalculator.EvaluateCalendar(day, unit, all));
        }).ToList();
    }

    private static Dictionary<string, string> EditorPaths(EditorDraft draft)
    {
        var paths = TimingPaths(draft);
        foreach (var network in draft.Networks)
        {
            foreach (var node in network.Nodes)
            {
                paths.TryAdd(node.Id, paths[network.Path].TrimEnd('/') + "/" + node.Name);
            }
        }

        var changes = (draft.GroupChanges ?? []).ToDictionary(c => c.Id, StringComparer.Ordinal);
        return paths.Select(p => (p.Key, Path: MapGroupPath(p.Value, changes))).Where(p => p.Path is not null)
            .ToDictionary(p => p.Key, p => MapNetworkPath(draft, p.Path!), StringComparer.Ordinal);
    }
}
