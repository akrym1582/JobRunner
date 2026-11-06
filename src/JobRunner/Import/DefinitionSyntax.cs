namespace JobRunner.Import;

/// <summary>原文のUTF-16位置です。実行モデルや保存形式から独立しています。</summary>
/// <param name="start">unitの開始です。</param>
/// <param name="headerStart">ヘッダー値の開始です。</param>
/// <param name="headerEnd">ヘッダー終端です。</param>
/// <param name="bodyStart">開き括弧直後です。</param>
internal sealed class DefinitionSyntax(int start, int headerStart, int headerEnd, int bodyStart)
{
    /// <summary>Gets. ユニット開始です。</summary>
    public int Start { get; } = start;

    /// <summary>Gets. ヘッダー開始です。</summary>
    public int HeaderStart { get; } = headerStart;

    /// <summary>Gets. ヘッダー終端です。</summary>
    public int HeaderEnd { get; } = headerEnd;

    /// <summary>Gets. 本体開始です。</summary>
    public int BodyStart { get; } = bodyStart;

    /// <summary>Gets or sets. 閉じ括弧直後です。</summary>
    public int End { get; set; }

    /// <summary>Gets. 原文順の属性です。</summary>
    public List<DefinitionAttribute> Attributes { get; } = [];
}
