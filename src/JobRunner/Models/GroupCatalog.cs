namespace JobRunner.Models;

/// <summary>実行世代が固定するグループ所属です。原文やエクスポートには含めません。</summary>
public sealed class GroupCatalog
{
    /// <summary>Gets or sets. 保存形式です。</summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>Gets or sets. 同じパスを別の採用領域で共有しません。</summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>Gets or sets. 完全名から固定IDへの対応です。</summary>
    public Dictionary<string, string> Groups { get; set; } = new(StringComparer.Ordinal);
}
