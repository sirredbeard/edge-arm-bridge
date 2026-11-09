namespace EdgeArmBridge;

// One code per failure step, so systemd and cron logs say what broke
// without anyone opening the status file first.
public static class ExitCodes
{
    public const int Ok = 0;
    public const int BadArguments = 1;
    public const int SourceMayBeStale = 2;   // Edge itself looks like it stopped syncing
    public const int DecryptCheckFailed = 3;  // staged copy cannot decrypt: never swap
    public const int ProfileLocked = 4;       // a browser is running on a profile
    public const int KeyUnavailable = 5;      // Safe Storage secret could not be read or stored
    public const int VersionGate = 6;         // target browser major is older than source major
    public const int CopyFailed = 7;
}
