using System.Diagnostics;
using System.Text.Json;
using EdgeArmBridge;

// edge-arm-bridge sync service.
// One run: copy the Edge profile to staging, move the Safe Storage secret,
// check the staged copy can decrypt, swap it in as the Chromium profile.
// Any failure before the swap leaves the live Chromium profile alone.
// Exit codes are in ExitCodes.cs. A status.json lands in the work directory
// on every run, good or bad.

string? configPath = null;
bool once = false, dryRun = false, daemon = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config": configPath = args[++i]; break;
        case "--once": once = true; break;
        case "--dry-run": dryRun = true; break;
        case "--daemon": daemon = true; break;
        default: Console.Error.WriteLine($"unknown argument: {args[i]}"); return ExitCodes.BadArguments;
    }
}
if (!once && !daemon)
{
    Console.Error.WriteLine("pass --once for a single sync, or --daemon to sync every 24 hours");
    return ExitCodes.BadArguments;
}
configPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "edge-arm-bridge", "config.json");

SyncConfig config;
try { config = SyncConfig.Load(configPath); }
catch (Exception ex) { Console.Error.WriteLine($"config: {ex.Message}"); return ExitCodes.BadArguments; }

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (once)
    return await RunSyncAsync(config, dryRun, cts.Token);

while (!cts.IsCancellationRequested)
{
    var code = await RunSyncAsync(config, dryRun, cts.Token);
    Console.WriteLine($"[{DateTimeOffset.Now:u}] sync finished, exit code {code}. Next run in 24 hours.");
    try { await Task.Delay(TimeSpan.FromHours(24), cts.Token); }
    catch (TaskCanceledException) { break; }
}
return ExitCodes.Ok;

async Task<int> RunSyncAsync(SyncConfig cfg, bool dry, CancellationToken ct)
{
    Directory.CreateDirectory(cfg.WorkDirectory);
    var status = new Dictionary<string, object?>
    {
        ["started_at"] = DateTimeOffset.Now.ToString("u"),
        ["source"] = cfg.EdgeProfilePath,
        ["target"] = cfg.ChromiumProfilePath,
        ["dry_run"] = dry,
    };
    int Finish(int code, string result, string? detail = null)
    {
        status["exit_code"] = code;
        status["result"] = result;
        if (detail is not null) status["detail"] = detail;
        status["finished_at"] = DateTimeOffset.Now.ToString("u");
        File.WriteAllText(Path.Combine(cfg.WorkDirectory, "status.json"),
            JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"sync {result}: {detail ?? result}");
        return code;
    }

    if (!Directory.Exists(cfg.EdgeProfilePath))
        return Finish(ExitCodes.CopyFailed, "failed", $"source profile not found: {cfg.EdgeProfilePath}");
    if (ProfileSync.IsProfileLocked(cfg.EdgeProfilePath))
        return Finish(ExitCodes.ProfileLocked, "failed", "Edge is running on the source profile. Close Edge and run again.");
    if (Directory.Exists(cfg.ChromiumProfilePath) && ProfileSync.IsProfileLocked(cfg.ChromiumProfilePath))
        return Finish(ExitCodes.ProfileLocked, "failed", "Chromium is running on the target profile. Close Chromium and run again.");

    // Version gate (findings, finding 8): never hand a newer profile to an
    // older browser.
    var sourceMajor = ProfileSync.ReadSourceMajor(cfg.EdgeProfilePath);
    var targetMajor = await ReadChromiumMajorAsync(cfg.ChromiumBinary, ct);
    status["source_major"] = sourceMajor;
    status["target_major"] = targetMajor;
    if (sourceMajor is not null && targetMajor is not null &&
        int.TryParse(sourceMajor, out var s) && int.TryParse(targetMajor, out var t) && t < s)
        return Finish(ExitCodes.VersionGate, "failed", $"target browser major {t} is older than source profile major {s}. Update Chromium first.");

    ProbeResult sourceProbe;
    try { sourceProbe = await Probe.RunAsync(cfg.ProbeScript, cfg.EdgeProfilePath, ct); }
    catch (Exception ex) { return Finish(ExitCodes.CopyFailed, "failed", $"source probe failed: {ex.Message}"); }
    status["source_logins"] = sourceProbe.Logins;
    status["source_cookies"] = sourceProbe.Cookies;

    var staging = Path.Combine(cfg.WorkDirectory, "staging-profile");
    try { ProfileSync.CopyToStaging(cfg.EdgeProfilePath, staging); }
    catch (Exception ex) { return Finish(ExitCodes.CopyFailed, "failed", $"copy to staging failed: {ex.Message}"); }

    // The keyring move (finding 5). Without the secret, the staged copy is a
    // trap: it opens, and quietly loses the sessions it was meant to keep.
    var keys = new KeyMigration(cfg);
    string secret;
    try
    {
        secret = await keys.ReadEdgeSecretAsync(ct);
        if (!dry) await keys.StoreChromiumSecretAsync(secret, ct);
    }
    catch (KeyUnavailableException ex)
    {
        Directory.Delete(staging, recursive: true);
        return Finish(ExitCodes.KeyUnavailable, "failed", ex.Message);
    }

    ProbeResult stagedProbe;
    try { stagedProbe = await Probe.RunAsync(cfg.ProbeScript, staging, ct); }
    catch (Exception ex) { return Finish(ExitCodes.CopyFailed, "failed", $"staged probe failed: {ex.Message}"); }
    if (stagedProbe.Logins != sourceProbe.Logins || stagedProbe.Cookies != sourceProbe.Cookies)
        return Finish(ExitCodes.DecryptCheckFailed, "failed",
            $"staged counts moved: logins {sourceProbe.Logins} to {stagedProbe.Logins}, cookies {sourceProbe.Cookies} to {stagedProbe.Cookies}. Not swapping.");

    if (stagedProbe.SampleLoginBlobHex.Length > 0)
    {
        var blob = Convert.FromHexString(stagedProbe.SampleLoginBlobHex);
        if (!OscryptCheck.TryDecrypt(blob, secret, out _))
            return Finish(ExitCodes.DecryptCheckFailed, "failed",
                "a sampled password from the staged copy does not decrypt with the Edge Safe Storage secret. Not swapping.");
        status["decrypt_check"] = "passed";
    }

    // Do not strand passwords the target saved on it's own (finding 5).
    var marker = Path.Combine(cfg.ChromiumProfilePath, ".edge-arm-bridge-mirror");
    if (Directory.Exists(cfg.ChromiumProfilePath) && !File.Exists(marker))
    {
        var targetProbe = await Probe.RunAsync(cfg.ProbeScript, cfg.ChromiumProfilePath, ct);
        if (targetProbe.Logins > 0)
            return Finish(ExitCodes.CopyFailed, "failed",
                "target profile holds logins that did not come from this mirror. Back it up or clear it by hand, then run again.");
    }

    // Stale source check (finding 9): Edge may have stopped syncing while
    // the mirror keeps copying. A profile that has not changed in two runs
    // and is older than the warn window is reported, not celebrated.
    var statePath = Path.Combine(cfg.WorkDirectory, "state.json");
    long lastNewest = 0;
    if (File.Exists(statePath))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
        if (doc.RootElement.TryGetProperty("source_newest_mtime", out var v)) lastNewest = v.GetInt64();
    }
    var ageHours = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sourceProbe.NewestMtime) / 3600.0;
    var stale = sourceProbe.NewestMtime == lastNewest && ageHours > cfg.StaleSourceWarnHours;
    status["source_stale_suspected"] = stale;

    if (dry)
    {
        Directory.Delete(staging, recursive: true);
        return Finish(stale ? ExitCodes.SourceMayBeStale : ExitCodes.Ok, "dry run ok",
            "staging copy built and checked. Nothing swapped.");
    }

    File.WriteAllText(Path.Combine(staging, ".edge-arm-bridge-mirror"),
        $"source={cfg.EdgeProfilePath} synced_at={DateTimeOffset.Now:u}");
    try
    {
        ProfileSync.SwapIn(staging, cfg.ChromiumProfilePath, Path.Combine(cfg.WorkDirectory, "backups"), cfg.BackupsToKeep);
    }
    catch (Exception ex) { return Finish(ExitCodes.CopyFailed, "failed", $"swap failed, previous profile restored where possible: {ex.Message}"); }

    File.WriteAllText(statePath, JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["source_newest_mtime"] = sourceProbe.NewestMtime,
        ["last_sync_at"] = DateTimeOffset.Now.ToString("u"),
    }));
    return Finish(stale ? ExitCodes.SourceMayBeStale : ExitCodes.Ok,
        stale ? "synced, with a warning" : "synced",
        stale ? "the Edge profile has not changed across two runs and looks stale. Check that Edge is signed in and syncing." : "profile mirrored and decrypt check passed");
}

static async Task<string?> ReadChromiumMajorAsync(string binary, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(binary) || !File.Exists(binary))
        return null;
    try
    {
        var start = new ProcessStartInfo(binary, "--version") { RedirectStandardOutput = true, UseShellExecute = false };
        using var process = Process.Start(start);
        if (process is null) return null;
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var version = output.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        return version?.Split('.').FirstOrDefault();
    }
    catch { return null; }
}
