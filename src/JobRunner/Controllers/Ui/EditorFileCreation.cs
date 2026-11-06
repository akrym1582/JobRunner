namespace JobRunner.Controllers.Ui;

/// <summary>ローカル下書きにだけ追加するUnitファイルです。</summary>
/// <param name="FileName">安全な相対ファイル名です。</param>
/// <param name="TargetPath">絶対論理配置先です。</param>
/// <param name="Encoding">明示した文字コードです。</param>
/// <param name="Bom">BOMを付けるかどうかです。</param>
/// <param name="GroupName">最初の空グループ名です。</param>
public sealed record EditorFileCreation(string FileName, string TargetPath, string Encoding, bool Bom, string GroupName);
