namespace JobRunner.Models;

/// <summary>
/// 登録種別と補充済み範囲を固定し、再起動で解除済み登録を復活させません。
/// </summary>
public sealed class Registration
{
    /// <summary>
    /// Gets or sets. 操作または自動登録から決定的に生成する識別子です。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 採用ID。別定義の登録と状態を混ぜないために使用します。
    /// </summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. 独立に実行登録するルートジョブネットの完全名です。
    /// </summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets. Confirmed、Planned、Immediateの登録種別です。
    /// </summary>
    public string Kind { get; set; } = "Confirmed";

    /// <summary>
    /// Gets or sets. 省略された年月や相対日の解決に用いる固定業務日です。
    /// </summary>
    public DateOnly Anchor { get; set; } = default;

    /// <summary>
    /// Gets or sets. この日付未満の予定は補充済みであることを示すカーソルです。
    /// </summary>
    public DateOnly Through { get; set; } = default;

    /// <summary>
    /// Gets or sets. 明示的な登録期限。nullは自動補充を継続します。
    /// </summary>
    public DateOnly? End { get; set; } = null;

    /// <summary>
    /// Gets or sets a value indicating whether. 解除された登録を補充や再起動で復活させないための印です。
    /// </summary>
    public bool Disabled { get; set; } = false;
}
