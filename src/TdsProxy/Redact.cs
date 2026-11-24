using System.Text.RegularExpressions;

namespace TdsProxy;

/// <summary>
/// Masks credentials in text on its way to the log, a trace or a client's error message.
///
/// The text in question is SQL, and SQL is where credentials get typed: a connection string handed
/// to EXEC daffydb.postgres, an ATTACH in an --init script, a CREATE SECRET sent through sp_duckdb.
/// Each is logged as it runs, and a failure can echo it back. So everything bound for one of those
/// places passes through here first. It is pattern matching, not parsing, and errs towards masking:
/// a log line that hides a little too much costs nothing, and one that shows a password cannot be
/// taken back.
/// </summary>
internal static class Redact
{
    private const string Mask = "***";

    /// <summary>
    /// key=value, as libpq, MySQL and ADO.NET connection strings write it. The value may be quoted:
    /// with doubled quotes inside a T-SQL string literal (password=''p w''), with single quotes once
    /// it has been taken out of one, or with double quotes, and T-SQL's N in front of a quote counts:
    /// WITH PASSWORD = N'p', in what daffydb.run_sqlserver runs. Otherwise it runs to the next space,
    /// semicolon or quote. <c>sig</c> is a SAS token's signature in a URL, and what follows it in
    /// the query string goes too: more than needed, which is the right way to be wrong.
    /// </summary>
    private static readonly Regex KeyValue = new(
        @"(?<key>\b(?:password|pwd|passwd|secret|client_secret|account_key|accountkey|sharedaccesssignature|sas_token|session_token|sig)\s*=\s*)" +
        @"(?<value>N?''(?:''''|(?!'').)*''|N?'(?:[^']|'')*'|""[^""]*""|[^\s;'""]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A DuckDB CREATE SECRET option: PASSWORD 'x', or PASSWORD ''x'' inside a T-SQL literal.</summary>
    private static readonly Regex SecretOption = new(
        @"(?<key>\b(?:password|secret|client_secret|session_token|connection_string|account_key)\s+)" +
        @"(?<value>''(?:(?!'').)*''|'(?:[^']|'')*')",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The password in a URL: postgresql://user:password@host/db.</summary>
    private static readonly Regex UrlPassword = new(
        @"(?<key>://[^/\s:@'""]+:)(?<value>[^@\s/'""]+)(?=@)",
        RegexOptions.CultureInvariant);

    public static string Text(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        text = KeyValue.Replace(text, m => m.Groups["key"].Value + Quoted(m.Groups["value"].Value));
        text = SecretOption.Replace(text, m => m.Groups["key"].Value + Quoted(m.Groups["value"].Value));
        return UrlPassword.Replace(text, m => m.Groups["key"].Value + Mask);
    }

    /// <summary>Keeps the quotes a value came in, so the masked text still reads as the statement did.</summary>
    private static string Quoted(string value) => value switch
    {
        ['\'', '\'', ..] => "''" + Mask + "''",
        ['\'', ..] => "'" + Mask + "'",
        ['"', ..] => "\"" + Mask + "\"",
        _ => Mask,
    };
}
