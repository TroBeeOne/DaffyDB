using System.Reflection;

namespace TdsProxy;

/// <summary>
/// What the banner needs to know about how the proxy was started. Purely for display — nothing
/// here changes behaviour.
/// </summary>
internal sealed record StartupInfo(
    string DataDirectory,
    string? InitScript,
    string? User,
    bool PasswordRequired,
    bool AllowPassthrough,
    bool AllowAdmin,
    string? PublicCertificatePath,
    string? CertificateProblem = null,
    bool Repl = false,
    bool? CertificateTrusted = null,
    string? PowerBiFile = null);

/// <summary>
/// Prints the connect-with instructions once the listener is up.
///
/// It is longer than a startup line usually deserves, deliberately: the flags each change what a
/// client has to send, and the alternative is reading the README with one hand while editing a
/// connection string with the other. Whatever is switched on gets a connection string; whatever is
/// not gets the flag that would switch it on.
///
/// Every string printed here is one that will actually connect as the server currently stands.
/// That is the rule the layout follows: encryption is the default, so the encrypted strings lead
/// and the plaintext one is not shown at all - until --no-cert or a failed certificate makes
/// plaintext the only thing left, at which point it is the only one shown.
/// </summary>
internal static class StartupBanner
{
    public static void Print(int port, StartupInfo info)
    {
        Console.WriteLine();
        PrintHeader(port);
        PrintConnect(port, info);
        PrintWhatIsServed(info);
        PrintHints(info);

        // The plain listener promises a running commentary below. At a prompt that would fight
        // the cursor, so the log is buffered instead and the banner says where it went.
        Console.WriteLine(info.Repl
            ? "  Ctrl-C or :quit to stop. Log lines are held while you type; :log shows them."
            : "  Ctrl-C to stop. Every batch is logged below with the DuckDB it became.");
        Console.WriteLine();
    }

    /// <summary>
    /// The tool's own version, as the package declares it. SourceLink appends +&lt;commit&gt; to the
    /// informational version, which is not something anyone wants to read off a banner.
    /// </summary>
    public static string ToolVersion()
    {
        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        int plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    /// <summary>
    /// Two lines on what this is, because the second one is the expectation that most needs
    /// setting: the wire protocol is SQL Server's, the engine underneath it is not.
    /// </summary>
    private static void PrintHeader(int port)
    {
        Console.WriteLine($"  DaffyDB {ToolVersion()} - query parquet, CSV, JSON and Excel files with any SQL Server client.");
        Console.WriteLine("  The engine is DuckDB, not SQL Server; shape anything custom with a view.");
        Console.WriteLine();
        Console.WriteLine($"  Listening on 127.0.0.1:{port}");
        Console.WriteLine();
    }

    /// <summary>
    /// The strings to copy. Each is labelled with the clients it is for, because the difference
    /// between them is which client is asking rather than which one is better.
    /// </summary>
    private static void PrintConnect(int port, StartupInfo info)
    {
        string endpoint = $"tcp:127.0.0.1,{port}";
        string user = info.User ?? "any";

        // The password is never echoed: this output is often redirected to a file.
        string password = info.PasswordRequired ? "<the --password value>" : "any";
        string tail = $"Pooling=False;User Id={user};Password={password}";

        Console.WriteLine(info.User is null && !info.PasswordRequired
            ? "  Connect with SQL Server authentication. Any username and password are accepted."
            : "  Connect with SQL Server authentication. These credentials are REQUIRED.");
        Console.WriteLine();

        if (info.PublicCertificatePath is null)
        {
            if (info.CertificateProblem is not null)
                Console.WriteLine($"    Encryption is UNAVAILABLE: {info.CertificateProblem}");
            else
                Console.WriteLine("    Encryption is OFF (--no-cert).");

            Console.WriteLine("    Plaintext is all that will connect:");
            Console.WriteLine();
            Console.WriteLine($"      Server={endpoint};Encrypt=False;TrustServerCertificate=True;{tail}");
            Console.WriteLine();
            Console.WriteLine($"      jdbc:sqlserver://127.0.0.1:{port};encrypt=false;user={user};password={password}");
            Console.WriteLine();
            return;
        }

        Console.WriteLine("    ADO.NET, SSMS, sqlcmd");
        Console.WriteLine($"      Server={endpoint};Encrypt=Mandatory;TrustServerCertificate=True;{tail}");
        Console.WriteLine();
        Console.WriteLine("    ADO.NET with Encrypt=Strict, which needs the certificate file");
        Console.WriteLine($"      Server={endpoint};Encrypt=Strict;ServerCertificate={info.PublicCertificatePath};{tail}");
        Console.WriteLine();
        Console.WriteLine("    JDBC, DBeaver");
        Console.WriteLine($"      jdbc:sqlserver://127.0.0.1:{port};encrypt=true;trustServerCertificate=true;" +
                          $"user={user};password={password}");
        Console.WriteLine();

        // Known only on Windows, which is also the only place Power BI runs. A client that checks
        // the certificate cannot use the strings above until Windows trusts it, so say which.
        if (info.CertificateTrusted is { } trusted)
        {
            Console.WriteLine("    Power BI's SQL Server connector, and other clients that check the certificate");
            Console.WriteLine(trusted
                ? $"      Server: 127.0.0.1,{port}   (the certificate is trusted on this machine)"
                : $"      Server: 127.0.0.1,{port}   once the certificate is trusted: daffydb --trust-cert");

            // The shortcut Power BI Desktop opens with this server already filled in.
            if (info.PowerBiFile is { } file)
                Console.WriteLine(info.Repl
                    ? $"      Or open Power BI Desktop on it: type :pbi, or open {file}"
                    : $"      Or open Power BI Desktop on it: {file}");
            Console.WriteLine();
        }
    }

    private static void PrintWhatIsServed(StartupInfo info)
    {
        Console.WriteLine($"  Serving data from    : {Path.GetFullPath(info.DataDirectory)}   (.parquet, .csv, .json and .xlsx)");

        if (info.InitScript is not null)
            Console.WriteLine($"  Startup script       : {Path.GetFullPath(info.InitScript)}");

        if (info.AllowPassthrough)
            Console.WriteLine("  Pass-through         : ON - clients may run EXEC sp_duckdb N'...'");

        if (info.AllowAdmin)
        {
            Console.WriteLine("  Admin commands       : ON - clients may mount folders, files and databases, save tables, and run T-SQL on a mounted SQL Server");
            Console.WriteLine("                         as parquet files, with EXEC daffydb.*");

            // Loopback only, but any program on this machine is still anyone.
            if (info.User is null && !info.PasswordRequired)
                Console.WriteLine("                         for any login; --user and --password limit who that is");
        }

        Console.WriteLine("  Commands             : EXEC daffydb.help lists what DaffyDB answers itself");
        Console.WriteLine();
    }

    /// <summary>Only mentions the flags that are not already in use, so the list shrinks as you use them.</summary>
    private static void PrintHints(StartupInfo info)
    {
        var hints = new List<(string Flag, string Description)>();

        hints.Add(("--data-dir PATH",
                   "serve a different folder of .parquet, .csv, .json and .xlsx files"));

        if (info.InitScript is null)
            hints.Add(("--init FILE.sql",
                       "run DuckDB SQL at startup: INSTALL httpfs, CREATE SECRET, ATTACH, CREATE VIEW"));

        if (info.PublicCertificatePath is not null)
            hints.Add(("--no-cert", "refuse encrypted connections and serve plaintext only"));

        if (info.User is null && !info.PasswordRequired)
            hints.Add(("--user NAME --password VALUE", "require these credentials to log in"));

        if (!info.AllowPassthrough)
            hints.Add(("--allow-passthrough", "let clients send DuckDB SQL with EXEC sp_duckdb N'...'"));

        if (!info.AllowAdmin)
            hints.Add(("--allow-admin", "let clients mount folders, files and databases, and save parquet, with EXEC daffydb.*"));

        if (info.Repl) hints.Add(("--no-repl", "just listen, no prompt"));
        else hints.Add(("--quiet", "log startup only, not each query"));

        hints.Add(("--trace", "print every decoded TDS message"));

        Console.WriteLine("  Restart with any of these to change what is served:");
        Console.WriteLine();

        int width = hints.Max(h => h.Flag.Length);
        foreach (var (flag, description) in hints)
            Console.WriteLine($"    {flag.PadRight(width)}   {description}");

        if (hints.Count >= 4)
        {
            // The example uses real paths, so they have to be paths for the machine it printed on.
            // A drive letter on a Linux banner reads like the tool does not know where it is.
            (string data, string init) = OperatingSystem.IsWindows()
                ? (@"C:\data\exports", @"C:\temp\startup.sql")
                : ("/srv/exports", "/tmp/startup.sql");

            Console.WriteLine();
            Console.WriteLine("    Combine freely, e.g.");
            Console.WriteLine($"      daffydb --data-dir {data} --init {init} --trace");
        }

        Console.WriteLine();
    }
}
