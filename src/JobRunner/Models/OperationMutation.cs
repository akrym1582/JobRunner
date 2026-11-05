namespace JobRunner.Models;

/// <summary>ネット操作の更新内容を、Table更新より先に不変保存する再開用の計画です。</summary>
public sealed class OperationMutation
{
    /// <summary>Gets or sets. 再開処理が認識する保存形式です。</summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>Gets or sets. 別操作の計画を誤適用しないための要求IDです。</summary>
    public string OperationId { get; set; } = string.Empty;

    /// <summary>Gets or sets. 元のETagと更新後のJSONを持つ行です。</summary>
    public List<ArchivedRow> Changes { get; set; } = [];

    /// <summary>Gets or sets. このプロセスが所有している場合だけ停止要求を出す世代IDです。</summary>
    public List<string> StopIds { get; set; } = [];
}
