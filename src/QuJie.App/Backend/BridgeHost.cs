using System.Diagnostics;
using System.Text.Json;

namespace QuJie.App.Backend;

public sealed class BridgeHost : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Action<string> _send;
    private readonly Action _shutdown;
    private readonly Func<string?> _pickFolder;
    private readonly DriverScanner _scanner = new();
    private readonly WindowsUpdateDrivers _updates = new();
    private readonly PnpDriverInstaller _installer = new();
    private readonly StateStore _state = new();
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _updateCts;
    private bool _disposed;

    public BridgeHost(Action<string> send, Action shutdown, Func<string?> pickFolder)
    {
        _send = send;
        _shutdown = shutdown;
        _pickFolder = pickFolder;
    }

    public async Task HandleAsync(string json)
    {
        long id = 0;
        string? action = null;
        JsonElement payload = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idElement))
            {
                id = idElement.TryGetInt64(out var parsedId) ? parsedId : 0;
            }
            action = root.TryGetProperty("action", out var actionElement) ? actionElement.GetString() : null;
            if (root.TryGetProperty("payload", out var payloadElement))
            {
                payload = payloadElement.Clone();
            }

            object? data = await DispatchAsync(action, payload);
            SendResponse(id, true, data, null);
        }
        catch (Exception ex)
        {
            AppLog.Write("ERROR", "处理操作 " + action + " 失败: " + ex);
            SendResponse(id, false, null, ex.Message);
        }
    }

    private async Task<object?> DispatchAsync(string? action, JsonElement payload)
    {
        switch (action)
        {
            case "getInitial":
                return new InitialDataDto
                {
                    System = DriverScanner.GetSystemInfo(),
                    Settings = _state.Settings,
                    IsAdmin = App.IsAdmin,
                };

            case "getSystemInfo":
                return DriverScanner.GetSystemInfo();

            case "scanDrivers":
            {
                _scanCts?.Cancel();
                _scanCts?.Dispose();
                _scanCts = new CancellationTokenSource();
                var token = _scanCts.Token;
                var settings = _state.Settings;
                return await Task.Run(() => _scanner.Scan(
                    settings,
                    p => SendEvent("scanProgress", p),
                    token), token);
            }

            case "cancelScan":
                _scanCts?.Cancel();
                return new { ok = true };

            case "searchDriverUpdates":
            {
                _updateCts?.Cancel();
                _updateCts?.Dispose();
                _updateCts = new CancellationTokenSource();
                var token = _updateCts.Token;
                return await Task.Run(() => _updates.Search(
                    p => SendEvent("updateProgress", p),
                    token), token);
            }

            case "installDriverUpdates":
            {
                if (!App.IsAdmin)
                {
                    return new InstallResultDto
                    {
                        Ok = false,
                        Message = "安装驱动需要管理员权限，请重新启动驱界并在 UAC 中允许。",
                    };
                }

                var ids = ReadStringArray(payload, "ids");
                if (ids.Count == 0)
                {
                    return new InstallResultDto { Ok = false, Message = "没有选择驱动更新。" };
                }

                var token = _updateCts?.Token ?? CancellationToken.None;
                return await Task.Run(() => _updates.Install(
                    ids,
                    p => SendEvent("installProgress", p),
                    token), CancellationToken.None);
            }

            case "installDriverFolder":
            {
                if (!App.IsAdmin)
                {
                    return new OperationResultDto
                    {
                        Ok = false,
                        Message = "安装驱动需要管理员权限，请重新启动驱界并在 UAC 中允许。",
                    };
                }

                string? folder = GetString(payload, "folder");
                if (string.IsNullOrWhiteSpace(folder))
                {
                    return new OperationResultDto { Ok = false, Message = "没有选择驱动文件夹。" };
                }

                SendEvent("folderInstallProgress", new InstallProgressDto
                {
                    Percent = 10,
                    Stage = "install",
                    Message = "正在调用 pnputil 安装驱动包…",
                });

                var result = await Task.Run(() => _installer.InstallFolder(folder));
                SendEvent("folderInstallProgress", new InstallProgressDto
                {
                    Percent = 100,
                    Stage = result.Ok ? "done" : "error",
                    Message = result.Message,
                });
                return result;
            }

            case "pickDriverFolder":
            {
                var path = _pickFolder();
                return new { path = path ?? string.Empty };
            }

            case "openWindowsUpdate":
                return PnpDriverInstaller.OpenWindowsUpdate();

            case "openDataFolder":
                OpenPath(AppPaths.DataRoot, true);
                return new { ok = true };

            case "openLog":
                OpenPath(AppPaths.LogFile, false);
                return new { ok = true };

            case "saveSettings":
            {
                var settings = payload.Deserialize<SettingsDto>(JsonOptions) ?? new SettingsDto();
                settings.Normalize();
                _state.SaveSettings(settings);
                return new { ok = true, settings };
            }

            case "openDeviceManager":
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "devmgmt.msc",
                        UseShellExecute = true,
                    });
                    return new { ok = true };
                }
                catch (Exception ex)
                {
                    return new { ok = false, error = ex.Message };
                }

            case "close":
                _shutdown();
                return new { ok = true };

            case "debugLog":
                AppLog.Write("WARN", "UI " + (GetString(payload, "message") ?? ""));
                return new { ok = true };

            default:
                throw new InvalidOperationException("未知操作：" + action);
        }
    }

    private static void OpenPath(string path, bool directory)
    {
        try
        {
            if (directory)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + path + "\"",
                    UseShellExecute = true,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = "\"" + path + "\"",
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("WARN", "打开路径失败: " + ex.Message);
        }
    }

    private void SendEvent(string name, object? data)
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            _send(JsonSerializer.Serialize(new
            {
                type = "event",
                @event = name,
                data,
            }, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Write("WARN", "发送事件失败: " + ex.Message);
        }
    }

    private void SendResponse(long id, bool ok, object? data, string? error)
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            _send(JsonSerializer.Serialize(new
            {
                type = "response",
                id,
                ok,
                data,
                error,
            }, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Write("ERROR", "发送响应失败: " + ex.Message);
        }
    }

    private static string? GetString(JsonElement payload, string name)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(name, out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }
        return null;
    }

    private static List<string> ReadStringArray(JsonElement payload, string name)
    {
        var result = new List<string>();
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(name, out var array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result.Add(value);
                    }
                }
            }
        }
        return result;
    }

    public void Dispose()
    {
        _disposed = true;
        try { _scanCts?.Cancel(); } catch { }
        try { _updateCts?.Cancel(); } catch { }
        _scanCts?.Dispose();
        _updateCts?.Dispose();
    }
}
