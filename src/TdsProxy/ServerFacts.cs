using System.Diagnostics;

namespace TdsProxy;

/// <summary>
/// What EXEC daffydb.status says about the server as a whole, as opposed to what it serves: how it
/// was started, and how many clients are connected. Program sets the first once the listener's
/// settings are known; the listener keeps the count.
/// </summary>
internal static class ServerFacts
{
    private static int _sessions;

    public static StartupInfo? Startup { get; set; }

    public static int Port { get; set; }

    public static DateTime StartedAt { get; } = Process.GetCurrentProcess().StartTime;

    public static int Sessions => Volatile.Read(ref _sessions);

    public static void SessionOpened() => Interlocked.Increment(ref _sessions);

    public static void SessionClosed() => Interlocked.Decrement(ref _sessions);
}
