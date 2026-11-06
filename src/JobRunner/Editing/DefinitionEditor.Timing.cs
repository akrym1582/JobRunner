using System.Globalization;
using JobRunner.Controllers.Ui;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;

namespace JobRunner.Editing;

/// <summary>階層変更後の原文に予定属性だけを適用します。</summary>
public static partial class DefinitionEditor
{
    private static readonly HashSet<string> ScheduleKeys = ["sd", "st", "cy", "sh", "shd", "cftd", "ln", "ed", "jc", "de"];
    private static readonly HashSet<string> CalendarKeys = ["op", "cl", "stt", "sdd", "md"];
    private static readonly HashSet<string> MultipleTimingKeys = ["sd", "st", "cy", "sh", "shd", "cftd", "ln", "op", "cl"];

    /// <summary>下書きから計算した候補だけを返し、登録・予定・世代は保存しません。</summary>
    /// <param name="compiled">下書きを反映し検証した定義です。</param>
    /// <param name="request">試算条件です。</param>
    /// <param name="options">時間制と時間帯です。</param>
    /// <returns>ルートの候補です。ネストを独立ルートとして計算しません。</returns>
    public static EditorSchedulePreview PreviewSchedule(DefinitionBundle compiled, EditorScheduleRequest request, RunnerOptions options)
    {
        if (request.Draft.EditorVersion is not (4 or 5) || request.Anchor.Year is < 1994 or > 2036 || request.From.Year is < 1994 or > 2036
            || request.Until > new DateOnly(2037, 1, 1) || request.Until <= request.From || request.Until.DayNumber - request.From.DayNumber > 366)
        {
            throw new ArgumentException("下書き試算は版4/5、1994〜2036年の業務日、終了を含まない1〜366日で指定してください。");
        }

        var paths = TimingPaths(request.Draft);
        var all = compiled.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        if (!paths.TryGetValue(request.NetworkId, out string? path) || !all.TryGetValue(path, out var root)
            || !root.IsNetwork() || (all.TryGetValue(ScheduleCalculator.Parent(path), out var parent) && parent.IsNetwork()))
        {
            throw new ArgumentException("下書き試算はルートネットだけが対象です。ネストには上位世代の条件が必要です。");
        }

        var points = ScheduleCalculator.Calculate(root, all, new Registration { Anchor = request.Anchor }, request.From, request.Until, options).OrderBy(p => p.Due).Take(2001).ToList();
        if (points.Count > 2000)
        {
            throw new ArgumentException("下書き試算は2000候補までです。期間を短くしてください。");
        }

        return new(points, options.TimeZone);
    }

    private static List<EditorAttributes> ProjectTiming(DefinitionBundle bundle, bool schedule)
    {
        // 配置先から補われる仮想グループには書き換え可能な原文がありません。
        var present = bundle.Sources.SelectMany(s => new Document(s).Syntax.Keys).ToHashSet(StringComparer.Ordinal);
        return bundle.Units.Flatten().Where(u => present.Contains(u.Path) && (schedule ? u.IsNetwork() : u.Type == "g"))
            .Select(u => new EditorAttributes(u.Path, u.Attributes.Where(a => (schedule ? ScheduleKeys : CalendarKeys).Contains(a.Key)).ToDictionary(a => a.Key, a => a.Value.ToList(), StringComparer.Ordinal))).ToList();
    }

    private static List<SourceEvidence> RenderTiming(DefinitionBundle bundle, EditorDraft draft)
    {
        var schedules = draft.Schedules ?? throw new ArgumentException("予定属性の投影がありません。元版を読み直してください。");
        var calendars = draft.Calendars ?? throw new ArgumentException("カレンダー属性の投影がありません。元版を読み直してください。");
        if (!schedules.Select(s => s.Id).Order(StringComparer.Ordinal).SequenceEqual(draft.Networks.Select(n => n.Path).Order(StringComparer.Ordinal))
            || schedules.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != schedules.Count
            || !calendars.Select(c => c.Id).Order(StringComparer.Ordinal).SequenceEqual(ProjectTiming(bundle, false).Select(c => c.Id).Order(StringComparer.Ordinal)))
        {
            throw new ArgumentException("予定またはカレンダーの対象が一致しません。グループの追加・削除・改名はできません。");
        }

        // 原文と安定IDの検証は版3と共通です。構造変更後に属性を適用し、改名前のパスを再使用しません。
        var sources = Render(bundle, draft with { EditorVersion = 3 });
        var documents = sources.Select(s => new Document(s)).ToList();
        var locations = documents.SelectMany(d => d.Syntax.Select(s => (s.Key, Document: d, Syntax: s.Value))).ToDictionary(s => s.Key, StringComparer.Ordinal);
        var units = sources.SelectMany(s => Jp1DefinitionParser.Parse(Decode(s).TrimStart('\uFEFF'), s.Mapping)).Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        var paths = TimingPaths(draft);
        void Apply(EditorAttributes entry, string path, HashSet<string> allowed)
        {
            if (entry.Attributes.Any(a => !allowed.Contains(a.Key) || a.Value.Count == 0 || (!MultipleTimingKeys.Contains(a.Key) && a.Value.Count != 1)))
            {
                throw new ArgumentException($"{path}: 未対応・空配列・重複した予定属性です。");
            }

            var unit = units[path];
            foreach (string key in allowed)
            {
                var values = entry.Attributes.GetValueOrDefault(key) ?? [];
                if ((unit.Attributes.GetValueOrDefault(key) ?? []).SequenceEqual(values))
                {
                    continue; // 変更しない表記と未使用属性は原文に残します。
                }

                foreach (string value in values)
                {
                    ValidateTimingValue(key, value);
                }

                if (key == "sd" && values.Count > 1 && values.Any(v => ScheduleCalculator.SplitRule(v).Value == "ud"))
                {
                    throw new ArgumentException("予定なしの0,udと他の開始日は併記できません。");
                }

                var location = locations[path];
                location.Document.SetValues(location.Syntax, key, values, key == "jc", ordered: true);
            }
        }

        foreach (var schedule in schedules)
        {
            Apply(schedule, paths[schedule.Id], ScheduleKeys);
        }

        foreach (var calendar in calendars)
        {
            Apply(calendar, calendar.Id, CalendarKeys);
        }

        return documents.Select(d => d.Render()).ToList();
    }

    private static Dictionary<string, string> TimingPaths(EditorDraft draft)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var networks = draft.Networks.ToDictionary(n => n.Path, StringComparer.Ordinal);
        void Visit(EditorNode node, string parent)
        {
            if (!paths.TryAdd(node.Id, parent.TrimEnd('/') + "/" + node.Name))
            {
                throw new ArgumentException("ネット階層が循環または重複しています。");
            }

            foreach (var child in networks[node.Id].Nodes.Where(n => n.Type is "n" or "rn"))
            {
                Visit(child, paths[node.Id]);
            }
        }

        foreach (var root in draft.Roots ?? [])
        {
            Visit(root.Node, root.ParentPath);
        }

        return paths;
    }

    private static void ValidateTimingValue(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl)
            || (key is not ("jc" or "ex") && value.Any(c => ";{}\"#".Contains(c, StringComparison.Ordinal))))
        {
            throw new ArgumentException($"{key}: 空値や構文の区切り文字は指定できません。省略する場合は指定を解除してください。");
        }

        if ((key == "ed" || (key is "op" or "cl" && value.Contains('/'))) && (!DateOnly.TryParseExact(value, "yyyy/M/d", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || end.Year is < 1994 or > 2036))
        {
            throw new ArgumentException($"{key}は1994〜2036年の実在する年月日で指定してください。");
        }

        if (key != "sd")
        {
            return;
        }

        var (rule, spec) = ScheduleCalculator.SplitRule(value);
        if (spec == "ud")
        {
            if (rule != 0)
            {
                throw new ArgumentException("予定なしは0,udで指定してください。");
            }

            return;
        }

        if (spec == "en")
        {
            return;
        }

        var parts = spec.Split('/');
        string selector = parts[^1];
        int year = 2000;
        int month = 1;
        if (parts.Length > 3 || (parts.Length == 3 && (!int.TryParse(parts[0], out year) || year is < 1994 or > 2036))
            || (parts.Length >= 2 && (!int.TryParse(parts[^2], out month) || month is < 1 or > 12)))
        {
            throw new ArgumentException("sdの年月が範囲外です。");
        }

        bool relative = selector.StartsWith('+') || selector.StartsWith('*') || selector.StartsWith('@');
        selector = selector.TrimStart('+', '*', '@');
        if (int.TryParse(selector, out int day) && (day < 1 || day > (relative ? 35 : parts.Length >= 2 ? DateTime.DaysInMonth(year, month) : 31)))
        {
            throw new ArgumentException("sdの日が範囲外または存在しません。");
        }

        if (selector.StartsWith("b-", StringComparison.Ordinal) && int.TryParse(selector[2..], out int back) && back > (relative ? 34 : parts.Length >= 2 ? DateTime.DaysInMonth(year, month) - 1 : 30))
        {
            throw new ArgumentException("sdの末日からの日数が範囲外です。");
        }
    }
}
