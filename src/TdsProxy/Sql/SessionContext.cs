namespace TdsProxy.Sql;

/// <summary>
/// The facts about the caller that T-SQL exposes as functions: SUSER_SNAME(), HOST_NAME(),
/// APP_NAME(), DB_NAME(). Kept as its own small record rather than reaching for the protocol
/// layer's session type, so the translator stays ignorant of the seam above it.
/// </summary>
internal sealed record SessionContext(
    string UserName, string Database, string HostName, string ApplicationName)
{
    /// <summary>What an unattributed translation reports — a statement run outside any session.</summary>
    public static SessionContext Default { get; } = new("dbo", Databases.Data, "client", "TDS proxy client");
}
