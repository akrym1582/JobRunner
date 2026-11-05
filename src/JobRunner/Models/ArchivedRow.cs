namespace JobRunner.Models;

/// <summary>削除前の行をそのまま保存し、途中で書き換わった行を削除しないようにします。</summary>
/// <param name="Key">元Tableの行キーです。</param>
/// <param name="ETag">スナップショット取得時のETagです。</param>
/// <param name="Json">保存モデルの原JSONです。</param>
public sealed record ArchivedRow(string Key, string ETag, string Json);
