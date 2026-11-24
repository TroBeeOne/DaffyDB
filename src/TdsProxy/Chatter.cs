namespace TdsProxy;

/// <summary>
/// The running commentary: one line per session, per statement, per translation. It is the most
/// useful thing about watching this tool in a terminal, and the least useful thing about running
/// it in the background behind something else, so --quiet turns exactly this off.
///
/// Startup output deliberately does not come through here. The banner carries the connection
/// string, the mount list says which files became which tables, and warnings say what was skipped
/// — none of that is chatter, and a quiet run still needs all of it.
///
/// Every line is masked on the way out (<see cref="Redact"/>): the commentary is mostly SQL, and
/// SQL is where people type connection strings.
/// </summary>
internal static class Chatter
{
    public static bool Quiet;

    public static void Line(string text)
    {
        if (!Quiet) Console.WriteLine(Redact.Text(text));
    }
}
