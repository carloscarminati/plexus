using System.Diagnostics;

namespace Plexus.Sidecar.Services;

// Resolves the Anthropic API key. The key must never reach the renderer — only
// the sidecar reads it. Preference order:
//   1. macOS keychain  (security find-generic-password -s plexus-anthropic-key)
//   2. ANTHROPIC_API_KEY environment variable
public sealed class KeychainService
{
    private const string Service = "plexus-anthropic-key";
    private const string Account = "plexus";

    public string? GetAnthropicKey()
    {
        var fromKeychain = ReadMacKeychain();
        if (!string.IsNullOrWhiteSpace(fromKeychain))
            return fromKeychain.Trim();

        var fromEnv = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv.Trim();
    }

    private static string? ReadMacKeychain()
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
            psi.ArgumentList.Add(Service);
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
