using System.Text.Json;

namespace EdgeArmBridge;

public sealed class SyncConfig
{
    public string EdgeProfilePath { get; set; } = "";
    public string ChromiumProfilePath { get; set; } = "";
    public string ChromiumBinary { get; set; } = "";
    public string WorkDirectory { get; set; } = "";
    public string EdgeSafeStorageLabel { get; set; } = "Microsoft Edge Safe Storage";
    public string ChromiumSafeStorageLabel { get; set; } = "Chrome Safe Storage";
    public int BackupsToKeep { get; set; } = 3;
    public int StaleSourceWarnHours { get; set; } = 48;
    public string ProbeScript { get; set; } = "tools/probe_profile.py";

    public static SyncConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<SyncConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"Config file parsed to nothing: {path}");
        if (string.IsNullOrWhiteSpace(config.EdgeProfilePath))
            throw new InvalidDataException("config: edgeProfilePath is required");
        if (string.IsNullOrWhiteSpace(config.ChromiumProfilePath))
            throw new InvalidDataException("config: chromiumProfilePath is required");
        if (string.IsNullOrWhiteSpace(config.WorkDirectory))
            config.WorkDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config.ChromiumProfilePath))!, "edge-arm-bridge-work");
        return config;
    }
}
