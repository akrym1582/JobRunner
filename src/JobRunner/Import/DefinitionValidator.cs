using System.Globalization;
using System.Text.RegularExpressions;
using JobRunner.Models;
using JobRunner.Scheduling;

namespace JobRunner.Import;

/// <summary>実行に影響する未知属性、配置衝突、循環を有効化より前に検出します。</summary>
public static class DefinitionValidator
{
    /// <summary>全ユニットに適用する属性。表示属性は保存し、実行には用いません。</summary>
    private static readonly HashSet<string> Common = ["ty", "cm", "sz", "el", "ex"];

    /// <summary>運用日、休業日、日付切り替えを定義するグループ属性です。</summary>
    private static readonly HashSet<string> Group = ["op", "cl", "stt"];

    /// <summary>予定と通常接続の実装に対応するネット属性です。</summary>
    private static readonly HashSet<string> Network = ["sd", "st", "cy", "sh", "shd", "ed", "ar", "ha", "jc", "mp"];

    /// <summary>ローカル実行器が解釈するコマンドと終了判定の属性です。</summary>
    private static readonly HashSet<string> Job = ["te", "sc", "prm", "wkp", "env", "etm", "jd", "wth", "tho", "ha"];

    /// <summary>繰り返しが意味を持つ属性。その他は二重指定を拒否します。</summary>
    private static readonly HashSet<string> Repeated = ["sd", "st", "cy", "sh", "shd", "ar", "op", "cl", "env", "el"];

    /// <summary>全ファイルを統合してから参照と属性の整合性を検証します。</summary>
    /// <param name="units">配置済みの最上位ユニットです。</param>
    /// <param name="options">ローカル実行先への明示的な対応付けです。</param>
    public static void Validate(List<UnitDefinition> units, RunnerOptions options)
    {
        var all = new Dictionary<string, UnitDefinition>(StringComparer.Ordinal);
        foreach (var unit in units.Flatten())
        {
            if (!all.TryAdd(unit.Path, unit))
            {
                throw Error(unit, "同じ完全名のユニットが複数入力にあります。");
            }

            var allowed = unit.Type switch
            {
                "g" => Group,
                "n" => Network,
                "j" or "pj" => Job,
                _ => throw Error(unit, $"未対応のユニット種別: {unit.Type}"),
            };
            foreach (var (key, values) in unit.Attributes)
            {
                if (!Common.Contains(key) && !allowed.Contains(key))
                {
                    throw Error(unit, $"未対応の実行属性: {key}");
                }

                if (values.Count > 1 && !Repeated.Contains(key))
                {
                    throw Error(unit, $"属性が二重に指定されています: {key}");
                }

                if (values.Any(v => v.Contains("?AJS2", StringComparison.Ordinal)))
                {
                    throw Error(unit, "マクロ変数は未対応です。");
                }
            }

            if (unit.PermissionMode is { Length: 4 } mode && mode[0] >= '4' && (unit.OwnerName is null || !options.LocalUsers.Contains(unit.OwnerName, StringComparer.Ordinal)))
            {
                throw Error(unit, "所有者実行のJP1ユーザーをLocalUsersで現在のOSユーザーへ明示的に対応付けてください。");
            }

            if (unit.Get("ex") is { Length: > 0 } agent && !options.LocalAgents.Contains(agent, StringComparer.Ordinal))
            {
                throw Error(unit, "実行先をLocalAgentsへ明示的に対応付けてください。");
            }

            if (unit.Type is "j" or "pj")
            {
                ValidateJob(unit);
            }

            if (unit.Get("ha", "n") is not ("y" or "n") || unit.Get("mp", "n") is not ("y" or "n"))
            {
                throw Error(unit, "未対応の保留・多重起動指定です。");
            }

            if (unit.Type == "n")
            {
                ValidateSchedule(unit, options);
            }

            foreach (var entry in unit.Attributes.GetValueOrDefault("op") ?? [])
            {
                ValidateCalendar(entry);
            }

            foreach (var entry in unit.Attributes.GetValueOrDefault("cl") ?? [])
            {
                ValidateCalendar(entry);
            }

            if (unit.Get("stt") is { Length: > 0 } baseline && ScheduleCalculator.ParseTime(baseline) >= 1440)
            {
                throw Error(unit, "カレンダー基準時刻は24時未満です。");
            }
        }

        foreach (var unit in all.Values)
        {
            string parentPath = ScheduleCalculator.Parent(unit.Path);
            if (all.TryGetValue(parentPath, out var parent) && parent.Type is not ("g" or "n"))
            {
                throw Error(unit, "配置先の親がグループまたはネットではありません。");
            }

            if (unit.Type == "g" && parent is not null && parent.Type == "n")
            {
                throw Error(unit, "ネットの下位にグループは配置できません。");
            }

            if (unit.Type == "n" && parent is not null && parent.Type == "n" && unit.Attributes.Keys.Any(k => k is "sd" or "st" or "cy" or "sh" or "shd" or "ed"))
            {
                throw Error(unit, "ネストの独立スケジュールは未対応です。");
            }

            string reference = unit.Get("jc");
            if (reference.Length > 0 && (!all.TryGetValue(reference, out var calendar) || calendar.Type != "g"))
            {
                throw Error(unit, "参照カレンダーのグループが存在しません。");
            }

            var graph = unit.Children.ToDictionary(c => c.Path, _ => new List<string>(), StringComparer.Ordinal);
            foreach (var (from, to) in Connections(unit))
            {
                if (!graph.ContainsKey(from) || !graph.ContainsKey(to))
                {
                    throw Error(unit, "接続先が同じネット直下に存在しません。");
                }

                graph[from].Add(to);
            }

            var visiting = new HashSet<string>();
            var done = new HashSet<string>();
            foreach (string path in graph.Keys)
            {
                Visit(path, graph, visiting, done);
            }
        }
    }

    /// <summary>通常接続を完全名へ変換し、依存関係の判定へ渡します。</summary>
    /// <param name="network">ar属性を持つネットです。</param>
    /// <returns>先行と後続の完全名の組です。</returns>
    public static IEnumerable<(string From, string To)> Connections(UnitDefinition network)
    {
        foreach (string edge in network.Attributes.GetValueOrDefault("ar") ?? [])
        {
            var match = Regex.Match(edge, @"^\(f=([^,]+),t=([^,\)]+)(?:,seq)?\)$");
            if (!match.Success)
            {
                throw Error(network, "通常接続以外のar属性は未対応です。");
            }

            yield return (network.Path + "/" + match.Groups[1].Value, network.Path + "/" + match.Groups[2].Value);
        }
    }

    /// <summary>ローカル実行器で解釈できる終了判定と実行形式だけを受け入れます。</summary>
    /// <param name="unit">検証するジョブです。</param>
    private static void ValidateJob(UnitDefinition unit)
    {
        if (unit.Children.Count > 0 || (unit.Get("te").Length == 0) == (unit.Get("sc").Length == 0))
        {
            throw Error(unit, "ジョブはteまたはscの一方が必要で、下位ユニットは持てません。");
        }

        if (unit.Get("jd", "cod") is not ("cod" or "nm" or "ab"))
        {
            throw Error(unit, "ファイルによる終了判定は未対応です。");
        }

        foreach (string key in new[] { "tho", "wth", "etm" })
        {
            if (unit.Get(key) is { Length: > 0 } value && (!int.TryParse(value, out int number) || number < 0 || (key == "etm" && number is < 1 or > 1440)))
            {
                throw Error(unit, $"不正な数値属性: {key}");
            }
        }

        if (unit.Get("wth") is { Length: > 0 } warning && int.Parse(warning, CultureInfo.InvariantCulture) > int.Parse(unit.Get("tho", "0"), CultureInfo.InvariantCulture))
        {
            throw Error(unit, "警告しきい値は異常しきい値以下にしてください。");
        }

        if ((unit.Type == "j" && OperatingSystem.IsWindows()) || (unit.Type == "pj" && !OperatingSystem.IsWindows()))
        {
            throw Error(unit, "ジョブ種別が実行OSに一致しません。");
        }

        foreach (string value in unit.Attributes.GetValueOrDefault("env") ?? [])
        {
            if (value.IndexOf('=') < 1)
            {
                throw Error(unit, "環境変数は名前=値で指定してください。");
            }
        }
    }

    /// <summary>ルールの重複や未対応の指定を予定生成の前に拒否します。</summary>
    /// <param name="unit">検証するネットです。</param>
    /// <param name="options">時間制の設定です。</param>
    private static void ValidateSchedule(UnitDefinition unit, RunnerOptions options)
    {
        foreach (string key in new[] { "sd", "st", "cy", "sh", "shd" })
        {
            var rules = new HashSet<int>();
            foreach (string raw in unit.Attributes.GetValueOrDefault(key) ?? [])
            {
                var (rule, value) = ScheduleCalculator.SplitRule(raw);
                if (!rules.Add(rule) || rule is < 0 or > 144 || (rule == 0 && (key != "sd" || value != "ud")))
                {
                    throw Error(unit, $"重複または不正なルール番号: {key}");
                }

                if (key == "sd" && !Regex.IsMatch(value, @"^(en|ud|(?:(?:\d{4}/)?\d{1,2}/)?(?:[*@]?(?:\d{1,2}|b(?:-\d{1,2})?)|(?:su|mo|tu|we|th|fr|sa)(?::[1-5b])?))$"))
                {
                    throw Error(unit, "相対日など未対応のsd指定です。");
                }

                if (key == "st")
                {
                    int minutes = ScheduleCalculator.ParseTime(value.TrimStart('+'));
                    if (options.ScheduleTimeMode == 24 && minutes >= 1440)
                    {
                        throw Error(unit, "24時間制の24時以降は仕様確認中のため有効化できません（JP1の不正値という意味ではありません）。");
                    }
                }

                if (key == "cy" && !Regex.IsMatch(value, @"^\(([1-9]|[12][0-9]|3[01]),[ymwd]\)$"))
                {
                    throw Error(unit, "不正な処理サイクルです。");
                }

                if (key == "cy")
                {
                    var parts = value.Trim('(', ')').Split(',');
                    int count = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    int maximum = parts[1] switch { "y" => 9, "m" => 12, "w" => 5, _ => 31 };
                    if (count > maximum)
                    {
                        throw Error(unit, "処理サイクルが単位ごとの上限を超えています。");
                    }
                }

                if (key == "sh" && value is not ("be" or "af" or "ca" or "no"))
                {
                    throw Error(unit, "不正な振り替え方法です。");
                }

                if (key == "shd" && (!int.TryParse(value, out int days) || days is < 1 or > 31))
                {
                    throw Error(unit, "振り替え猶予日数は1〜31です。");
                }
            }
        }
    }

    /// <summary>曜日または実在する日付のみをカレンダー値として受け入れます。</summary>
    /// <param name="value">opまたはclの値です。</param>
    private static void ValidateCalendar(string value)
    {
        if (!Regex.IsMatch(value, "^(su|mo|tu|we|th|fr|sa)$") && !DateOnly.TryParseExact(value, "yyyy/M/d", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new FormatException($"不正なカレンダー指定: {value}");
        }
    }

    /// <summary>深さ優先探索で、互いに待ち続ける接続の循環を検出します。</summary>
    /// <param name="path">現在のユニットです。</param>
    /// <param name="graph">接続グラフです。</param>
    /// <param name="visiting">現在の探索経路です。</param>
    /// <param name="done">検証済みユニットです。</param>
    private static void Visit(string path, Dictionary<string, List<string>> graph, HashSet<string> visiting, HashSet<string> done)
    {
        if (done.Contains(path))
        {
            return;
        }

        if (!visiting.Add(path))
        {
            throw new FormatException($"接続が循環しています: {path}");
        }

        foreach (string next in graph[path])
        {
            Visit(next, graph, visiting, done);
        }

        visiting.Remove(path);
        done.Add(path);
    }

    /// <summary>配置後の完全名と原文位置を含む検証結果を作ります。</summary>
    /// <param name="unit">問題があるユニットです。</param>
    /// <param name="message">拒否理由です。</param>
    /// <returns>有効化を中断する例外です。</returns>
    private static FormatException Error(UnitDefinition unit, string message) => new($"{unit.Source}:{unit.Line} {unit.Path}: {message}");
}
