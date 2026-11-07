namespace JobRunner.Controllers.Ui;

/// <summary>グループ単位の下書き入出力です。</summary>
/// <param name="Draft">現在の下書きです。</param>
/// <param name="Path">出力対象、または置換対象の完全名です。</param>
/// <param name="ZipBase64">取り込むZIPです。出力時は省略します。</param>
/// <param name="ParentPath">新規追加先のグループです。</param>
/// <param name="Name">追加時のグループ名です。</param>
public sealed record EditorGroupPackageRequest(EditorDraft Draft, string Path, string? ZipBase64 = null, string? ParentPath = null, string? Name = null);
