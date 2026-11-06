namespace JobRunner.Controllers.Jp1;

/// <summary>公式に対応する入力違反だけへメッセージIDを割り当てます。</summary>
/// <param name="parameter">違反した項目です。</param>
/// <param name="number">確認済みKNAK番号です。</param>
/// <param name="message">違反理由です。</param>
internal sealed class Jp1ParameterException(string parameter, int number, string message) : FormatException(message)
{
    /// <summary>Gets. 違反した項目です。</summary>
    public string Parameter { get; } = parameter;

    /// <summary>Gets. 発生条件に対応した公式メッセージIDです。</summary>
    public string MessageId { get; } = number == 0 ? "JOBRUNNER_UNSUPPORTED_PARAMETER" : $"KNAK{number}-E";
}
