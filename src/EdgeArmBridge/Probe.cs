using System.Diagnostics;
using System.Text.Json;

namespace EdgeArmBridge;

public sealed record ProbeResult(
    int Logins,
    Dictionary<string, int> LoginPrefixes,
    int Cookies,
    Dictionary<string, int> CookiePrefixes,
    string SampleLoginBlobHex,
    long NewestMtime);

// Profile counts and one sample blob come from tools/probe_profile.py.
// The probe is read-only and prints no plain text secrets.
public static class Probe
{
    public static async Task<ProbeResult> RunAsync(string probeScript, string profilePath, CancellationToken ct)
    {
        var start = new ProcessStartInfo("python3", $"\"{probeScript}\" \"{profilePath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start probe");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"probe failed on {profilePath}: {await process.StandardError.ReadToEndAsync(ct)}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Dictionary<string, int> ReadMap(string name) =>
            root.GetProperty(name).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        return new ProbeResult(
            root.GetProperty("logins").GetInt32(),
            ReadMap("login_prefixes"),
            root.GetProperty("cookies").GetInt32(),
            ReadMap("cookie_prefixes"),
            root.GetProperty("sample_login_blob_hex").GetString() ?? "",
            root.GetProperty("newest_mtime").GetInt64());
    }
}
