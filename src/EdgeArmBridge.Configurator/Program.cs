using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Gtk;

// Edge Sync Settings: a small GTK 4 front end for the edge-arm-bridge
// service. A first run is guided: sign into Edge, watch the mirror steps
// happen, land in Chromium. After that the same window is the settings
// page: it edits the config file the sync service reads, runs syncs, and
// toggles the wake timer.

string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
string configDir = Path.Combine(home, ".config", "edge-arm-bridge");
string configPath = Path.Combine(configDir, "config.json");
string wakeSecondsPath = Path.Combine(configDir, "wake-seconds");
string dataDir = Path.Combine(home, ".local", "share", "edge-arm-bridge");
string wrapperEdgeProfile = Path.Combine(dataDir, "edge-profile");

var settings = BridgeSettings.Load(configPath, home, dataDir);

var app = Application.New("io.github.sirredbeard.edgearmbridge.configurator", Gio.ApplicationFlags.FlagsNone);
app.OnActivate += (_, _) =>
{
    var window = ApplicationWindow.New(app);
    window.Title = "Edge Sync Settings";
    window.SetDefaultSize(560, 0);

    var status = Label.New(StatusText());
    status.SetXalign(0);
    status.SetWrap(true);
    var note = Label.New("");
    note.SetXalign(0);
    note.SetWrap(true);

    var edgeProfile = Entry.New();
    edgeProfile.SetText(settings.EdgeProfilePath);
    var chromiumProfile = Entry.New();
    chromiumProfile.SetText(settings.ChromiumProfilePath);
    var chromiumBinary = Entry.New();
    chromiumBinary.SetText(settings.ChromiumBinaryPath);
    var wakeSeconds = Entry.New();
    wakeSeconds.SetText(settings.WakeSeconds);

    void SaveAll()
    {
        settings.EdgeProfilePath = edgeProfile.GetText();
        settings.ChromiumProfilePath = chromiumProfile.GetText();
        settings.ChromiumBinaryPath = chromiumBinary.GetText();
        settings.WakeSeconds = wakeSeconds.GetText();
        settings.Save(configPath, wakeSecondsPath);
    }

    // ---- First run card ----
    var step1State = Label.New("Not started.");
    step1State.SetXalign(0); step1State.SetWrap(true);
    var step2State = Label.New("Waiting for sign-in.");
    step2State.SetXalign(0); step2State.SetWrap(true);
    var step3State = Label.New("Waiting for the first mirror.");
    step3State.SetXalign(0); step3State.SetWrap(true);
    var openChromium = Button.NewWithLabel("Open Chromium");
    openChromium.SetSensitive(false);
    var mirrorNow = Button.NewWithLabel("Mirror now");
    mirrorNow.SetSensitive(false);

    void RunFirstSync()
    {
        step2State.SetText("Mirroring now: copying the profile, moving the sign-in secret, test-decrypting a stored password.");
        RunWithPoll("/usr/bin/edge-arm-bridge-sync", note, status, code =>
        {
            if (code == 0)
            {
                step2State.SetText(SyncOutcomeText());
                step3State.SetText("Ready. Your logins and cookies are live in the Chromium profile.");
                openChromium.SetSensitive(true);
            }
            else
            {
                step2State.SetText(SyncFailureText(code));
                mirrorNow.SetSensitive(true);
            }
        });
    }

    var openEdge = Button.NewWithLabel("Open Edge and sign in");
    bool edgeSeen = false;
    int edgeTicks = 0;
    openEdge.OnClicked += (_, _) =>
    {
        SaveAll();
        StartDetached("/usr/bin/edge-arm-bridge-edge");
        step1State.SetText("Edge is starting. Sign into your Microsoft account in the Edge window, give sync a minute, then close the window.");
        openEdge.SetSensitive(false);
        GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 1000, () =>
        {
            edgeTicks++;
            if (EdgeRunning())
            {
                edgeSeen = true;
                step1State.SetText("Edge is open. Sign in, let sync finish, then close the Edge window and the mirror starts on its own.");
                return true;
            }
            if (edgeSeen)
            {
                step1State.SetText("Signed in and closed. Good: a closed profile is a clean profile to mirror.");
                mirrorNow.SetSensitive(false);
                RunFirstSync();
                return false;
            }
            if (edgeTicks > 20)
            {
                step1State.SetText("Edge did not appear. It may have failed to start, or it opened and closed already. Mirror now tries with whatever profile is on disk.");
                mirrorNow.SetSensitive(true);
                openEdge.SetSensitive(true);
                return false;
            }
            return true;
        });
    };
    mirrorNow.OnClicked += (_, _) => { SaveAll(); mirrorNow.SetSensitive(false); RunFirstSync(); };
    openChromium.OnClicked += (_, _) =>
    {
        StartDetached(settings.ChromiumBinaryPath, $"--user-data-dir={settings.ChromiumProfilePath} --no-first-run");
        step3State.SetText("Chromium is opening with the mirrored profile. Edge stays the source of truth: changes made in Chromium do not flow back.");
    };

    var firstRunBox = Box.New(Orientation.Vertical, 6);
    firstRunBox.SetMarginStart(12); firstRunBox.SetMarginEnd(12);
    firstRunBox.SetMarginTop(10); firstRunBox.SetMarginBottom(10);
    firstRunBox.Append(StepLabel("1. Sign into Edge", "Edge runs under FEX on this machine so a Microsoft account can sign in. That sign-in is what makes the profile worth mirroring."));
    firstRunBox.Append(step1State);
    firstRunBox.Append(openEdge);
    firstRunBox.Append(StepLabel("2. Mirror to Chromium", "The mirror copies the profile, moves the sign-in secret to the Chromium keyring entry, and test-decrypts one stored password before it swaps anything in. If the test fails, nothing changes."));
    firstRunBox.Append(step2State);
    firstRunBox.Append(mirrorNow);
    firstRunBox.Append(StepLabel("3. Open Chromium", "The fast native browser, with your Edge logins and cookies live in it."));
    firstRunBox.Append(step3State);
    firstRunBox.Append(openChromium);
    firstRunBox.Append(Label.New("After this, turn on the wake timer below and the mirror stays fresh on its own."));
    var firstRunCard = Frame.New("First run");
    firstRunCard.SetChild(firstRunBox);
    firstRunCard.SetVisible(FirstRunIncomplete());

    // ---- Settings grid ----
    var grid = Grid.New();
    grid.SetRowSpacing(8);
    grid.SetColumnSpacing(12);
    grid.SetMarginStart(16);
    grid.SetMarginEnd(16);
    grid.SetMarginTop(16);
    grid.SetMarginBottom(16);
    int row = 0;
    grid.Attach(Label.New("Last sync"), 0, row, 1, 1);
    grid.Attach(status, 1, row++, 1, 1);
    grid.Attach(Label.New("Edge profile"), 0, row, 1, 1);
    grid.Attach(edgeProfile, 1, row++, 1, 1);
    grid.Attach(Label.New("Chromium profile"), 0, row, 1, 1);
    grid.Attach(chromiumProfile, 1, row++, 1, 1);
    grid.Attach(Label.New("Chromium binary"), 0, row, 1, 1);
    grid.Attach(chromiumBinary, 1, row++, 1, 1);
    grid.Attach(Label.New("Wake dwell (seconds)"), 0, row, 1, 1);
    grid.Attach(wakeSeconds, 1, row++, 1, 1);

    var timerSwitch = Gtk.Switch.New();
    timerSwitch.SetActive(TimerEnabled());
    timerSwitch.OnStateSet += (_, args) =>
    {
        RunUserSystemctl(args.State ? "enable" : "disable");
        note.SetText(args.State ? "Wake timer on: Edge wakes every six hours and the mirror follows."
                                : "Wake timer off.");
        return false;
    };
    grid.Attach(Label.New("Wake timer"), 0, row, 1, 1);
    grid.Attach(timerSwitch, 1, row++, 1, 1);

    var buttons = Box.New(Orientation.Horizontal, 8);
    var save = Button.NewWithLabel("Save settings");
    save.OnClicked += (_, _) =>
    {
        SaveAll();
        note.SetText("Settings saved to " + configPath);
    };
    var signIn = Button.NewWithLabel("Sign into Edge");
    signIn.OnClicked += (_, _) =>
    {
        SaveAll();
        StartDetached("/usr/bin/edge-arm-bridge-edge");
        note.SetText("Edge is opening under FEX. Sign in there; the mirror picks it up on the next sync.");
    };
    var syncNow = Button.NewWithLabel("Sync now");
    syncNow.OnClicked += (_, _) =>
    {
        SaveAll();
        RunWithPoll("/usr/bin/edge-arm-bridge-sync", note, status, null);
    };
    var wakeNow = Button.NewWithLabel("Wake and sync");
    wakeNow.OnClicked += (_, _) =>
    {
        SaveAll();
        RunWithPoll("/usr/bin/edge-arm-bridge-wake", note, status, null);
    };
    buttons.Append(save);
    buttons.Append(signIn);
    buttons.Append(syncNow);
    buttons.Append(wakeNow);
    grid.Attach(buttons, 0, row++, 2, 1);
    grid.Attach(note, 0, row, 2, 1);

    var page = Box.New(Orientation.Vertical, 10);
    page.Append(firstRunCard);
    page.Append(grid);
    window.SetChild(page);
    window.Present();
};
return app.RunWithSynchronizationContext(args);

Label StepLabel(string title, string body)
{
    var label = Label.New(title + "\n" + body);
    label.SetXalign(0);
    label.SetWrap(true);
    return label;
}

bool FirstRunIncomplete()
{
    string statusPath = Path.Combine(settings.WorkDir, "status.json");
    if (!File.Exists(statusPath)) return true;
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(statusPath));
        bool synced = doc.RootElement.TryGetProperty("result", out var r) && r.GetString() == "synced";
        return !synced || !Directory.Exists(settings.ChromiumProfilePath);
    }
    catch { return true; }
}

bool EdgeRunning()
{
    try
    {
        var p = Process.Start(new ProcessStartInfo("pgrep", "-f user-data-dir=" + wrapperEdgeProfile)
        { RedirectStandardOutput = true });
        string output = p!.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Trim().Length > 0;
    }
    catch { return false; }
}

string SyncOutcomeText()
{
    string statusPath = Path.Combine(settings.WorkDir, "status.json");
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(statusPath));
        var root = doc.RootElement;
        string decrypt = root.TryGetProperty("decrypt_check", out var d) ? d.GetString() ?? "" : "";
        long logins = root.TryGetProperty("source_logins", out var l) ? l.GetInt64() : 0;
        long cookies = root.TryGetProperty("source_cookies", out var c) ? c.GetInt64() : 0;
        return $"Done. Decrypt check {decrypt}; the mirror holds {logins} logins and {cookies} cookies from Edge.";
    }
    catch { return "Done. The mirror is in place."; }
}

string SyncFailureText(int code) => code switch
{
    3 => "The decrypt check failed, so nothing was swapped in and the old target is untouched. The usual cause is a sign-in secret that does not match. Sign into Edge again, close it, then mirror.",
    4 => "A browser still holds a profile lock. Close Edge and Chromium, then mirror again.",
    5 => "The sign-in secret was not in the keyring. That happens when the first Edge sign-in ran outside a desktop session. Sign into Edge from this window, in the desktop, then mirror.",
    6 => "The Chromium build is older than the Edge build that wrote this profile, and the mirror will not run across that gap. Update Chromium, then mirror again.",
    _ => $"The mirror stopped with exit {code}. The settings window's note line shows the service output.",
};

string StatusText()
{
    string statusPath = Path.Combine(settings.WorkDir, "status.json");
    if (!File.Exists(statusPath)) return "No sync has run yet.";
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(statusPath));
        var root = doc.RootElement;
        string result = root.TryGetProperty("result", out var r) ? r.GetString() ?? "?" : "?";
        string when = root.TryGetProperty("finished_at", out var f) ? f.GetString() ?? "" : "";
        return result + (when.Length > 0 ? " at " + when : "");
    }
    catch
    {
        return "Status file could not be read.";
    }
}

bool TimerEnabled()
{
    try
    {
        var p = Process.Start(new ProcessStartInfo("systemctl", "--user is-enabled edge-arm-bridge-wake.timer")
        { RedirectStandardOutput = true });
        string output = p!.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return output == "enabled";
    }
    catch { return false; }
}

void RunUserSystemctl(string verb)
{
    try
    {
        var psi = new ProcessStartInfo("systemctl", $"--user {verb} --now edge-arm-bridge-wake.timer")
        { RedirectStandardOutput = true, RedirectStandardError = true };
        Process.Start(psi)?.WaitForExit();
    }
    catch { /* the note line carries the state the user asked for */ }
}

void StartDetached(string path, string arguments = "")
{
    try { Process.Start(new ProcessStartInfo(path) { Arguments = arguments, UseShellExecute = false }); }
    catch { /* missing pieces show up as a failed step later */ }
}

void RunWithPoll(string path, Label note, Label status, Action<int>? onDone)
{
    Process proc;
    try
    {
        proc = Process.Start(new ProcessStartInfo(path)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
    }
    catch (Exception ex)
    {
        note.SetText("Could not start " + path + ": " + ex.Message);
        onDone?.Invoke(-1);
        return;
    }
    note.SetText("Running " + Path.GetFileName(path) + " ...");
    GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 500, () =>
    {
        if (!proc.HasExited) return true;
        string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        string last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        note.SetText($"Exit {proc.ExitCode}. {last}");
        status.SetText(StatusText());
        onDone?.Invoke(proc.ExitCode);
        return false;
    });
}

sealed class BridgeSettings
{
    public string EdgeProfilePath = "";
    public string ChromiumProfilePath = "";
    public string ChromiumBinaryPath = "";
    public string WorkDir = "";
    public string WakeSeconds = "300";

    public static BridgeSettings Load(string configPath, string home, string dataDir)
    {
        var s = new BridgeSettings
        {
            EdgeProfilePath = Path.Combine(dataDir, "edge-profile"),
            ChromiumProfilePath = Path.Combine(dataDir, "chromium-profile"),
            ChromiumBinaryPath = File.Exists("/usr/bin/google-chrome") ? "/usr/bin/google-chrome"
                : File.Exists("/usr/bin/chromium") ? "/usr/bin/chromium"
                : "/usr/lib/edge-arm-bridge/chromium/chrome",
            WorkDir = Path.Combine(dataDir, "work"),
        };
        if (File.Exists(configPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                var root = doc.RootElement;
                s.EdgeProfilePath = Get(root, "EdgeProfilePath") ?? Get(root, "edgeProfilePath") ?? s.EdgeProfilePath;
                s.ChromiumProfilePath = Get(root, "ChromiumProfilePath") ?? Get(root, "chromiumProfilePath") ?? s.ChromiumProfilePath;
                s.ChromiumBinaryPath = Get(root, "ChromiumBinaryPath") ?? Get(root, "chromiumBinaryPath") ?? s.ChromiumBinaryPath;
                s.WorkDir = Get(root, "WorkDir") ?? Get(root, "workDir") ?? s.WorkDir;
            }
            catch { /* a broken config falls back to defaults; Save rewrites it */ }
        }
        string wakePath = Path.Combine(Path.GetDirectoryName(configPath)!, "wake-seconds");
        if (File.Exists(wakePath)) s.WakeSeconds = File.ReadAllText(wakePath).Trim();
        return s;
    }

    private static string? Get(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Save(string configPath, string wakeSecondsPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"EdgeProfilePath\": \"{Escape(EdgeProfilePath)}\",");
        sb.AppendLine($"  \"ChromiumProfilePath\": \"{Escape(ChromiumProfilePath)}\",");
        sb.AppendLine($"  \"ChromiumBinaryPath\": \"{Escape(ChromiumBinaryPath)}\",");
        sb.AppendLine($"  \"WorkDir\": \"{Escape(WorkDir)}\"");
        sb.AppendLine("}");
        File.WriteAllText(configPath, sb.ToString());
        if (int.TryParse(WakeSeconds, out int seconds) && seconds > 0)
            File.WriteAllText(wakeSecondsPath, seconds.ToString());
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
