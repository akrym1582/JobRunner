using JobRunner.Controllers.Ui;
using JobRunner.Models;
using JobRunner.Scheduling;

namespace JobRunner.Editing;

/// <summary>下書きのネストを、祖先の予定から順に副作用なく試算します。</summary>
public static partial class DefinitionEditor
{
    /// <summary>仮のルート予定と同じ業務日のネスト候補を評価します。</summary>
    /// <param name="compiled">下書きを反映し検証した定義です。</param>
    /// <param name="request">仮登録日と業務日範囲です。</param>
    /// <param name="options">時間帯と時間制です。</param>
    /// <param name="cancellationToken">要求取消です。</param>
    /// <returns>ルート候補ごとに祖先経路と対象外理由を持つ結果です。</returns>
    public static EditorNestedSchedulePreview PreviewNestedSchedule(DefinitionBundle compiled, EditorScheduleRequest request, RunnerOptions options, CancellationToken cancellationToken = default)
    {
        if (request.Draft.EditorVersion is not (4 or 5 or 6 or 7 or 8 or 9) || request.Anchor.Year is < 1994 or > 2036 || request.From.Year is < 1994 or > 2036
            || request.Until > new DateOnly(2037, 1, 1) || request.Until <= request.From || request.Until.DayNumber - request.From.DayNumber > 366)
        {
            throw new ArgumentException("ネスト試算は版4以降、1994〜2036年の業務日、終了を含まない1〜366日で指定してください。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var all = compiled.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        var changes = (request.Draft.EditorVersion >= 7 ? request.Draft.GroupChanges ?? [] : []).ToDictionary(c => c.Id, StringComparer.Ordinal);
        var paths = TimingPaths(request.Draft).Select(kv => (Id: kv.Key, Path: MapGroupPath(kv.Value, changes))).Where(kv => kv.Path is not null).ToDictionary(kv => kv.Id, kv => MapNetworkPath(request.Draft, kv.Path!), StringComparer.Ordinal);
        if (!paths.TryGetValue(request.NetworkId, out string? path) || !all.TryGetValue(path, out var target) || !target.IsNetwork())
        {
            throw new ArgumentException("試算対象のネストネットがありません。");
        }

        var chain = new List<UnitDefinition> { target };
        while (all.TryGetValue(ScheduleCalculator.Parent(chain[^1].Path), out var parent) && parent.IsNetwork())
        {
            chain.Add(parent);
        }

        if (chain.Count < 2)
        {
            throw new ArgumentException("ネスト試算は上位ネットを持つネットだけが対象です。ルートは既存の試算を使ってください。");
        }

        chain.Reverse();
        var root = chain[0];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var rows = new List<EditorNestedScheduleRow>();
        try
        {
            var registration = new Registration { Anchor = request.Anchor };
            var points = ScheduleCalculator.Calculate(root, all, registration, request.From, request.Until, options, deadline.Token).OrderBy(p => p.Due).Take(2001).ToList();
            if (points.Count > 2000)
            {
                throw new ArgumentException("ネスト試算は2000行までです。期間を短くしてください。");
            }

            var byDay = points.ToLookup(p => p.BusinessDate);
            for (var day = request.From; day < request.Until; day = day.AddDays(1))
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!byDay[day].Any())
                {
                    rows.Add(new(day, null, null, false, "ROOT_NOT_SCHEDULED", chain.Select((unit, i) => new EditorNestedScheduleStep(unit.Path, unit.Type, false, i == 0 ? "ROOT_NOT_SCHEDULED" : "ANCESTOR_EXCLUDED", null)).ToList()));
                }
                else
                {
                    foreach (var point in byDay[day])
                    {
                        var steps = new List<EditorNestedScheduleStep> { new(root.Path, root.Type, true, "ROOT", point) };
                        var selected = new NestedScheduleSelection(point, true, "ROOT");
                        foreach (var unit in chain.Skip(1))
                        {
                            selected = NestedScheduleSelector.Select(unit, all, registration, selected, options, deadline.Token);
                            steps.Add(new(unit.Path, unit.Type, selected.Enabled, selected.Reason, selected.Enabled ? selected.Point : null));
                        }

                        rows.Add(new(day, point, selected.Enabled ? selected.Point : null, selected.Enabled, selected.Reason, steps));
                    }
                }

                if (rows.Count > 2000)
                {
                    throw new ArgumentException("ネスト試算は2000行までです。期間を短くしてください。");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ArgumentException("ネスト試算の計算時間が5秒を超えました。期間またはルール数を減らしてください。");
        }

        return new(paths.Single(kv => kv.Value == root.Path).Key, root.Path, rows, options.TimeZone);
    }
}
