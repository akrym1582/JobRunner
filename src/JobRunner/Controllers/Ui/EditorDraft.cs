namespace JobRunner.Controllers.Ui;

/// <summary>保存済み版を基にした下書きです。</summary>
/// <param name="DefinitionHash">原文を固定する版です。</param>
/// <param name="Networks">全ネットの編集投影です。</param>
/// <param name="EditorVersion">投影の版です。未指定は判定属性を持たない旧下書きです。</param>
public sealed record EditorDraft(string DefinitionHash, List<EditorNetwork> Networks, int? EditorVersion = null);
