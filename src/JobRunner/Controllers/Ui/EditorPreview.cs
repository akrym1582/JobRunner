namespace JobRunner.Controllers.Ui;

/// <summary>原文差分と実行・表示の診断を分けて返します。</summary>
/// <param name="Valid">実行定義として出力可能かです。</param>
/// <param name="Errors">出力を妨げる診断です。</param>
/// <param name="LayoutErrors">図の表示を妨げる診断です。</param>
/// <param name="Sources">変更後の原文です。</param>
public sealed record EditorPreview(bool Valid, List<string> Errors, List<string> LayoutErrors, List<EditorSource> Sources);
