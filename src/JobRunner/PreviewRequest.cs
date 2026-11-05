namespace JobRunner;

/// <summary>保存を伴わない予定照会の期間と登録基準日を明示します。</summary>
/// <param name="Version">保存済み定義のハッシュです。</param>
/// <param name="UnitPath">対象ルートネットの完全名です。</param>
/// <param name="Anchor">省略年月を解決する登録基準日です。</param>
/// <param name="From">照会する業務日の下限です。</param>
/// <param name="Until">照会する業務日の排他的上限です。</param>
public sealed record PreviewRequest(string Version, string UnitPath, DateOnly Anchor, DateOnly From, DateOnly Until);
