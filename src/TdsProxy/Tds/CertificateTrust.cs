using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TdsProxy.Tds;

/// <summary>
/// Puts DaffyDB's own certificate among the current Windows user's trusted roots, when asked to.
///
/// Most clients can be told to accept a self-signed certificate - TrustServerCertificate=True - but
/// some cannot, and Power BI's SQL Server connector is the one people meet first: it checks the
/// certificate and has nowhere to say otherwise. For those the certificate has to be trusted by
/// Windows, which is what this does, and only ever on a yes.
///
/// Three things keep that from being more than it sounds:
/// <list type="bullet">
///   <item>Only the public certificate is added, and only to the current user's store - not the
///   machine's, which would need elevation and would speak for every account.</item>
///   <item>Windows asks again itself, with a security warning that no program can skip, so the
///   answer here is never the only one.</item>
///   <item>The certificate is not a certificate authority: its basic constraints say so and its key
///   usage has no certificate signing. Trusting it vouches for DaffyDB's own server on the names
///   in it - localhost, 127.0.0.1 and this machine - and for nothing it could sign.</item>
/// </list>
/// --untrust-cert takes it back out.
/// </summary>
internal static class CertificateTrust
{
    /// <summary>
    /// Set on the copy put in the store, so the store's list shows what it is, and so that
    /// --untrust-cert can find every DaffyDB certificate ever added, not just today's.
    /// </summary>
    private const string FriendlyName = "DaffyDB (self-signed, for local connections)";

    /// <summary>Whether Windows already trusts this certificate, from either the user's roots or the machine's.</summary>
    public static bool IsTrusted(X509Certificate2 certificate)
    {
        if (!OperatingSystem.IsWindows()) return false;

        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            if (store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false).Count > 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Adds the certificate to the current user's trusted roots. Windows shows its own warning
    /// first; saying no there comes back as a failure, and is reported as that and nothing worse.
    /// </summary>
    public static bool Trust(X509Certificate2 certificate, TextWriter output)
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("[tls]    trusting the certificate is only done on Windows; on other systems,");
            output.WriteLine("[tls]    use TrustServerCertificate=True, or Encrypt=Strict with the .cer file.");
            return false;
        }

        if (IsTrusted(certificate))
        {
            output.WriteLine("[tls]    the certificate is already trusted");
            return true;
        }

        // The public half alone. The store would take the private key too if it were offered,
        // and nothing that only needs to recognise the certificate should hold that.
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
        publicOnly.FriendlyName = FriendlyName;

        try
        {
            output.WriteLine("[tls]    Windows will now ask you to confirm; answer Yes to trust the certificate.");
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(publicOnly);
        }
        catch (CryptographicException ex)
        {
            output.WriteLine($"[tls]    not trusted: {ex.Message.Trim()}");
            return false;
        }

        // Windows returns quietly when the warning is declined on some versions, so look rather
        // than assume.
        bool trusted = IsTrusted(certificate);
        output.WriteLine(trusted
            ? "[tls]    trusted for this Windows user (Current User > Trusted Root Certification Authorities)"
            : "[tls]    not trusted: Windows did not add it");
        return trusted;
    }

    /// <summary>
    /// Removes every DaffyDB certificate from the current user's trusted roots - this one and any
    /// left behind by a regenerated one. Windows confirms each removal itself.
    /// </summary>
    public static int Untrust(X509Certificate2? current, TextWriter output)
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("[tls]    nothing to do: certificates are only ever trusted on Windows");
            return 0;
        }

        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        var ours = store.Certificates
            .Where(c => c.FriendlyName == FriendlyName ||
                        (current is not null && c.Thumbprint == current.Thumbprint))
            .ToList();

        int removed = 0;
        foreach (var certificate in ours)
        {
            try
            {
                store.Remove(certificate);
                removed++;
            }
            catch (CryptographicException ex)
            {
                output.WriteLine($"[tls]    kept {certificate.Subject}: {ex.Message.Trim()}");
            }
        }

        output.WriteLine(removed == 0
            ? "[tls]    no DaffyDB certificate was trusted for this Windows user"
            : $"[tls]    removed {removed} DaffyDB certificate(s) from this Windows user's trusted roots");

        if (current is not null && IsTrusted(current))
            output.WriteLine("[tls]    still trusted machine-wide (Local Machine); remove it there with certlm.msc");

        return removed;
    }

    /// <summary>
    /// Whether to ask, which is once per certificate: not if Windows already trusts it, and not if
    /// the answer was no - that is remembered beside the certificate, until the certificate changes.
    /// </summary>
    public static bool ShouldOffer(X509Certificate2 certificate, string certificatePath)
    {
        if (!OperatingSystem.IsWindows() || IsTrusted(certificate)) return false;

        string declined = DeclinedPath(certificatePath);
        return !(File.Exists(declined) && File.ReadAllText(declined).Trim() == certificate.Thumbprint);
    }

    /// <summary>
    /// The question, asked at the prompt before its first command. It is never asked before the
    /// server is listening: a copy started in a window nobody is looking at would otherwise wait
    /// for an answer with its port closed. And it is only asked where there is a prompt at all -
    /// not under --no-repl, and not with input or output redirected.
    /// </summary>
    public static void Offer(X509Certificate2 certificate, string certificatePath, TextReader input, TextWriter output)
    {
        output.WriteLine("  DaffyDB's certificate is self-signed. Most clients can be told to accept it, but some");
        output.WriteLine("  cannot - Power BI's SQL Server connector among them - and only work once Windows trusts it.");
        output.WriteLine("  Trusting it covers DaffyDB's own server on localhost, 127.0.0.1 and this machine's name,");
        output.WriteLine("  for your Windows account only. It cannot vouch for anything else.");
        output.WriteLine();
        output.Write("  Trust it now? Windows will ask you to confirm. [y/N] ");
        output.Flush();

        string answer = (input.ReadLine() ?? "").Trim();
        output.WriteLine();

        if (answer.StartsWith("y", StringComparison.OrdinalIgnoreCase))
        {
            Trust(certificate, output);
            output.WriteLine();
            return;
        }

        try
        {
            File.WriteAllText(DeclinedPath(certificatePath), certificate.Thumbprint);
        }
        catch (IOException)
        {
            // Asked again next time, which is the only cost of not being able to remember.
        }
        catch (UnauthorizedAccessException)
        {
        }

        output.WriteLine("  Not trusted, and you will not be asked again. 'daffydb --trust-cert' does it later.");
        output.WriteLine();
    }

    private static string DeclinedPath(string certificatePath) => Path.ChangeExtension(certificatePath, ".trust-declined");
}
