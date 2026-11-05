namespace JobRunner.Models;

/// <summary>
/// 配置後のユニットと原文の属性を保持し、診断とスナップショット実行に使用します。
/// </summary>
public sealed class UnitDefinition
{
    /// <summary>
    /// Gets or sets. 配置後の完全名。世代の定義参照にも使用します。
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 原文のユニット名。Unicodeと大文字小文字を保持します。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. JP1のユニット種別。グループ、ネット、ローカルジョブを区別します。
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 原文上の開始行。取り込みエラーの位置表示に使います。
    /// </summary>
    public int Line { get; set; } = 0;

    /// <summary>
    /// Gets or sets. 原文の入力Blobパス。配置元の追跡に使用します。
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 繰り返し属性と未知属性を失わず保持します。
    /// </summary>
    public Dictionary<string, List<string>> Attributes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets. 原文で定義された下位ユニットです。
    /// </summary>
    public List<UnitDefinition> Children { get; set; } = [];
}
