namespace JobRunner.Controllers.Ui;

/// <summary>保存済みのログ片です。</summary>
/// <param name="Sequence">ストリーム内の連番です。</param>
/// <param name="Text">端末制御として解釈しない文字列です。</param>
public sealed record LogChunk(int Sequence, string Text);
