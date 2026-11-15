using System.IO;
using System.Text.Json;

namespace QuJie.App.Backend;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly string _path;

    public StateStore()
    {
        _path = AppPaths.StateFile;
        Settings = Load();
    }

    public SettingsDto Settings { get; private set; }

    public void SaveSettings(SettingsDto settings)
    {
        settings.Normalize();
        lock (_sync)
        {
            Settings = settings;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(new StateDto { Settings = settings }, JsonOptions));
            }
            catch (Exception ex)
            {
                AppLog.Write("WARN", "保存设置失败: " + ex.Message);
            }
        }
    }

    private SettingsDto Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var state = JsonSerializer.Deserialize<StateDto>(
                    File.ReadAllText(_path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (state?.Settings != null)
                {
                    state.Settings.Normalize();
                    return state.Settings;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("WARN", "读取设置失败: " + ex.Message);
        }
        return new SettingsDto();
    }

    private sealed class StateDto
    {
        public SettingsDto Settings { get; set; } = new();
    }
}
