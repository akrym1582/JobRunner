using System.Text;
using System.Text.Json;
using JobRunner.Models;

namespace JobRunner.Controllers.Jp1;

/// <summary>HTTPの入力検証を操作と参照で共有します。</summary>
public static partial class Jp1Endpoints
{
    /// <summary>必須不足、型、バイト長、不正文字を区別します。</summary>
    /// <param name="values">JSONパラメーターです。</param>
    /// <param name="name">項目名です。</param>
    /// <param name="maximum">UTF-8の上限バイト数です。</param>
    /// <returns>検証済み文字列です。</returns>
    private static string RequiredString(JsonElement values, string name, int maximum)
    {
        if (!values.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null || (value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0))
        {
            throw new Jp1ParameterException(name, 3201, name + "を指定してください。");
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new Jp1ParameterException(name, 3206, name + "は文字列です。");
        }

        string text = value.GetString()!;
        if (Encoding.UTF8.GetByteCount(text) > maximum)
        {
            throw new Jp1ParameterException(name, 3202, name + "のバイト長が上限を超えています。");
        }

        if (text.Any(char.IsControl))
        {
            throw new Jp1ParameterException(name, 3204, name + "に制御文字は指定できません。");
        }

        return text;
    }

    /// <summary>既定値を含む列挙引数を検証します。</summary>
    /// <param name="values">パラメーターです。</param>
    /// <param name="name">項目名です。</param>
    /// <param name="fallback">省略値。nullなら必須です。</param>
    /// <param name="allowed">受理する値です。</param>
    /// <returns>検証済みの値です。</returns>
    private static string Choice(JsonElement values, string name, string? fallback, params string[] allowed)
    {
        string value = fallback is not null && (!values.TryGetProperty(name, out var raw) || raw.ValueKind == JsonValueKind.Null)
            ? fallback : RequiredString(values, name, 255);
        if (!allowed.Contains(value, StringComparer.Ordinal))
        {
            bool unsupported = (name == "unitNameMatchMethods" && value == "RE") || (name == "status" && value is "EXECDEFFER" or "SHUTDOWN" or "QUEUING" or "CONDITIONWAIT" or "MONITORING" or "INVALIDSEQ" or "MONITORCLOSE" or "UNEXECMONITOR" or "MONITORINTRPT" or "MONITORNORMAL") || (name == "status" && value.StartsWith("GRP_", StringComparison.Ordinal));
            throw new Jp1ParameterException(name, unsupported ? 0 : 3206, name + "の指定値が不正または未対応です。");
        }

        return value;
    }

    /// <summary>整数の型と範囲を区別して検証します。</summary>
    /// <param name="values">パラメーターです。</param>
    /// <param name="name">項目名です。</param>
    /// <param name="minimum">下限です。</param>
    /// <param name="maximum">上限です。</param>
    /// <returns>範囲内の整数です。</returns>
    private static int RequiredNumber(JsonElement values, string name, int minimum, int maximum)
    {
        if (!values.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            throw new Jp1ParameterException(name, 3201, name + "を指定してください。");
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
        {
            throw new Jp1ParameterException(name, 3206, name + "は整数です。");
        }

        if (number < minimum || number > maximum)
        {
            throw new Jp1ParameterException(name, 3203, name + "が範囲外です。");
        }

        return number;
    }

    /// <summary>日時解析エラーを項目付きの入力エラーへ変換します。</summary>
    /// <param name="values">パラメーターです。</param>
    /// <param name="name">項目名です。</param>
    /// <param name="options">サービス時刻です。</param>
    /// <returns>絶対時刻です。</returns>
    private static DateTimeOffset ParameterTime(JsonElement values, string name, RunnerOptions options)
    {
        string value = RequiredString(values, name, 16);
        try
        {
            return ReadLocalTime(value, options);
        }
        catch (FormatException)
        {
            throw new Jp1ParameterException(name, 3206, name + "の日時が不正です。");
        }
    }
}
