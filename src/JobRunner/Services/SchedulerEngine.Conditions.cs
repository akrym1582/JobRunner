using System.Globalization;
using Azure.Data.Tables;
using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Storage;

namespace JobRunner.Services;

/// <summary>保存済み結果に基づき、条件分岐と異常時後続を判定します。</summary>
public sealed partial class SchedulerEngine
{
    /// <summary>明示的な不成立だけを非実行へ進め、結果不明を成功として扱いません。</summary>
    /// <param name="occurrence">判定する世代です。</param>
    /// <param name="units">固定定義です。</param>
    /// <param name="all">同じ読み取り時点の世代です。</param>
    /// <returns>実行しない条件が確定している場合trueです。</returns>
    private static bool ShouldExclude(Occurrence occurrence, Dictionary<string, UnitDefinition> units, IEnumerable<Occurrence> all)
    {
        var siblings = all.Where(o => o.RootId == occurrence.RootId).ToDictionary(o => o.UnitPath, StringComparer.Ordinal);
        string parent = ScheduleCalculator.Parent(occurrence.UnitPath);
        if (siblings[parent].State == "NotScheduled")
        {
            return true;
        }

        var incoming = DefinitionValidator.Edges(units[parent]).Where(e => e.To == occurrence.UnitPath).ToList();
        if (incoming.Any(e => e.Conditional && siblings[e.From].State == "Succeeded" && siblings[e.From].ConditionMatched == false))
        {
            return true;
        }

        return units[occurrence.UnitPath].IsRecovery() && (incoming.Count == 0 || incoming.Any(e =>
            units[e.From].IsRecovery()
                ? siblings[e.From].State is "Warning" or "Failed" or "TimedOut" or "NotScheduled" or "Cancelled" or "Interrupted"
                : siblings[e.From].State is "Succeeded" or "Warning" or "NotScheduled" or "Cancelled"));
    }

    /// <summary>文字列の値を勝手に補完せず、選択された比較だけを評価します。</summary>
    /// <param name="unit">比較条件です。</param>
    /// <param name="value">先行が保存した値。nullは未定義です。</param>
    /// <returns>条件成立の有無です。</returns>
    private static bool EvaluateVariable(UnitDefinition unit, string? value)
    {
        string kind = unit.Get("ej");
        if (kind is "snn" or "snl")
        {
            return kind == "snn" ? !string.IsNullOrEmpty(value) : string.IsNullOrEmpty(value);
        }

        if (value is null)
        {
            throw new FormatException("比較する変数が未定義です。");
        }

        string expected = unit.Get("ejt");
        if (kind.StartsWith('s'))
        {
            return kind switch
            {
                "sce" => value == expected,
                "sne" => value != expected,
                "spe" => value.Contains(expected, StringComparison.Ordinal),
                "spn" => !value.Contains(expected, StringComparison.Ordinal),
                _ => throw new FormatException("未対応の文字列比較です。"),
            };
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 0)
        {
            throw new FormatException("変数を非負の32ビット整数として解釈できません。");
        }

        var numeric = new UnitDefinition();
        numeric.Attributes["ej"] = [kind[1..]];
        foreach (var (source, target) in new[] { ("eji", "ejc"), ("ejs", "ejl"), ("ejg", "ejh"), ("ejm", "ejm"), ("eju", "eju") })
        {
            if (unit.Attributes.TryGetValue(source, out var values))
            {
                numeric.Attributes[target] = values;
            }
        }

        return EvaluateCondition(numeric, (uint)number);
    }

    /// <summary>符号なし終了コードをJP1の比較条件で判定します。</summary>
    /// <param name="unit">判定ジョブの定義です。</param>
    /// <param name="code">永続化された終了コードです。</param>
    /// <returns>判定条件の成立を表します。</returns>
    private static bool EvaluateCondition(UnitDefinition unit, uint code)
    {
        uint threshold = uint.Parse(unit.Get("ejc", "0"), CultureInfo.InvariantCulture);
        uint lower = uint.Parse(unit.Get("ejl", "0"), CultureInfo.InvariantCulture);
        uint upper = uint.Parse(unit.Get("ejh", "0"), CultureInfo.InvariantCulture);
        bool within = (unit.Get("ejm", "ge") == "ge" ? code >= lower : code > lower) && (unit.Get("eju", "le") == "le" ? code <= upper : code < upper);
        return unit.Get("ej", "gt") switch
        {
            "gt" => code > threshold,
            "ge" => code >= threshold,
            "lt" => code < threshold,
            "le" => code <= threshold,
            "eq" => code == threshold,
            "ne" => code != threshold,
            "ri" => within,
            "ro" => !within,
            _ => throw new InvalidDataException("保存済み判定条件が未対応です。"),
        };
    }

    /// <summary>最初の開始を保持し、再開は別の日時として保存します。</summary>
    /// <param name="occurrence">開始する世代です。</param>
    private void MarkStarted(Occurrence occurrence)
    {
        if (occurrence.StartedAt is null)
        {
            occurrence.StartedAt = clock.GetUtcNow();
        }
        else
        {
            occurrence.RestartedAt = clock.GetUtcNow();
        }

        occurrence.Started = true;
    }

    /// <summary>先行試行と現在のファイル観測から、一度だけ判定結果を保存します。</summary>
    /// <param name="row">判定対象行です。</param>
    /// <param name="occurrence">判定対象世代です。</param>
    /// <param name="unit">判定条件です。</param>
    /// <param name="units">固定した定義です。</param>
    /// <param name="all">同一時点の世代です。</param>
    /// <param name="token">停止通知です。</param>
    /// <returns>判定結果の保存完了です。</returns>
    private async Task JudgeAsync(TableEntity row, Occurrence occurrence, UnitDefinition unit, Dictionary<string, UnitDefinition> units, IEnumerable<Occurrence> all, CancellationToken token)
    {
        try
        {
            string kind = unit.Get("ej", "gt");
            bool result;
            if (kind is "ef" or "nf")
            {
                bool exists;
                try
                {
                    exists = (File.GetAttributes(unit.Get("ejf")) & FileAttributes.Directory) == 0;
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    exists = false;
                }

                result = kind == "ef" ? exists : !exists;
            }
            else
            {
                var edge = DefinitionValidator.Edges(units[ScheduleCalculator.Parent(unit.Path)]).Single(e => e.To == unit.Path);
                var predecessor = all.Single(v => v.RootId == occurrence.RootId && v.UnitPath == edge.From);
                var attemptRow = await storage.GetAsync("attempt:" + predecessor.AttemptId, token);
                var attempt = attemptRow is null ? null : StorageRepository.Read<JobAttempt>(attemptRow);
                if ((attempt is null || attempt.OutputError is not null) && (kind.StartsWith('v') || kind.StartsWith('s') || predecessor.ManualExitCode is null))
                {
                    throw new FormatException("判定に必要な先行試行または出力がありません。");
                }

                if (kind.StartsWith('v') || kind.StartsWith('s'))
                {
                    string? value = attempt!.OutputVariables?.GetValueOrDefault(unit.Get("ejv").Trim('?'));
                    result = EvaluateVariable(unit, value);
                }
                else
                {
                    int code = predecessor.ManualExitCode ?? attempt?.ExitCode ?? throw new FormatException("先行試行の終了コードがありません。");
                    result = EvaluateCondition(unit, unchecked((uint)code));
                }
            }

            occurrence.ConditionMatched = result;
            occurrence.State = "Succeeded";
            occurrence.StatusDetail = result ? "NORMAL" : "NORMALFALSE";
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            occurrence.State = "Failed";
            occurrence.StatusDetail = "ABNORMAL";
            occurrence.Reason = "判定エラー: " + ex.Message;
        }

        MarkStarted(occurrence);
        occurrence.FinishedAt = clock.GetUtcNow();
        await CommitAsync([Update(row, occurrence)], token);
    }
}
