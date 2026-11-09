using System.Diagnostics;

namespace EdgeArmBridge;

// The Safe Storage secret moves keyring entry to keyring entry through
// secret-tool (libsecret-tools). The value passes through memory and a pipe.
// It is never written to disk by this service and never logged.
//
// secret-tool names entries by label, and Chromium stores it's entry with
// the attributes application=<product> and xdg:schema set to the OSCrypt
// schema. Lookup by label alone is what a human would do at a terminal, so
// the service tries label first and reports the exact failure if the entry
// is not found or the keyring is locked.
public sealed class KeyMigration
{
    private readonly SyncConfig _config;

    public KeyMigration(SyncConfig config) => _config = config;

    public async Task<string> ReadEdgeSecretAsync(CancellationToken ct)
    {
        // Test hook for headless runs and containers: an operator can hand the
        // secret in through the environment instead of the keyring. The value
        // is used for the decrypt check and the store, and goes no further.
        var fromEnv = Environment.GetEnvironmentVariable("EDGE_ARM_BRIDGE_SAFE_STORAGE_SECRET");
        if (!string.IsNullOrEmpty(fromEnv))
            return fromEnv;

        var (ok, value, error) = await RunSecretToolAsync(new[] { "lookup", "label", _config.EdgeSafeStorageLabel }, null, ct);
        if (!ok || string.IsNullOrEmpty(value))
            throw new KeyUnavailableException(
                $"could not read keyring entry '{_config.EdgeSafeStorageLabel}'. " +
                $"The keyring may be locked (this service must run in the desktop session), or secret-tool may be missing. Detail: {error}");
        return value.Trim();
    }

    public async Task StoreChromiumSecretAsync(string secret, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EDGE_ARM_BRIDGE_SAFE_STORAGE_SECRET")))
            return; // env-provided runs do not write operator secrets anywhere
        var (ok, _, error) = await RunSecretToolAsync(
            new[] { "store", "--label", _config.ChromiumSafeStorageLabel, "xdg:schema", "chrome_libsecret_os_crypt_password_v2" },
            secret, ct);
        if (!ok)
            throw new KeyUnavailableException($"could not store keyring entry '{_config.ChromiumSafeStorageLabel}'. Detail: {error}");
    }

    private static async Task<(bool Ok, string StdOut, string StdErr)> RunSecretToolAsync(string[] args, string? stdin, CancellationToken ct)
    {
        try
        {
            var start = new ProcessStartInfo("secret-tool")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin is not null,
                UseShellExecute = false,
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null) return (false, "", "secret-tool did not start");
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(secretMemory(stdin), ct);
                process.StandardInput.Close();
            }
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode == 0, stdout, stderr.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return (false, "", "secret-tool not found, install libsecret-tools");
        }
    }

    private static ReadOnlyMemory<char> secretMemory(string value) => value.AsMemory();
}

public sealed class KeyUnavailableException(string message) : Exception(message);
