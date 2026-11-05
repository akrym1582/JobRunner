namespace JobRunner.Models;

/// <summary>
/// 原文と取得時の証跡を残し、同じ定義を再検証できるようにします。
/// </summary>
public sealed class SourceEvidence
{
    /// <summary>
    /// Gets or sets. 取得時に使用した配置先・形式・文字コードです。
    /// </summary>
    public DefinitionSource Mapping { get; set; } = new();

    /// <summary>
    /// Gets or sets. 変換前バイト列のSHA-256です。
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 取得整合性の証跡。定義ハッシュには含めません。
    /// </summary>
    public string ETag { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 原文バイト列。文字コード変換による情報損失を防ぎます。
    /// </summary>
    public string Base64 { get; set; } = string.Empty;
}
