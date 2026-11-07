using System.Text.Json;
using Azure.Data.Tables;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Storage;

namespace JobRunner.Services;

/// <summary>確認画面と実行器で共有する、運用反映の影響判定です。</summary>
public static class DefinitionPublication
{
    /// <summary>個別操作・開始済み・消化済みの世代を通常予定の置換から保護します。</summary>
    /// <param name="members">同じルートに属する全世代です。</param>
    /// <param name="now">確認時刻です。</param>
    /// <returns>旧定義を維持する理由。nullなら通常の未開始予定です。</returns>
    public static string? Protection(IEnumerable<Occurrence> members, DateTimeOffset now)
    {
        var values = members.ToList();
        var root = values.Single(o => o.Id == o.RootId);
        if (root.State == "Superseded")
        {
            return "置換済み";
        }

        if (values.Any(o => o.Started || o.StartedAt is not null || o.RestartedAt is not null || o.AttemptId is not null || o.State is "Starting" or "Running" or "Unknown"))
        {
            return "開始済み・結果不明";
        }

        if (values.Any(o => o.IndividuallyModified || o.ScheduleChangeId is not null || o.AddedByChangeId is not null))
        {
            return "個別運用操作あり";
        }

        if (root.State is not ("Waiting" or "Held"))
        {
            return "取消・消化済み";
        }

        return root.Due < now ? "時刻経過済み" : null;
    }

    /// <summary>原予定枠を定義版と分け、同日の時刻変更や切戻しで消化済みを復活させません。</summary>
    /// <param name="registration">登録の明示条件です。</param>
    /// <param name="rule">スケジュールルール番号です。</param>
    /// <param name="day">元の業務日です。</param>
    /// <param name="due">間隔・日時・即時登録の元の日時です。</param>
    /// <returns>Tableの抑止記録キーです。</returns>
    public static string SlotKey(Registration registration, int rule, DateOnly day, DateTimeOffset due) => "generation-slot:" + StorageRepository.Hash(JsonSerializer.Serialize(new { registration.Namespace, registration.Id, Rule = rule, Day = day, Due = registration.FixedDue is not null || registration.Kind == "Immediate" ? due : (DateTimeOffset?)null }));

    /// <summary>反映前に確認した対象行と版を照合するハッシュです。</summary>
    /// <param name="activation">現在の採用行です。</param>
    /// <param name="registrations">対象領域の全登録です。</param>
    /// <param name="occurrences">対象領域の全世代です。</param>
    /// <param name="now">時刻経過による置換可否の変更も照合する基準時刻です。</param>
    /// <param name="controls">停止状態の確認対象です。</param>
    /// <returns>確認以後の変更を検知するハッシュです。</returns>
    public static string ImpactHash(TableEntity? activation, IEnumerable<TableEntity> registrations, IEnumerable<TableEntity> occurrences, DateTimeOffset now, IEnumerable<TableEntity>? controls = null)
    {
        var rows = occurrences.ToList();
        var versions = (activation is null ? [] : new[] { activation }).Concat(registrations).Concat(rows).Concat(controls ?? []).OrderBy(r => r.RowKey, StringComparer.Ordinal).Select(r => new { r.RowKey, ETag = r.ETag.ToString() });
        var protections = rows.Select(StorageRepository.Read<Occurrence>).GroupBy(o => o.RootId).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new { RootId = g.Key, Reason = Protection(g, now) });
        return StorageRepository.Hash(JsonSerializer.Serialize(new { Versions = versions, Protections = protections }));
    }

    /// <summary>世代行へ固定する完全名がStorageの文字列上限に収まることを反映前に確認します。</summary>
    /// <param name="bundle">反映予定の定義です。</param>
    public static void ValidateCapacity(DefinitionBundle bundle)
    {
        foreach (var root in Roots(bundle).Values)
        {
            foreach (var unit in new[] { root }.Flatten())
            {
                // StorageRepositoryはJson列をUTF-16で60000バイトまでに制限します。
                // 日時・ID・状態・監視・運用項目へ4000文字の余白を残します。
                if (JsonSerializer.Serialize(new { UnitPath = unit.Path, RootPath = root.Path }).Length > 26000)
                {
                    throw new FormatException("ユニットの完全名が運用状態へ保存できる長さを超えています。名前または階層を短くしてください。");
                }
            }
        }
    }

    /// <summary>通常のルートネットを抽出します。</summary>
    /// <param name="bundle">検証済みの定義です。</param>
    /// <returns>自動登録対象です。</returns>
    public static Dictionary<string, UnitDefinition> Roots(DefinitionBundle bundle)
    {
        var all = bundle.Units.Flatten().ToDictionary(u => u.Path, StringComparer.Ordinal);
        return all.Values.Where(u => u.Type == "n" && (!all.TryGetValue(ScheduleCalculator.Parent(u.Path), out var parent) || parent.Type == "g")).ToDictionary(u => u.Path, StringComparer.Ordinal);
    }
}
