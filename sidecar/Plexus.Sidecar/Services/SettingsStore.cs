using Plexus.Sidecar.Routing;

namespace Plexus.Sidecar.Services;

// App-level settings that used to be scattered (env var for the confirm timeout,
// a hardcoded default policy). Persisted as ~/.plexus/settings.json — the same
// local-config home as mcp-servers.json / providers.json. NO secrets live here;
// those stay in the keychain (see KeychainService).
public sealed class AppSettings
{
    // Seconds to wait for a tool-confirmation reply before cancelling the turn.
    public int ConfirmTimeoutSeconds { get; set; } = 120;

    // Global default routing policy applied to NEW graphs. The topbar / per-node
    // PolicyPicker overrides it per turn (settings = default, topbar = override).
    public RoutingPolicy DefaultPolicy { get; set; } = RoutingPolicy.Manual("claude-opus-4-8");
}

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private AppSettings _current;

    public SettingsStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".plexus");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        _current = Load();
    }

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public void Save(AppSettings settings)
    {
        if (settings.ConfirmTimeoutSeconds <= 0)
            settings.ConfirmTimeoutSeconds = 120;
        lock (_gate)
        {
            _current = settings;
            try { File.WriteAllText(_path, PlexusJson.Serialize(settings)); }
            catch { /* best-effort; in-memory value still applies this session */ }
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = PlexusJson.Deserialize<AppSettings>(File.ReadAllText(_path));
                if (loaded is not null)
                {
                    if (loaded.ConfirmTimeoutSeconds <= 0) loaded.ConfirmTimeoutSeconds = 120;
                    loaded.DefaultPolicy ??= RoutingPolicy.Manual("claude-opus-4-8");
                    return loaded;
                }
            }
        }
        catch { /* fall through to defaults */ }

        // First run: seed the confirm timeout from the legacy env var if present, so
        // existing setups keep their value; thereafter the file is the source of truth.
        var settings = new AppSettings();
        if (int.TryParse(Environment.GetEnvironmentVariable("PLEXUS_CONFIRM_TIMEOUT_SECONDS"), out var s) && s > 0)
            settings.ConfirmTimeoutSeconds = s;
        return settings;
    }
}
