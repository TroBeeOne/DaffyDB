using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using TdsProxy.Query;
using TdsProxy.Repl;
using TdsProxy.Tds;

namespace TdsProxy;

internal static class Program
{
    private const int DefaultServePort = 1433;

    private static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine("try: daffydb --help");
            return 1;
        }

        if (options.Help) { PrintUsage(); return 0; }
        if (options.Version) { PrintVersion(); return 0; }

        // The prompt is the default, but only where there is someone to type at it. Redirected
        // output is a script or a log file, and starting a REPL in front of one would break every
        // caller that exists today. --repl forces it on anyway, which is how it gets tested.
        bool repl = options.Repl ?? (!Console.IsOutputRedirected && !Console.IsInputRedirected);

        // In REPL mode the log is buffered rather than printed, so there is nothing for --quiet to
        // suppress and plenty for :log to show. It stays meaningful for a plain listener.
        Chatter.Quiet = options.Quiet && !repl;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            if (options.Seed)
            {
                ParquetSeeder.Seed(options.DataDirectory);
                return 0;
            }

            // One-off commands about the certificate rather than ways to serve. --untrust-cert
            // does not create a certificate just to look for it.
            if (options.TrustCert)
            {
                using var toTrust = ServerCertificate.Load(options.CertificatePath, options.CertificatePassword,
                                                           options.RegenerateCert);
                return CertificateTrust.Trust(toTrust, Console.Out) ? 0 : 1;
            }

            if (options.UntrustCert)
            {
                using var current = File.Exists(ServerCertificate.ResolvePath(options.CertificatePath))
                    ? ServerCertificate.Load(options.CertificatePath, options.CertificatePassword, regenerate: false)
                    : null;
                CertificateTrust.Untrust(current, Console.Out);
                return 0;
            }

            // Encryption is on by default because every current client asks for it by default:
            // Encrypt=Mandatory is the default in Microsoft.Data.SqlClient 4.0+, mssql-jdbc 10+,
            // sqlcmd 18+ and SSMS 20+. Left off, a fresh install fails its very first connection
            // over a flag nobody knows to pass. The first run spends about 50ms generating a
            // certificate and reuses it for five years; every run after that just loads the file.
            //
            // A failure here costs encryption, not the server. A read-only profile or a confined
            // container should still serve plaintext rather than refusing to start.
            X509Certificate2? certificate = null;
            string? certificateProblem = null;

            if (options.Tls)
            {
                try
                {
                    certificate = ServerCertificate.Load(options.CertificatePath, options.CertificatePassword,
                                                         options.RegenerateCert);
                }
                catch (Exception ex)
                {
                    certificateProblem = $"{ex.GetType().Name}: {ex.Message}";
                    Console.WriteLine($"[tls]    could not provision a certificate: {certificateProblem}");
                }
            }

            int port = options.Port ?? DefaultServePort;
            bool? certificateTrusted = certificate is not null && OperatingSystem.IsWindows()
                ? CertificateTrust.IsTrusted(certificate)
                : null;

            var handler = CreateHandler(options);
            try
            {
                var startup = new StartupInfo(
                    options.DataDirectory,
                    options.InitScript,
                    options.User,
                    PasswordRequired: options.Password is not null,
                    options.AllowPassthrough,
                    AllowAdmin: options.AllowAdmin,
                    PublicCertificatePath: certificate is null
                        ? null
                        : ServerCertificate.PublicPath(options.CertificatePath),
                    CertificateProblem: certificateProblem,
                    Repl: repl,
                    CertificateTrusted: certificateTrusted,
                    // Written where the banner has a Power BI section to show it in, which is on
                    // Windows with encryption on. :pbi writes it regardless.
                    PowerBiFile: certificateTrusted is null ? null : PowerBiDesktop.Write(port));

                // For EXEC daffydb.status, which reports how the server was started.
                ServerFacts.Startup = startup;
                ServerFacts.Port = port;

                var server = new TdsServer(port, options.Trace, handler, certificate, startup);

                // Asked at the prompt, once the server is already listening, and only there: a copy
                // started where nobody can answer must not sit with its port closed waiting for one.
                Action<TextReader, TextWriter>? question = null;
                string certificatePath = ServerCertificate.ResolvePath(options.CertificatePath);
                if (certificate is { } served && CertificateTrust.ShouldOffer(served, certificatePath))
                    question = (input, output) => CertificateTrust.Offer(served, certificatePath, input, output);

                bool interactive = !Console.IsInputRedirected;
                Action<TextReader, TextWriter> powerBi = (input, output) =>
                    PowerBiDesktop.Open(port, certificate, startup, input, output, interactive);

                if (repl) await ServeWithPromptAsync(server, handler, cts, interactive, question, powerBi);
                else await server.RunAsync(cts.Token);
            }
            finally
            {
                (handler as IDisposable)?.Dispose();
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fatal: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    /// <summary>
    /// Runs the listener and a prompt together.
    ///
    /// The order matters in both directions. The prompt waits for the banner, so the two do not
    /// interleave; and Console.Out is only diverted into the log buffer once that banner is out,
    /// because what it says — the connection string, what got mounted — is the part you need to
    /// see immediately. Everything after that is chatter, and chatter belongs in :log.
    /// </summary>
    private static async Task ServeWithPromptAsync(TdsServer server, IQueryHandler handler,
                                                   CancellationTokenSource cts, bool interactive,
                                                   Action<TextReader, TextWriter>? question,
                                                   Action<TextReader, TextWriter> powerBi)
    {
        var listening = new TaskCompletionSource();
        var serving = server.RunAsync(cts.Token, () => listening.TrySetResult());

        // If the port is taken, serving faults instead of ever signalling; await both so the
        // failure surfaces as itself rather than as a prompt that never appears.
        await Task.WhenAny(listening.Task, serving);
        if (serving.IsFaulted) await serving;

        var log = new LogBuffer();
        var console = Console.Out;
        Console.SetOut(log);
        try
        {
            var loop = new ReplLoop(handler, log, console, Console.In, cts, interactive, question, powerBi);
            await loop.RunAsync();
        }
        finally
        {
            Console.SetOut(console);
        }

        // Reached by :quit, by Ctrl-C, or by the input stream ending. All three stop the listener.
        await cts.CancelAsync();
        try { await serving; } catch (OperationCanceledException) { }
    }

    /// <summary>
    /// The seam in practice: choosing an execution engine is a one-line decision, because nothing
    /// above <see cref="IQueryHandler"/> knows or cares which one is in use.
    /// </summary>
    private static IQueryHandler CreateHandler(Options options)
    {
        IQueryHandler handler = options.Handler switch
        {
            "echo" => new EchoQueryHandler(),
            "duckdb" => new DuckDbQueryHandler(options.DataDirectory, options.InitScript, options.AllowPassthrough,
                                               options.AllowAdmin),
            _ => throw new ArgumentException($"unknown handler '{options.Handler}' (expected echo or duckdb)"),
        };

        // Authentication is a wrapper, not a feature of the engine: with neither flag given every
        // login is accepted, exactly as before.
        if (options.User is null && options.Password is null) return handler;

        Console.WriteLine($"[auth]   requiring user='{options.User ?? "(any)"}'" +
                          $" password={(options.Password is null ? "(any)" : "set")}");
        return new CredentialGate(handler, options.User, options.Password);
    }

    /// <summary>
    /// Reports the DuckDB build as well as our own, because "which DuckDB is this?" is the question
    /// that actually gets asked. Loading the engine here doubles as a smoke test: a packaged tool
    /// whose native library cannot be found fails on --version rather than mid-query.
    /// </summary>
    private static void PrintVersion()
    {
        Console.WriteLine($"daffydb {StartupBanner.ToolVersion()}");
        Console.WriteLine($"duckdb  {DuckDbQueryHandler.EngineVersion()}");
        Console.WriteLine($"dotnet  {Environment.Version}");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            daffydb - query Parquet, CSV, JSON and Excel files with any SQL Server client

              daffydb
                  Serve the current directory on port 1433. Every .parquet, .csv, .json
                  and .xlsx file under it becomes a table, and any client that speaks to
                  SQL Server can query them: Management Studio, sqlcmd, ADO.NET, Power BI,
                  DBeaver.

                  You also get a prompt, for trying T-SQL without another client:
                  :tables lists what is mounted, :help lists the rest, :quit stops the
                  server. Clients stay connected and keep working while you use it. On
                  Windows, :pbi opens Power BI Desktop already pointed at the server.

              daffydb [--no-repl]
                  Just listen, no prompt. This is automatic when output is redirected,
                  so scripts and log files behave as they always have.

              daffydb [--data-dir DIR] [--port N]
                  Serve DIR instead, on another port. The file name decides where a table
                  lands: orders.parquet becomes dbo.orders, and either Sales.Customer.csv
                  or Sales/Customer.csv becomes Sales.Customer. CSV and JSON columns are
                  typed by DuckDB's sniffer; where both orders.parquet and orders.csv
                  exist, the parquet wins, and a csv wins over a json. A file that cannot
                  be read is reported and skipped.

                  Also read: .jsonl and .ndjson, and .csv.gz, .json.gz and the like.
                  The scan goes two folders deep, and passes over bin, obj,
                  node_modules, dot-folders, and appsettings, launchSettings and package
                  json files. A folder with more than 500 data files is not mounted at
                  all - it is almost certainly the wrong folder.

                  An Excel workbook (.xlsx, .xlsm) is a schema, and each sheet in it a
                  table: Budget.xlsx becomes Budget.Q1, Budget.[Summary Q1] and so on.
                  Sheets are copied into memory at startup, typed from every row, so a
                  workbook saved while daffydb runs shows its changes after a restart; the
                  file itself is never changed. A sheet is a table when its first row is
                  a header, or when that row is data, in which case the columns are
                  column_A, column_B and so on. Hidden, empty and report-shaped sheets
                  are left out.

              daffydb [--no-cert] [--cert-file FILE.pfx] [--cert-password PW] [--regen-cert]
                  Encryption is on by default. Encrypt=Mandatory (TDS 7.4) and
                  Encrypt=Strict (TDS 8.0) both work on the same port as plaintext,
                  because the first byte of a connection says which one it is. A
                  self-signed certificate is generated on first run, kept per-user, and
                  reused for five years; the .cer beside it is what a Strict client is
                  pointed at. Nothing external is needed, and if the certificate cannot
                  be written the server says so and serves plaintext anyway.
                  --no-cert skips all of that and refuses encryption outright.

              daffydb --trust-cert | --untrust-cert
                  Windows only. Add the certificate to your own Trusted Root store, or
                  take every DaffyDB certificate back out. Needed by clients that check
                  the certificate and cannot be told to trust it - Power BI's SQL Server
                  connector is one. Windows asks you to confirm either way. The first
                  interactive prompt offers to do this; a no is remembered.

              daffydb [--user NAME] [--password VALUE]
                  Require these credentials. Either may be given alone; with neither, any
                  login is accepted. The protocol obfuscates the password rather than
                  encrypting it, so it is only really protected on an encrypted
                  connection - which is the default now, but not under --no-cert.

              daffydb [--init FILE.sql]
                  Run a DuckDB SQL script at startup, after the data files are mounted.
                  DuckDB's own dialect, not T-SQL: INSTALL httpfs, CREATE SECRET, ATTACH,
                  CREATE VIEW over anything DuckDB can read. A failure stops the server.

              daffydb [--allow-passthrough]
                  Let clients run DuckDB SQL directly with EXEC sp_duckdb N'...'.
                  Off by default: it is arbitrary DuckDB execution, including file and
                  network reads, available to anyone who can connect.

              daffydb [--allow-admin]
                  Let clients add to what is served while it runs, and take it away
                  again, all of it read-only: EXEC daffydb.mount 'D:\exports' mounts
                  another folder or file by the same rules as --data-dir, and
                  daffydb.postgres, daffydb.mysql, daffydb.sqlserver, daffydb.sqlite
                  and daffydb.duckdb mount a database's tables as views, read live.
                  EXEC daffydb.unmount takes a mount back out. EXEC daffydb.save_parquet
                  writes a table to a parquet file, and daffydb.save_parquet_all writes
                  every one, never replacing a file that is there. EXEC daffydb.run_sqlserver
                  runs T-SQL on a mounted SQL Server, as far as its login may. Off by
                  default, because it reads and writes whatever the account running
                  daffydb can reach, for anyone who can connect.
                  daffydb's own prompt can always run them. EXEC daffydb.help,
                  daffydb.status and daffydb.tables need no flag. The postgres, mysql
                  and sqlite commands download DuckDB's extension for each the first
                  time one is used; nothing is downloaded until then. sqlserver needs
                  nothing downloaded.

              daffydb --seed [--data-dir DIR]
                  Write sample customers/orders parquet files to play with.

              daffydb [--quiet] [--trace]
                  --quiet logs startup only, not each query. --trace prints every decoded
                  TDS message, which is how you find out what a client really sent.

              daffydb --version | --help
            """);
    }

    private sealed record Options(
        bool Seed, int? Port, bool Trace, bool Quiet, string Handler, string DataDirectory,
        string? User, string? Password, string? InitScript, bool AllowPassthrough, bool AllowAdmin,
        bool Tls, string? CertificatePath, string? CertificatePassword, bool RegenerateCert,
        bool TrustCert, bool UntrustCert, bool Help, bool Version, bool? Repl)
    {
        public static Options? Parse(string[] args)
        {
            bool seed = false, trace = false, quiet = false, allowPassthrough = false, allowAdmin = false;
            bool regenerateCert = false, trustCert = false, untrustCert = false, help = false, version = false;

            // Encryption is opt-out rather than opt-in. --no-cert wins wherever it appears, so
            // ordering it against the other certificate flags never changes the answer.
            bool noCert = false;

            // Null means decide from the console; the flags are only for overriding that.
            bool? repl = null;
            string? certificatePath = null, certificatePassword = null;
            int? port = null;
            string handler = "duckdb";
            string dataDirectory = ".";
            string? user = null, password = null, initScript = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--seed": seed = true; break;
                    case "--trace": trace = true; break;
                    case "--quiet" or "-q": quiet = true; break;
                    case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
                    case "--handler" when i + 1 < args.Length: handler = args[++i].ToLowerInvariant(); break;
                    case "--data-dir" when i + 1 < args.Length: dataDirectory = args[++i]; break;
                    case "--user" when i + 1 < args.Length: user = args[++i]; break;
                    case "--password" when i + 1 < args.Length: password = args[++i]; break;
                    case "--init" when i + 1 < args.Length: initScript = args[++i]; break;
                    case "--allow-passthrough": allowPassthrough = true; break;
                    case "--allow-admin": allowAdmin = true; break;
                    // --cert is what switched encryption on before it became the default. It is
                    // still accepted, and still does exactly what it says; there is just nothing
                    // left for it to turn on.
                    case "--cert": break;
                    case "--no-cert": noCert = true; break;
                    case "--cert-file" when i + 1 < args.Length: certificatePath = args[++i]; break;
                    case "--cert-password" when i + 1 < args.Length: certificatePassword = args[++i]; break;
                    case "--regen-cert": regenerateCert = true; break;
                    case "--trust-cert": trustCert = true; break;
                    case "--untrust-cert": untrustCert = true; break;
                    case "--repl": repl = true; break;
                    case "--no-repl": repl = false; break;
                    case "--version" or "-v": version = true; break;
                    case "--help" or "-h": help = true; break;
                    default: Console.Error.WriteLine($"unknown argument: {args[i]}"); return null;
                }
            }

            return new Options(seed, port, trace, quiet, handler, dataDirectory, user, password,
                               initScript, allowPassthrough, allowAdmin, !noCert, certificatePath, certificatePassword,
                               regenerateCert, trustCert, untrustCert, help, version, repl);
        }
    }
}
