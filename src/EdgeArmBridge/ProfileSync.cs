namespace EdgeArmBridge;

public static class ProfileSync
{
    // Copied, in order. Paths are relative to the profile root.
    // The list is explicit on purpose: a new Edge store should be a decision,
    // not a surprise. Edge-only stores not listed here do not transfer.
    public static readonly string[] IncludePaths =
    {
        "Local State",
        "Last Version",
        "Default/Preferences",
        "Default/Secure Preferences",
        "Default/Bookmarks",
        "Default/Login Data",
        "Default/Login Data For Account",
        "Default/Web Data",
        "Default/History",
        "Default/Favicons",
        "Default/Network",
        "Default/Cookies",
        "Default/Sessions",
        "Default/Extension State",
        "Default/Local Extension Settings",
        "Default/Sync Extension Settings",
        "Default/Extensions",
        "Default/Local Storage",
        "Default/Session Storage",
        "Default/IndexedDB",
        "Default/Service Worker",
    };

    // Never copied, even inside an included directory. Locks and caches are
    // per-run state. Copying a lock file into the target is a classic way to
    // make a good profile look corrupt.
    private static readonly string[] DenyNames =
    {
        "LOCK", "SingletonLock", "SingletonCookie", "SingletonSocket",
        "GPUCache", "Code Cache", "Cache", "Cache_Data", "GrShaderCache",
        "BrowserMetrics", "Crash Reports", "ShaderCache",
    };

    public static bool IsProfileLocked(string profilePath)
    {
        // Both browsers hold a lock file and a flock while running. The lock
        // file alone can be stale, so check for a live process whose command
        // line names this profile too.
        var lockFile = Path.Combine(profilePath, "SingletonLock");
        if (!File.Exists(lockFile) && !Directory.Exists(lockFile))
            return false;
        foreach (var procDir in Directory.EnumerateDirectories("/proc").Where(d => int.TryParse(Path.GetFileName(d), out _)))
        {
            try
            {
                var cmdline = File.ReadAllText(Path.Combine(procDir, "cmdline"));
                if (cmdline.Contains(profilePath, StringComparison.Ordinal))
                    return true;
            }
            catch (IOException) { /* process went away mid-scan */ }
            catch (UnauthorizedAccessException) { /* not ours to read */ }
        }
        return false;
    }

    public static void CopyToStaging(string source, string staging)
    {
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        foreach (var relative in IncludePaths)
        {
            var from = Path.Combine(source, relative);
            var to = Path.Combine(staging, relative);
            if (Directory.Exists(from))
                CopyDirectory(from, to);
            else if (File.Exists(from))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                CopyFileStable(from, to);
            }
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var entry in Directory.EnumerateFileSystemEntries(from))
        {
            var name = Path.GetFileName(entry);
            if (DenyNames.Contains(name))
                continue;
            var dest = Path.Combine(to, name);
            if (Directory.Exists(entry))
                CopyDirectory(entry, dest);
            else if (File.Exists(entry))
                CopyFileStable(entry, dest);
        }
    }

    private static void CopyFileStable(string from, string to)
    {
        // SQLite files travel with their -wal and -journal siblings when they
        // exist. Copying the main file alone can hand the target a database
        // that is missing it's last committed transactions.
        File.Copy(from, to, overwrite: true);
        foreach (var suffix in new[] { "-wal", "-journal", "-shm" })
            if (File.Exists(from + suffix))
                File.Copy(from + suffix, to + suffix, overwrite: true);
    }

    public static string? ReadSourceMajor(string profilePath)
    {
        var lastVersion = Path.Combine(profilePath, "Last Version");
        if (!File.Exists(lastVersion))
            return null;
        var text = File.ReadAllText(lastVersion).Trim();
        return text.Split('.').FirstOrDefault();
    }

    public static void SwapIn(string staging, string target, string backupRoot, int backupsToKeep)
    {
        Directory.CreateDirectory(backupRoot);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var backupPath = Path.Combine(backupRoot, $"chromium-profile-{stamp}");
        var hadTarget = Directory.Exists(target);
        if (hadTarget)
            Directory.Move(target, backupPath);
        try
        {
            Directory.Move(staging, target);
        }
        catch
        {
            if (hadTarget && !Directory.Exists(target))
                Directory.Move(backupPath, target); // put the old profile back, then let the caller report the failure
            throw;
        }
        var backups = Directory.EnumerateDirectories(backupRoot, "chromium-profile-*")
            .OrderByDescending(d => d, StringComparer.Ordinal).ToList();
        foreach (var old in backups.Skip(backupsToKeep))
            Directory.Delete(old, recursive: true);
    }
}
