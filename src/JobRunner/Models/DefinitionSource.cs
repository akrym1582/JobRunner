namespace JobRunner.Models;

/// <summary>
/// 入力ファイルの配置先と文字コードを指定し、自動推定による誤読を防ぎます。
/// </summary>
public sealed class DefinitionSource
{
    /// <summary>
    /// Gets or sets. 解析アダプターの種別。未対応形式は取り込みを拒否します。
    /// </summary>
    public string Kind { get; set; } = "Unit";

    /// <summary>
    /// Gets or sets. 最上位ユニットを配置する親グループのJP1論理パスです。
    /// </summary>
    public string TargetPath { get; set; } = "/";

    /// <summary>
    /// Gets or sets. コンテナー名とBlob名を含む大文字小文字を維持する入力パスです。
    /// </summary>
    public string BlobPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 入力の文字コード名。変換不能なバイトはエラーにします。
    /// </summary>
    public string Encoding { get; set; } = "utf-8";
}
