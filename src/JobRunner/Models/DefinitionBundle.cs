namespace JobRunner.Models;

/// <summary>
/// 全入力を一つの不変版として保存し、部分的な採用を防ぎます。
/// </summary>
public sealed class DefinitionBundle
{
    /// <summary>
    /// Gets or sets. 永続形式の版。不明な版は読み替えず停止します。
    /// </summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// Gets or sets. 配置情報と原文ハッシュから求めるSHA-256です。
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 入力バイト列と取得証跡を保存するマニフェストです。
    /// </summary>
    public List<SourceEvidence> Sources { get; set; } = [];

    /// <summary>
    /// Gets or sets. 全ファイルを統合した最上位ユニット一覧です。
    /// </summary>
    public List<UnitDefinition> Units { get; set; } = [];
}
