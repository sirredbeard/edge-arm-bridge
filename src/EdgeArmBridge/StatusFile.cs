using System.Text.Json.Serialization;

namespace EdgeArmBridge;

// The two small JSON files the service writes. Typed records instead of
// dictionaries of object: a dictionary's values are polymorphic, and the
// source generated serializer has no metadata for whatever runtime type
// happens to land in one. Typed properties keep every value's type known
// at compile time, which is also what the AOT compiler needs. The JSON
// names are the snake_case names status.json has always used.
public sealed class StatusFile
{
    [JsonPropertyName("started_at")]
    public string StartedAt { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("dry_run")]
    public bool DryRun { get; set; }

    [JsonPropertyName("exit_code")]
    public int ExitCode { get; set; }

    [JsonPropertyName("result")]
    public string Result { get; set; } = "";

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("finished_at")]
    public string FinishedAt { get; set; } = "";

    [JsonPropertyName("source_major")]
    public string? SourceMajor { get; set; }

    [JsonPropertyName("target_major")]
    public string? TargetMajor { get; set; }

    [JsonPropertyName("source_logins")]
    public int? SourceLogins { get; set; }

    [JsonPropertyName("source_cookies")]
    public int? SourceCookies { get; set; }

    [JsonPropertyName("decrypt_check")]
    public string? DecryptCheck { get; set; }

    [JsonPropertyName("source_stale_suspected")]
    public bool? SourceStaleSuspected { get; set; }
}

public sealed class StateFile
{
    [JsonPropertyName("source_newest_mtime")]
    public long SourceNewestMtime { get; set; }

    [JsonPropertyName("last_sync_at")]
    public string LastSyncAt { get; set; } = "";
}
