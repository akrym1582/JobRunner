using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>大きな下書きの本文は不変Blobへ置きます。</summary>
/// <param name="Revision">競合検出用の版です。</param>
/// <param name="Path">本文の保存先です。</param>
public sealed record SharedDraftPointer(long Revision, string Path);
