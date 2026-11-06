using System.Globalization;
using System.Text.RegularExpressions;
using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>JP1 V12 5.2.2のel属性から表示位置を読みます。表示エラーは実行定義の採用を妨げません。</summary>
public static class DefinitionLayout
{
    /// <summary>全ての直下ユニットに明示座標があることを確認します。</summary>
    /// <param name="network">表示対象ネットです。</param>
    /// <returns>完全名に対応する水平・垂直位置です。</returns>
    public static Dictionary<string, (int X, int Y)> Read(UnitDefinition network)
    {
        var positions = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);
        var children = network.Children.ToDictionary(u => u.Name, StringComparer.Ordinal);
        foreach (string raw in network.Attributes.GetValueOrDefault("el") ?? [])
        {
            var match = Regex.Match(raw, @"^\s*([^,]+),\s*([^,]+),\s*\+([0-9]+)\s+\+([0-9]+)\s*$", RegexOptions.CultureInvariant);
            if (!match.Success || !int.TryParse(match.Groups[3].Value, CultureInfo.InvariantCulture, out int x) || !int.TryParse(match.Groups[4].Value, CultureInfo.InvariantCulture, out int y) || x > 16000 || y > 10000)
            {
                throw new InvalidDataException($"{network.Path}: elの座標形式が不正です。el=ユニット名,種別,+水平位置 +垂直位置 を指定してください。");
            }

            string name = match.Groups[1].Value.Trim();
            if (!children.TryGetValue(name, out var child) || child.Type != match.Groups[2].Value.Trim())
            {
                throw new InvalidDataException($"{network.Path}: elの対象または種別が定義と一致しません: {name}");
            }

            if (!positions.TryAdd(child.Path, (x, y)))
            {
                throw new InvalidDataException($"{child.Path}: elの座標が重複指定されています。");
            }
        }

        foreach (var child in network.Children)
        {
            if (!positions.ContainsKey(child.Path))
            {
                throw new InvalidDataException($"{child.Path}: 表示座標がありません。親ネットのel属性に座標を指定してください。");
            }
        }

        return positions;
    }
}
