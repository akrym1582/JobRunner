namespace JobRunner.Controllers.Ui;

/// <summary>原文確認用の読取専用ファイルです。</summary>
/// <param name="FileName">元のBlobパスです。</param>
/// <param name="Encoding">保存文字コードです。</param>
/// <param name="TargetPath">配置先です。</param>
/// <param name="Text">復号した原文です。</param>
/// <param name="Changed">元のバイト列から変更されたかです。</param>
/// <param name="Kind">入力形式です。旧下書きの省略値は、従来対応していたUnitとして読みます。</param>
public sealed record EditorSource(string FileName, string Encoding, string TargetPath, string Text, bool Changed, string Kind = "Unit");
