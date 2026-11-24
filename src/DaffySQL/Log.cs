namespace DaffySQL;

/// <summary>
/// DaffySQL's diagnostics: logins, redirects, sessions opened and returned. A library writes no
/// console output of its own, so lines go to <see cref="Sink"/> when something has set one - the
/// console app does - and nowhere otherwise. Every line has a stable code beside its text.
///
/// Passwords, secrets and connection strings never go through here.
/// </summary>
internal static class Log
{
    public static Action<string, string>? Sink;

    public static void Debug(string code, string message)
    {
        try { Sink?.Invoke(code, message); }
        catch { /* diagnostics must never be the thing that breaks a query */ }
    }
}
