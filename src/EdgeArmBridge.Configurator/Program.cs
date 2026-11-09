using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Gtk;

// Edge Sync Settings: a small GTK 4 front end for the edge-arm-bridge
// service. It edits the same config file the sync service reads, launches
// the Edge sign-in window, runs syncs, and toggles the wake timer.

string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
string configDir = Path.Combine(home, ".config", "edge-arm-bridge");
string configPath = Path.Combine(configDir, "config.json");
string wakeSecondsPath = Path.Combine(configDir, "wake-seconds");
string dataDir = Path.Combine(home, ".local", "share", "edge-arm-bridge");

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

    void SaveAll()
    {
        settings.EdgeProfilePath = edgeProfile.GetText();
        settings.ChromiumProfilePath = chromiumProfile.GetText();
        settings.ChromiumBinaryPath = chromiumBinary.GetText();
        settings.WakeSeconds = wakeSeconds.GetText();
        settings.Save(configPath, wakeSecondsPath);
    }

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
        RunWithPoll("/usr/bin/edge-arm-bridge-sync", note, status);
    };
    var wakeNow = Button.NewWithLabel("Wake and sync");
    wakeNow.OnClicked += (_, _) =>
    {
        SaveAll();
        RunWithPoll("/usr/bin/edge-arm-bridge-wake", note, status);
    };
    buttons.Append(save);
    buttons.Append(signIn);
    buttons.Append(syncNow);
    buttons.Append(wakeNow);
    grid.Attach(buttons, 0, row++, 2, 1);
    grid.Attach(note, 0, row, 2, 1);

    window.SetChild(grid);
    window.Present();
};
return app.RunWithSynchronizationContext(args);

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

void StartDetached(string path)
{
    try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = false }); }
    catch { /* missing wrapper shows up as a failed sync later */ }
}

void RunWithPoll(string path, Label note, Label status)
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
