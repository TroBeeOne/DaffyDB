namespace TdsProxy.Query;

/// <summary>
/// Turns away logins that do not match a fixed user name and password, then defers to the handler
/// underneath for everything else.
///
/// This exists as much to demonstrate the login hook as to be useful: it is the smallest complete
/// example of a handler making a decision about a caller, and it lives beside the DuckDB handler
/// rather than inside it because authentication has nothing to do with executing queries. Anything
/// richer — a user table, per-user storage credentials, an external identity provider — replaces
/// this class without touching the protocol.
///
/// Note what it is not: the password arrives obfuscated rather than encrypted, so on a plaintext
/// connection this keeps out the careless, not the determined. Pair it with <c>--cert</c>.
/// </summary>
internal sealed class CredentialGate(IQueryHandler inner, string? user, string? password)
    : IQueryHandler, IDisposable
{
    public Task<LoginVerdict> OnLoginAsync(SqlSession session, CancellationToken cancellationToken)
    {
        bool userOk = user is null || string.Equals(session.UserName, user, StringComparison.OrdinalIgnoreCase);

        // Ordinal, and not short-circuited against the user check, because a password comparison
        // should not be the thing that tells an attacker the user name was right.
        bool passwordOk = password is null || string.Equals(session.Password, password, StringComparison.Ordinal);

        if (!userOk || !passwordOk)
        {
            Chatter.Line($"[auth]   rejected: {session}");
            return Task.FromResult(LoginVerdict.Reject($"Login failed for user '{session.UserName}'."));
        }

        Chatter.Line($"[auth]   accepted: {session}");
        return inner.OnLoginAsync(session, cancellationToken);
    }

    public Task<QueryResult> ExecuteAsync(SqlRequest request, CancellationToken cancellationToken) =>
        inner.ExecuteAsync(request, cancellationToken);

    public void Dispose() => (inner as IDisposable)?.Dispose();
}
