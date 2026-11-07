using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>ブラウザーの自動保存と区別する共有下書きです。</summary>
/// <param name="Revision">Storage保存版です。</param>
/// <param name="BaseRevision">編集の基点にした運用版です。</param>
/// <param name="Base">原文を保持する編集の基点です。</param>
/// <param name="Draft">意味エラーを許容する編集中の内容です。</param>
public sealed record SharedEditorDraft(long Revision, long BaseRevision, EditorDocument Base, EditorDraft Draft);
