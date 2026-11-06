namespace JobRunner.Import;

/// <summary>繰り返し属性も個別の構文位置として保持します。</summary>
/// <param name="Key">属性名です。</param>
/// <param name="Value">復号した値です。</param>
/// <param name="Start">属性開始です。</param>
/// <param name="ValueStart">等号直後です。</param>
/// <param name="End">セミコロン直後です。</param>
internal sealed record DefinitionAttribute(string Key, string Value, int Start, int ValueStart, int End);
