using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using JobRunner.Models;

namespace JobRunner.Import;

/// <summary>判定の入力を実行前に検証します。</summary>
public static partial class DefinitionValidator
{
    /// <summary>未対応の条件を偽へ丸めず、有効化前に拒否します。</summary>
    /// <param name="unit">対象の判定ジョブです。</param>
    private static void ValidateJudgment(UnitDefinition unit)
    {
        string kind = unit.Get("ej", "gt");
        if (unit.Children.Count != 0 || kind is not ("gt" or "ge" or "lt" or "le" or "eq" or "ne" or "ri" or "ro" or "ef" or "nf" or "vgt" or "vge" or "vlt" or "vle" or "veq" or "vne" or "vri" or "vro" or "sce" or "spe" or "sne" or "spn" or "snn" or "snl")
            || unit.Get("ejm", "ge") is not ("gt" or "ge") || unit.Get("eju", "le") is not ("lt" or "le"))
        {
            throw Error(unit, "判定条件または境界条件が不正です。");
        }

        bool variable = kind.StartsWith('v') || kind.StartsWith('s');
        if (variable && !Regex.IsMatch(unit.Get("ejv"), @"^\?AJS2[A-Z0-9.]{1,64}\?$"))
        {
            throw Error(unit, "変数名は?AJS2xxxxx?で指定してください。");
        }

        if (kind is "ef" or "nf" && (!Path.IsPathFullyQualified(unit.Get("ejf")) || Encoding.UTF8.GetByteCount(unit.Get("ejf")) > 260 || unit.Attributes.ContainsKey("ejc")))
        {
            throw Error(unit, "ファイル判定は260バイト以下の絶対パスを指定してください。");
        }

        if (kind is "sce" or "spe" or "sne" or "spn" && (!unit.Attributes.ContainsKey("ejt") || Encoding.UTF8.GetByteCount(unit.Get("ejt")) > 511))
        {
            throw Error(unit, "文字列の判定値は511バイト以下です。");
        }

        foreach (string key in variable ? new[] { "eji", "ejs", "ejg" } : new[] { "ejc", "ejl", "ejh" })
        {
            if (!uint.TryParse(unit.Get(key, "0"), NumberStyles.None, CultureInfo.InvariantCulture, out uint number) || (variable && number > int.MaxValue))
            {
                throw Error(unit, "判定数値が範囲外です。");
            }
        }

        if (kind is "ri" or "ro" or "vri" or "vro")
        {
            string low = variable ? "ejs" : "ejl";
            string high = variable ? "ejg" : "ejh";
            if (!unit.Attributes.ContainsKey(low) || !unit.Attributes.ContainsKey(high)
                || long.Parse(unit.Get(low), CultureInfo.InvariantCulture) + (unit.Get("ejm", "ge") == "gt" ? 1L : 0) > long.Parse(unit.Get(high), CultureInfo.InvariantCulture) - (unit.Get("eju", "le") == "lt" ? 1L : 0))
            {
                throw Error(unit, "判定範囲に整数が存在しません。");
            }
        }
    }
}
