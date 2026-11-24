using System.Text.RegularExpressions;

namespace TdsProxy.Sql;

/// <summary>
/// What a CATCH block can see about the error it is handling, which is exactly what the ERROR_*
/// functions report. Severity and state are nominal — nothing here distinguishes one failure from
/// another the way SQL Server's own message catalogue does.
/// </summary>
internal sealed record SqlErrorInfo(
    int Number,
    string Message,
    byte Severity = 16,
    byte State = 1,
    int Line = 0,
    string? Procedure = null);

/// <summary>
/// The two statements that raise an error deliberately. Without them a CATCH block can only be
/// reached by accident, which makes TRY/CATCH awkward to demonstrate and awkward to test.
/// </summary>
internal static class SqlErrors
{
    /// <summary>The number SQL Server gives a user-raised error that names no message id.</summary>
    public const int UserErrorNumber = 50000;

    private static readonly Regex ThrowPattern = new(
        @"^THROW(\s+(?<number>\d+)\s*,\s*(?<message>.+?)\s*,\s*(?<state>\d+))?\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex RaiserrorPattern = new(
        @"^RAISERROR\s*\(\s*(?<message>.+?)\s*,\s*(?<severity>\d+)\s*,\s*(?<state>\d+)\s*(,.*)?\)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// THROW, with or without arguments. The bare form rethrows whatever the enclosing CATCH block
    /// is handling, so it reports no error of its own and leaves that to the caller.
    /// </summary>
    public static bool TryParseThrow(string statement, out SqlErrorInfo? error)
    {
        error = null;

        var match = ThrowPattern.Match(statement.Trim());
        if (!match.Success) return false;

        if (!match.Groups["number"].Success) return true;   // bare THROW: rethrow

        error = new SqlErrorInfo(
            int.Parse(match.Groups["number"].Value),
            Unquote(match.Groups["message"].Value),
            State: byte.Parse(match.Groups["state"].Value));

        return true;
    }

    /// <summary>RAISERROR('message', severity, state). The message-id form is not supported.</summary>
    public static bool TryParseRaiserror(string statement, out SqlErrorInfo? error)
    {
        error = null;

        var match = RaiserrorPattern.Match(statement.Trim());
        if (!match.Success) return false;

        string message = match.Groups["message"].Value;
        if (!message.StartsWith('\'')) return false;   // a message id, not a string

        error = new SqlErrorInfo(
            UserErrorNumber,
            Unquote(message),
            Severity: byte.Parse(match.Groups["severity"].Value),
            State: byte.Parse(match.Groups["state"].Value));

        return true;
    }

    private static string Unquote(string text)
    {
        text = text.Trim();
        if (text.StartsWith("N'", StringComparison.OrdinalIgnoreCase)) text = text[1..];

        return text.Length >= 2 && text[0] == '\'' && text[^1] == '\''
            ? text[1..^1].Replace("''", "'")
            : text;
    }
}
