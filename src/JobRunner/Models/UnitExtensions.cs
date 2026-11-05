namespace JobRunner.Models;

/// <summary>保持した属性と階層を、定義を書き換えずに参照する共通処理です。</summary>
public static class UnitExtensions
{
    /// <summary>通常・リカバリーのネットを同じ階層処理へ渡します。</summary>
    /// <param name="unit">対象のユニットです。</param>
    /// <returns>該当種別ならtrueです。</returns>
    public static bool IsNetwork(this UnitDefinition unit) => unit.Type is "n" or "rn";

    /// <summary>異常時にだけ開始する属性を識別します。</summary>
    /// <param name="unit">対象のユニットです。</param>
    /// <returns>該当種別ならtrueです。</returns>
    public static bool IsRecovery(this UnitDefinition unit) => unit.Type is "rj" or "rp" or "rn" or "rjdj";

    /// <summary>条件を保存して後続を解放する判定ジョブです。</summary>
    /// <param name="unit">対象のユニットです。</param>
    /// <returns>該当種別ならtrueです。</returns>
    public static bool IsJudgment(this UnitDefinition unit) => unit.Type is "jdj" or "rjdj";

    /// <summary>単一値属性を取得します。繰り返し可否は有効化前に検証します。</summary>
    /// <param name="unit">対象ユニットです。</param>
    /// <param name="key">JP1属性名です。</param>
    /// <param name="fallback">属性を省略したときの明示的な既定値です。</param>
    /// <returns>定義値または省略時の値です。</returns>
    public static string Get(this UnitDefinition unit, string key, string fallback = "") =>
        unit.Attributes.TryGetValue(key, out var values) ? values[0] : fallback;

    /// <summary>ネットとグループの全階層を列挙し、参照先の一括検証に使用します。</summary>
    /// <param name="units">探索を開始する最上位ユニットです。</param>
    /// <returns>親を先に返す全ユニットです。</returns>
    public static IEnumerable<UnitDefinition> Flatten(this IEnumerable<UnitDefinition> units)
    {
        foreach (var unit in units)
        {
            yield return unit;
            foreach (var child in unit.Children.Flatten())
            {
                yield return child;
            }
        }
    }
}
