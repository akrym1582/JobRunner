namespace JobRunner.Controllers.Ui;

/// <summary>採用中の定義を下書きへ読み出します。</summary>
/// <param name="Draft">編集対象です。</param>
/// <param name="Sources">原文の確認用です。</param>
/// <param name="Containers">新規ルートを追加できる位置です。</param>
public sealed record EditorDocument(EditorDraft Draft, List<EditorSource> Sources, List<EditorContainer>? Containers = null);
