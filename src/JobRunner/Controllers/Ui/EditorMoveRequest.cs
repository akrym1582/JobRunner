namespace JobRunner.Controllers.Ui;

/// <summary>グループまたは通常ルートの移動候補です。</summary>
/// <param name="Draft">現在の下書きです。</param>
/// <param name="Path">現在の完全名です。</param>
/// <param name="ParentPath">移動先グループです。</param>
/// <param name="SourceIndex">移動先の原文ファイルです。</param>
/// <param name="FileName">並替えに依存しない原文名です。</param>
/// <param name="FileTargetPath">ファイルの配置先です。</param>
/// <param name="FileEncoding">文字コードです。</param>
public sealed record EditorMoveRequest(EditorDraft Draft, string Path, string ParentPath, int SourceIndex, string? FileName = null, string? FileTargetPath = null, string? FileEncoding = null);
