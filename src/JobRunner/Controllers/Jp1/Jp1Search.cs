namespace JobRunner.Controllers.Jp1;

/// <summary>JP1の検索定数とワイルドカードを、内部状態とは分けて扱います。</summary>
public static class Jp1Search
{
    /// <summary>集合条件でも応答の状態は変えず、該当する実状態だけを選びます。</summary>
    /// <param name="status">JP1形式の実状態です。</param>
    /// <param name="filter">検索条件です。</param>
    /// <returns>公式の状態集合に含まれる場合trueです。</returns>
    public static bool MatchesStatus(string status, string filter) => filter switch
    {
        "GRP_WAIT" => status is "TIMEWAIT" or "TERMWAIT" or "EXECWAIT",
        "GRP_RUN" => status is "RUNNING" or "WACONT" or "ABCONT",
        "GRP_ABNORMAL" => status is "ABNORMAL" or "INTERRUPT" or "KILL" or "FAIL" or "UNKNOWN",
        "GRP_NORMAL" => status is "NORMAL" or "NORMALFALSE",
        _ => status == filter,
    };

    /// <summary>比較先がない場合も、不完全なエスケープを入力段階で拒否します。</summary>
    /// <param name="pattern">JP1のRE指定です。</param>
    public static void ValidatePattern(string pattern)
    {
        bool escaped = false;
        foreach (char c in pattern)
        {
            escaped = !escaped && c == '\\';
        }

        if (escaped)
        {
            throw new Jp1ParameterException("unitName", 3206, "エスケープの後に文字が必要です。");
        }
    }

    /// <summary>全体一致で照合し、正規表現の実行や指数的なバックトラックを行いません。</summary>
    /// <param name="name">単純ユニット名です。</param>
    /// <param name="pattern">*、?、バックスラッシュのエスケープを含む指定です。</param>
    /// <returns>一致した場合trueです。</returns>
    public static bool MatchesName(string name, string pattern)
    {
        ValidatePattern(pattern);
        var text = name.EnumerateRunes().ToArray();
        var symbols = pattern.EnumerateRunes().ToArray();
        var previous = new bool[text.Length + 1];
        previous[0] = true;
        for (int i = 0; i < symbols.Length; i++)
        {
            int symbol = symbols[i].Value;
            bool escaped = symbol == '\\';
            if (escaped)
            {
                symbol = symbols[++i].Value;
            }

            var next = new bool[text.Length + 1];
            bool star = !escaped && symbol == '*';
            next[0] = star && previous[0];
            for (int j = 1; j <= text.Length; j++)
            {
                next[j] = star ? previous[j] || next[j - 1] : previous[j - 1] && ((!escaped && symbol == '?') || text[j - 1].Value == symbol);
            }

            previous = next;
        }

        return previous[text.Length];
    }
}
