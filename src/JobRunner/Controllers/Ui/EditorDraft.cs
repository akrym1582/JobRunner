namespace JobRunner.Controllers.Ui;

/// <summary>保存済み版を基にした下書きです。</summary>
/// <param name="DefinitionHash">原文を固定する版です。</param>
/// <param name="Networks">全ネットの編集投影です。</param>
public sealed record EditorDraft(string DefinitionHash, List<EditorNetwork> Networks);
