using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using TdsProxy.Sql;
using TdsProxy.Tds;

namespace TdsProxy;

/// <summary>
/// Power BI Desktop, opened already pointed at this server.
///
/// A .pbids file is Power BI's own shortcut to a data source: opening one starts Power BI Desktop
/// with the connection filled in, so the first thing on screen is the navigator rather than the
/// Get Data dialog. DaffyDB writes one at startup for the port it is serving, so the banner can say
/// where it is, and :pbi writes it again and opens it.
///
/// The file holds the address, the database and Import mode, and no credentials. Power BI asks
/// for those itself and keeps them, and a file anyone can double-click is no place for a password.
/// </summary>
internal static class PowerBiDesktop
{
    private const int DefaultPort = 1433;

    /// <summary>
    /// Per user, beside the certificate folder. The default port gets the plain name and any other
    /// port its own file, so two copies serving different ports do not overwrite each other's.
    /// </summary>
    public static string PathFor(int port) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                                  Environment.SpecialFolderOption.Create),
        "DaffyDB",
        port == DefaultPort ? "daffydb.pbids" : $"daffydb-{port}.pbids");

    /// <summary>
    /// Writes the file for this port and returns where, or null when it could not be written, which
    /// costs the shortcut and nothing else. Power BI Desktop is Windows-only, so elsewhere there is
    /// nothing to write.
    /// </summary>
    public static string? Write(int port, TextWriter? problems = null)
    {
        if (!OperatingSystem.IsWindows()) return null;

        string path = PathFor(port);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Contents(port));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems?.WriteLine($"  could not write {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Power BI's connection-file format, version 0.1, in which "tds" is SQL Server's protocol. The
    /// address is 127.0.0.1 because Power BI's connector does not reach DaffyDB through localhost,
    /// and a port other than 1433 follows it after a comma, as in any SQL Server address.
    /// </summary>
    private static string Contents(int port)
    {
        string server = port == DefaultPort ? "127.0.0.1" : $"127.0.0.1,{port}";

        return $$"""
            {
              "version": "0.1",
              "connections": [
                {
                  "details": {
                    "protocol": "tds",
                    "address": {
                      "server": "{{server}}",
                      "database": "{{Databases.Data}}"
                    },
                    "authentication": null,
                    "query": null
                  },
                  "options": {},
                  "mode": "Import"
                }
              ]
            }

            """;
    }

    /// <summary>
    /// :pbi. Writes the file afresh and opens it with whatever Windows has registered for .pbids,
    /// which is Power BI Desktop once it is installed.
    ///
    /// Before that, the one thing that would make it fail on arrival: Power BI's connector checks
    /// the certificate, so an untrusted one is raised first, with the offer to trust it. That is
    /// asked every time, unlike the startup question, because typing :pbi is asking for exactly the
    /// thing it is needed for.
    /// </summary>
    public static void Open(int port, X509Certificate2? certificate, StartupInfo info,
                            TextReader input, TextWriter output, bool interactive)
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("  Power BI Desktop runs only on Windows.");
            return;
        }

        string? path = Write(port, output);
        if (path is null) return;

        if (certificate is null)
        {
            output.WriteLine("  Encryption is off, so Power BI will offer to connect without it; that is the only way in.");
        }
        else if (!CertificateTrust.IsTrusted(certificate) && !OfferTrust(certificate, input, output, interactive))
        {
            output.WriteLine("  Not opened: Power BI would refuse the certificate. 'daffydb --trust-cert' trusts it,");
            output.WriteLine("  and :pbi works once it is.");
            return;
        }

        output.WriteLine($"  Power BI asks for credentials the first time: choose Database, then {Credentials(info)}.");
        output.WriteLine($"  opening {path}");

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception ex)
        {
            output.WriteLine($"  could not open it: {ex.Message}");
            output.WriteLine("  Is Power BI Desktop installed? The file is ready for it at the path above.");
        }
    }

    private static bool OfferTrust(X509Certificate2 certificate, TextReader input, TextWriter output, bool interactive)
    {
        output.WriteLine("  Power BI checks DaffyDB's certificate, and Windows does not trust it yet.");
        if (!interactive) return false;

        output.Write("  Trust it now? Windows will ask you to confirm. [y/N] ");
        output.Flush();

        string answer = (input.ReadLine() ?? "").Trim();
        return answer.StartsWith("y", StringComparison.OrdinalIgnoreCase) && CertificateTrust.Trust(certificate, output);
    }

    /// <summary>What to type in Power BI's Database tab. Never the password itself: this is printed.</summary>
    private static string Credentials(StartupInfo info) => (info.User, info.PasswordRequired) switch
    {
        (null, false) => "any username and password",
        (null, true) => "any username and the --password value",
        (_, false) => $"the username {info.User} and any password",
        (_, true) => $"the username {info.User} and the --password value",
    };
}
