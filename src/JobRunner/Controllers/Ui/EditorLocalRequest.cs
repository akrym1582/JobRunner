using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>保存せず検証するローカル入力です。ファイルとZIPは排他です。</summary>
/// <param name="Sources">文字コードと配置先を明示した原文です。</param>
/// <param name="ZipBase64">JobRunner出力ZIPのバイト列です。</param>
public sealed record EditorLocalRequest(List<SourceEvidence>? Sources = null, string? ZipBase64 = null);
