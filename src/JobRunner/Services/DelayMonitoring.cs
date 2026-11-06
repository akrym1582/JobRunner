using JobRunner.Models;
using JobRunner.Scheduling;

namespace JobRunner.Services;

/// <summary>永続時刻から遅延を評価し、照会のたびに状態を書き換えません。</summary>
public static class DelayMonitoring
{
    /// <summary>開始と終了の遅延有無を算出します。</summary>
    /// <param name="value">評価する世代です。</param>
    /// <param name="all">同じルートに属する世代です。</param>
    /// <param name="options">サービス時刻の設定です。</param>
    /// <param name="now">評価するUTC日時です。</param>
    /// <returns>開始と終了の遅延有無です。</returns>
    public static (bool Start, bool End) Evaluate(Occurrence value, IEnumerable<Occurrence> all, RunnerOptions options, DateTimeOffset now)
    {
        var settings = value.DelayMonitor;
        if (settings is null || value.State is "Cancelled" or "NotScheduled" or "Skipped" or "Superseded")
        {
            return (false, false);
        }

        var startLimit = Limit(value, settings.Start, settings.StartMinutes, all, options);
        var endLimit = Limit(value, settings.End, settings.EndMinutes, all, options);
        var actualStart = value.RestartedAt ?? value.StartedAt;
        bool start = startLimit is not null && (actualStart is DateTimeOffset started ? started > startLimit : !value.Started && now >= startLimit);
        bool end = endLimit is not null && (value.FinishedAt is DateTimeOffset finished ? finished > endLimit : now >= endLimit);
        if (settings.Duration == true && actualStart is DateTimeOffset began)
        {
            var limit = began.AddMinutes(settings.DurationMinutes!.Value);
            end |= value.FinishedAt is DateTimeOffset ended ? ended > limit : now >= limit;
        }

        return (start, end);
    }

    /// <summary>監視時刻を世代の業務日または参照ネットの予定から求めます。</summary>
    /// <param name="value">対象世代です。</param>
    /// <param name="mode">監視方式です。</param>
    /// <param name="minutes">時刻または相対分です。</param>
    /// <param name="all">同一ルートの世代です。</param>
    /// <param name="options">サービス時刻です。</param>
    /// <returns>監視しない場合nullです。</returns>
    public static DateTimeOffset? Limit(Occurrence value, string? mode, int? minutes, IEnumerable<Occurrence> all, RunnerOptions options)
    {
        if (mode is null or "NOT")
        {
            return null;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
        DateTime local;
        if (mode == "ABSOLUTE")
        {
            local = value.BusinessDate.ToDateTime(TimeOnly.MinValue).AddMinutes(minutes!.Value);
        }
        else
        {
            var reference = mode == "OWN" ? value : all.Single(o => o.RootId == value.RootId && (mode == "ROOT" ? o.Id == value.RootId : o.UnitPath == ScheduleCalculator.Parent(value.UnitPath)));
            local = TimeZoneInfo.ConvertTime(reference.Due, zone).DateTime.AddMinutes(minutes!.Value);
        }

        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
        {
            throw new FormatException("遅延監視日時が存在しないか曖昧です。");
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
