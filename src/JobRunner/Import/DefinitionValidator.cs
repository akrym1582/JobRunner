using System.Globalization;
using System.Text.RegularExpressions;
using JobRunner.Models;
using JobRunner.Scheduling;

namespace JobRunner.Import;

/// <summary>実行に影響する未知属性、配置衝突、循環を有効化より前に検出します。</summary>
public static partial class DefinitionValidator
{
    /// <summary>全ユニットに適用する属性。表示属性は保存し、実行には用いません。</summary>
    private static readonly HashSet<string> Common = ["ty", "cm", "sz", "el", "ex"];

    /// <summary>運用日、休業日、日付切り替えを定義するグループ属性です。</summary>
    private static readonly HashSet<string> Group = ["op", "cl", "stt", "sdd", "md"];

    /// <summary>予定と通常接続の実装に対応するネット属性です。</summary>
    private static readonly HashSet<string> Network = ["sd", "st", "cy", "sh", "shd", "ed", "ar", "ha", "jc", "mp", "cftd", "ln", "de", "sy", "ey", "fd"];

    /// <summary>ローカル実行器が解釈するコマンドと終了判定の属性です。</summary>
    private static readonly HashSet<string> Job = ["te", "sc", "prm", "wkp", "env", "etm", "jd", "wth", "tho", "ha"];

    /// <summary>先行終了コードだけを参照する判定ジョブ属性です。</summary>
    private static readonly HashSet<string> Judgment = ["ej", "ejc", "ejl", "ejh", "ejm", "eju", "ejf", "ejv", "ejt", "eji", "ejs", "ejg", "ha"];

    /// <summary>繰り返しが意味を持つ属性。その他は二重指定を拒否します。</summary>
    private static readonly HashSet<string> Repeated = ["sd", "st", "cy", "sh", "shd", "ar", "op", "cl", "env", "el", "cftd", "ln", "sy", "ey"];

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
                "n" or "rn" => Network,
                "j" or "pj" or "rj" or "rp" => Job,
                "jdj" or "rjdj" => Judgment,
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

                if (key != "ejv" && values.Any(v => v.Contains("?AJS2", StringComparison.Ordinal)))
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

            if (unit.Type is "j" or "pj" or "rj" or "rp")
            {
                ValidateJob(unit);
            }

            if (unit.IsJudgment())
            {
                ValidateJudgment(unit);
            }

            if (unit.Get("ha", "n") is not ("y" or "n") || unit.Get("mp", "n") is not ("y" or "n"))
            {
                throw Error(unit, "未対応の保留・多重起動指定です。");
            }

            if (unit.IsNetwork())
            {
                ValidateSchedule(unit, options);
                DefinitionDelayMonitor.Validate(unit);
            }

            foreach (var entry in unit.Attributes.GetValueOrDefault("op") ?? [])
            {
                ValidateCalendar(entry);
            }

            foreach (var entry in unit.Attributes.GetValueOrDefault("cl") ?? [])
            {
                ValidateCalendar(entry);
            }

            if (!Regex.IsMatch(unit.Get("sdd", "1"), @"^([1-9]|[12][0-9]|3[01]|(su|mo|tu|we|th|fr|sa):[1-5])$") || unit.Get("md", "th") is not ("th" or "ne"))
            {
                throw Error(unit, "基準日または月区分が不正です。");
            }

            if ((int.TryParse(unit.Get("sdd"), out int baseDay) && baseDay > 28) || unit.Get("sdd").EndsWith(":5", StringComparison.Ordinal))
            {
                throw Error(unit, "29〜31日または第5曜日の基準日は、欠落月の仕様照合が未完了のため現在未対応です。");
            }

            if (unit.Get("stt") is { Length: > 0 } baseline && ScheduleCalculator.ParseTime(baseline) >= 1440)
            {
                throw Error(unit, "カレンダー基準時刻は24時未満です。");
            }
        }

        foreach (var unit in all.Values)
        {
            string parentPath = ScheduleCalculator.Parent(unit.Path);
            if (unit.IsNetwork() && (!all.TryGetValue(parentPath, out var monitorParent) || !monitorParent.IsNetwork())
                && new[] { "sy", "ey" }.SelectMany(key => unit.Attributes.GetValueOrDefault(key) ?? []).Any(raw => ScheduleCalculator.SplitRule(raw).Value.StartsWith('M') || ScheduleCalculator.SplitRule(raw).Value.StartsWith('U')))
            {
                throw Error(unit, "ルートネットのM/U遅延監視は未対応です。");
            }

            if (all.TryGetValue(parentPath, out var parent) && parent.Type is not ("g" or "n" or "rn"))
            {
                throw Error(unit, "配置先の親がグループまたはネットではありません。");
            }

            if (unit.Type == "g" && parent is not null && parent.IsNetwork())
            {
                throw Error(unit, "ネットの下位にグループは配置できません。");
            }

            if (parent?.IsNetwork() == true && unit.IsNetwork() && unit.Attributes.ContainsKey("sd") && unit.Get("de", "y") != "n")
            {
                throw Error(unit, "ネストの独立予定はde=nを指定してください。");
            }

            if (unit.Attributes.ContainsKey("de") && (unit.Get("de") is not ("y" or "n") || parent?.IsNetwork() != true || (unit.Get("de") == "y" && unit.Attributes.ContainsKey("sd"))))
            {
                throw Error(unit, "上位依存deはネストだけに指定でき、yとsdは併用できません。");
            }

            if (unit.IsRecovery() && (parent is null || !parent.IsNetwork()))
            {
                throw Error(unit, "リカバリーユニットはネット内だけに定義できます。");
            }

            string reference = unit.Get("jc");
            if (reference.Length > 0 && (!all.TryGetValue(reference, out var calendar) || calendar.Type != "g"))
            {
                throw Error(unit, "参照カレンダーのグループが存在しません。");
            }

            var graph = unit.Children.ToDictionary(c => c.Path, _ => new List<string>(), StringComparer.Ordinal);

            // 接続の構文解析は一度だけ行い、子ごとの先行・条件接続検証へ再利用します。
            var incomingEdges = new Dictionary<string, List<(string From, string To, bool Conditional)>>(StringComparer.Ordinal);
            var conditionalCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (from, to, conditional) in Edges(unit))
            {
                if (!graph.ContainsKey(from) || !graph.ContainsKey(to))
                {
                    throw Error(unit, "接続先が同じネット直下に存在しません。");
                }

                if (all[from].IsRecovery() && !all[to].IsRecovery())
                {
                    throw Error(unit, "リカバリーの後続はリカバリーユニットにしてください。");
                }

                graph[from].Add(to);
                if (!incomingEdges.TryGetValue(to, out var edges))
                {
                    edges = [];
                    incomingEdges.Add(to, edges);
                }

                edges.Add((from, to, conditional));
                if (conditional)
                {
                    conditionalCounts[from] = conditionalCounts.GetValueOrDefault(from) + 1;
                }
            }

            foreach (var child in unit.Children)
            {
                var incoming = incomingEdges.GetValueOrDefault(child.Path) ?? [];
                if (child.IsJudgment() && (incoming.Count != 1 || incoming[0].Conditional || all[incoming[0].From].Type is not ("j" or "pj" or "rj" or "rp")))
                {
                    throw Error(child, "終了コード判定の先行は通常ジョブ一つにしてください。");
                }

                if (incoming.Any(e => e.Conditional) && (incoming.Count != 1 || !all[incoming[0].From].IsJudgment()))
                {
                    throw Error(child, "条件接続の先行は判定ジョブ一つにしてください。");
                }

                if (conditionalCounts.GetValueOrDefault(child.Path) > 1)
                {
                    throw Error(child, "判定ジョブと従属ユニットは一対一です。");
                }
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
    public static IEnumerable<(string From, string To)> Connections(UnitDefinition network) => Edges(network).Select(e => (e.From, e.To));

    /// <summary>順接続と条件接続を区別して取得します。</summary>
    /// <param name="network">接続定義のネットです。</param>
    /// <returns>先行・後続・接続種別です。</returns>
    public static IEnumerable<(string From, string To, bool Conditional)> Edges(UnitDefinition network)
    {
        foreach (string edge in network.Attributes.GetValueOrDefault("ar") ?? [])
        {
            var match = Regex.Match(edge, @"^\(f=([^,]+),t=([^,\)]+)(?:,(seq|con))?\)$");
            if (!match.Success)
            {
                throw Error(network, "順接続と条件接続以外は未対応です。");
            }

            yield return (network.Path + "/" + match.Groups[1].Value, network.Path + "/" + match.Groups[2].Value, match.Groups[3].Value == "con");
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

        // pjはLinuxでもローカルコマンドとして受理する独自拡張です。実行方式はホストOSで決めます。
        if (unit.Type is "j" or "rj" && OperatingSystem.IsWindows())
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
        foreach (string key in new[] { "sd", "st", "cy", "sh", "shd", "cftd", "ln" })
        {
            var rules = new HashSet<int>();
            foreach (string raw in unit.Attributes.GetValueOrDefault(key) ?? [])
            {
                var (rule, value) = ScheduleCalculator.SplitRule(raw);
                if (!rules.Add(rule) || rule is < 0 or > 144 || (rule == 0 && (key != "sd" || value != "ud")))
                {
                    throw Error(unit, $"重複または不正なルール番号: {key}");
                }

                if (key == "sd" && !Regex.IsMatch(value, @"^(en|ud|(?:(?:\d{4}/)?\d{1,2}/)?(?:[+*@]?(?:\d{1,2}|b(?:-\d{1,2})?)|[+]?(?:su|mo|tu|we|th|fr|sa)(?::[1-5b])?))$"))
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

                if (key == "ln" && (!int.TryParse(value, out int upper) || upper is < 1 or > 144))
                {
                    throw Error(unit, "上位ルール番号は1〜144です。");
                }

                if (key == "cftd" && !Regex.IsMatch(value, @"^(no|be|af|db|da)(,([1-9]|[12][0-9]|3[01]))?(,([1-9]|[12][0-9]|3[01]))?$"))
                {
                    throw Error(unit, "不正な起算スケジュールです。");
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
