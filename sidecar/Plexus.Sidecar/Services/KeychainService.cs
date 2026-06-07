using System.Diagnostics;

namespace Plexus.Sidecar.Services;

// Resolves provider API keys. Keys must never reach the renderer — only the
// sidecar reads them. They live in the OS keychain, referenced by provider id
// (service "plexus-{providerId}-key"). Per-provider env fallback:
// {PROVIDERID}_API_KEY (e.g. ANTHROPIC_API_KEY). Preference: keychain, then env.
public sealed class KeychainService
{
    private const string Account = "plexus";

    public string? GetKey(string providerId)
    {
        var service = $"plexus-{providerId}-key";
        var fromKeychain = ReadMacKeychain(service);
        if (!string.IsNullOrWhiteSpace(fromKeychain))
            return fromKeychain.Trim();

        var envVar = $"{providerId.ToUpperInvariant()}_API_KEY";
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv.Trim();
    }

    // Convenience for the R0 single-provider path.
    public string? GetAnthropicKey() => GetKey("anthropic");

    // True if a secret is stored in the keychain for this id (does NOT read it out).
    // Env-var fallbacks are not "stored" — Settings manages keychain entries only.
    public bool HasKey(string providerId)
    {
        if (!OperatingSystem.IsMacOS())
            return false;
        var (exit, _) = RunSecurity("find-generic-password", "-a", Account, "-s", $"plexus-{providerId}-key");
        return exit == 0;
    }

    // Write/update a secret. The panel edits config; secrets only ever land here,
    // never in any config file. Returns false off macOS (no keychain).
    public bool SetKey(string providerId, string secret)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(secret))
            return false;
        // -U updates the item in place if it already exists instead of erroring.
        var (exit, _) = RunSecurity("add-generic-password", "-a", Account, "-s", $"plexus-{providerId}-key", "-w", secret, "-U");
        return exit == 0;
    }

    public bool DeleteKey(string providerId)
    {
        if (!OperatingSystem.IsMacOS())
            return false;
        var (exit, _) = RunSecurity("delete-generic-password", "-a", Account, "-s", $"plexus-{providerId}-key");
        return exit == 0; // non-zero also means "wasn't there", which is fine for the caller
    }

    private static (int ExitCode, string Output) RunSecurity(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/security",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null)
                return (-1, "");
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return (proc.ExitCode, output);
        }
        catch
        {
            return (-1, "");
        }
    }

    private static string? ReadMacKeychain(string service)
    {
        if (!OperatingSystem.IsMacOS())
            return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/security",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("find-generic-password");
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(Account);
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(service);
            psi.ArgumentList.Add("-w"); // print only the password to stdout

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return proc.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null; // keychain not available / item missing — fall through to env.
        }
    }
}
