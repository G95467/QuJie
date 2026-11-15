using System.Diagnostics;
using System.Text;

namespace QuJie.App.Backend;

public sealed class DriverScanner
{
    private static readonly HashSet<string> OldEligibleClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Display", "Net", "MEDIA", "Bluetooth", "Camera", "Image", "Printer",
        "HDC", "USB", "HIDClass", "Keyboard", "Mouse", "Monitor", "Battery",
        "SCSIAdapter", "DiskDrive", "SmartCardReader", "WPD",
    };

    public DriverScanResultDto Scan(
        SettingsDto settings,
        Action<ScanProgressDto>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        var result = new DriverScanResultDto { ScannedAt = DateTime.Now };
        var devices = new List<DriverDeviceDto>();

        progress?.Invoke(new ScanProgressDto { Percent = 4, Message = "正在重新扫描硬件…" });
        var rescan = PnpUtil.ScanDevices();
        result.RescanMessage = rescan.Message;

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke(new ScanProgressDto { Percent = 14, Message = "正在读取设备与驱动信息…" });

        IntPtr set = IntPtr.Zero;
        try
        {
            set = NativeDeviceApi.SetupDiGetClassDevsW(
                IntPtr.Zero,
                null,
                IntPtr.Zero,
                NativeDeviceApi.DIGCF_PRESENT | NativeDeviceApi.DIGCF_ALLCLASSES);

            if (set == IntPtr.Zero || set == new IntPtr(-1))
            {
                throw new InvalidOperationException("无法打开本机设备信息。");
            }

            uint index = 0;
            while (devices.Count < 420)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var data = new NativeDeviceApi.SP_DEVINFO_DATA
                {
                    cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeDeviceApi.SP_DEVINFO_DATA>(),
                };

                if (!NativeDeviceApi.SetupDiEnumDeviceInfo(set, index++, ref data))
                {
                    break;
                }

                var instanceId = NativeDeviceApi.GetInstanceId(set, ref data);
                if (string.IsNullOrWhiteSpace(instanceId))
                {
                    continue;
                }

                var friendly = NativeDeviceApi.GetPropertyString(set, ref data, NativeDeviceApi.SPDRP_FRIENDLYNAME);
                var description = NativeDeviceApi.GetPropertyString(set, ref data, NativeDeviceApi.SPDRP_DEVICEDESC);
                var className = NativeDeviceApi.GetPropertyString(set, ref data, NativeDeviceApi.SPDRP_CLASS);
                var manufacturer = NativeDeviceApi.GetPropertyString(set, ref data, NativeDeviceApi.SPDRP_MFG);
                var driverKey = NativeDeviceApi.GetPropertyString(set, ref data, NativeDeviceApi.SPDRP_DRIVER);
                var hardwareId = NativeDeviceApi.GetPropertyString(set, ref data, NativeDeviceApi.SPDRP_HARDWAREID);
                var location = NativeDeviceApi.GetPropertyString(set, ref data, NativeDeviceApi.SPDRP_LOCATION_INFORMATION);

                var driver = NativeDeviceApi.ReadDriverInfo(driverKey);
                var status = NativeDeviceApi.GetStatus(data.DevInst);
                var problem = status.Problem;

                var name = !string.IsNullOrWhiteSpace(friendly)
                    ? friendly
                    : (!string.IsNullOrWhiteSpace(description) ? description : driver.Description);
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = NativeDeviceApi.ClassDisplayName(className) + "设备";
                }

                var statusKey = "ok";
                var statusText = "正常";
                if (problem == 28)
                {
                    statusKey = "missing";
                    statusText = "缺少驱动";
                }
                else if (problem != 0)
                {
                    statusKey = "problem";
                    statusText = "驱动异常";
                }
                else if (string.IsNullOrWhiteSpace(driver.Version) && string.IsNullOrWhiteSpace(driver.DateText))
                {
                    statusKey = "unknown";
                    statusText = "版本未知";
                }
                else if (!instanceId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) &&
                         OldEligibleClasses.Contains(className) &&
                         !IsMicrosoftDriver(driver.Provider) &&
                         driver.Date.HasValue &&
                         driver.Date.Value < DateTime.Now.AddYears(-Math.Clamp(settings.OldDriverYears, 2, 12)))
                {
                    statusKey = "old";
                    statusText = "驱动较旧";
                }

                int ageYears = 0;
                if (driver.Date.HasValue)
                {
                    ageYears = Math.Max(0, (int)((DateTime.Now - driver.Date.Value).TotalDays / 365.25));
                }

                var item = new DriverDeviceDto
                {
                    Id = instanceId,
                    InstanceId = instanceId,
                    Name = name,
                    ClassName = NativeDeviceApi.ClassDisplayName(className),
                    ClassKey = className,
                    Manufacturer = string.IsNullOrWhiteSpace(manufacturer) ? driver.Provider : manufacturer,
                    Status = statusKey,
                    StatusText = statusText,
                    ProblemCode = (int)problem,
                    ProblemText = NativeDeviceApi.ProblemText(problem),
                    DriverVersion = driver.Version,
                    DriverDate = driver.Date?.ToString("yyyy-MM-dd") ?? string.Empty,
                    DriverProvider = driver.Provider,
                    HardwareId = hardwareId,
                    Location = location,
                    AgeYears = ageYears,
                    IsMissing = problem == 28,
                    HasProblem = problem != 0 && problem != 28,
                    IsOld = statusKey == "old",
                    IsVirtual = instanceId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(className, "SoftwareDevice", StringComparison.OrdinalIgnoreCase),
                };

                devices.Add(item);

                if (devices.Count % 30 == 0)
                {
                    int percent = Math.Min(72, 14 + devices.Count / 5);
                    progress?.Invoke(new ScanProgressDto
                    {
                        Percent = percent,
                        Message = "已读取 " + devices.Count + " 个设备…",
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Write("ERROR", "设备枚举失败: " + ex);
            throw new InvalidOperationException("读取设备信息失败：" + ex.Message, ex);
        }
        finally
        {
            if (set != IntPtr.Zero && set != new IntPtr(-1))
            {
                NativeDeviceApi.SetupDiDestroyDeviceInfoList(set);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke(new ScanProgressDto { Percent = 86, Message = "正在整理驱动状态…" });

        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["missing"] = 0,
            ["problem"] = 1,
            ["old"] = 2,
            ["unknown"] = 3,
            ["ok"] = 4,
        };

        devices = devices
            .GroupBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(d => order.TryGetValue(d.Status, out var rank) ? rank : 9)
            .ThenBy(d => d.Name, StringComparer.CurrentCulture)
            .ToList();

        result.Devices = devices;
        result.TotalDevices = devices.Count;
        result.MissingCount = devices.Count(d => d.IsMissing);
        result.ProblemCount = devices.Count(d => d.HasProblem);
        result.OldCount = devices.Count(d => d.IsOld);
        result.OkCount = devices.Count(d => d.Status == "ok");
        result.UnknownCount = devices.Count(d => d.Status == "unknown");

        int score = 100
            - result.MissingCount * 18
            - result.ProblemCount * 9
            - Math.Min(result.OldCount, 15) * 2
            - Math.Min(result.UnknownCount, 8);
        result.HealthScore = Math.Clamp(score, 28, 100);
        result.HealthText = result.HealthScore >= 90
            ? "状态优秀"
            : result.HealthScore >= 75
                ? "整体良好"
                : result.HealthScore >= 55
                    ? "需要维护"
                    : "建议修复";

        result.ScanMilliseconds = started.ElapsedMilliseconds;
        progress?.Invoke(new ScanProgressDto { Percent = 100, Message = "设备扫描完成" });
        AppLog.Write("INFO",
            "驱动扫描完成: devices=" + result.TotalDevices +
            " missing=" + result.MissingCount +
            " problem=" + result.ProblemCount +
            " old=" + result.OldCount +
            " unknown=" + result.UnknownCount);
        return result;
    }

    private static bool IsMicrosoftDriver(string? provider)
    {
        return !string.IsNullOrWhiteSpace(provider) &&
               provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
    }

    public static SystemInfoDto GetSystemInfo()
    {
        var os = Environment.OSVersion;
        var cpu = string.Empty;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpu = key?.GetValue("ProcessorNameString") as string ?? string.Empty;
        }
        catch
        {
        }

        return new SystemInfoDto
        {
            ComputerName = Environment.MachineName,
            OsName = "Windows",
            OsVersion = os.Version.ToString(),
            OsBuild = os.Version.Build.ToString(),
            CpuName = cpu.Trim(),
            Is64Bit = Environment.Is64BitOperatingSystem,
            IsAdmin = App.IsAdmin,
            AppVersion = AppInfo.Version,
            DataDirectory = AppPaths.DataRoot,
            LogFile = AppPaths.LogFile,
        };
    }
}

internal static class PnpUtil
{
    public static (bool Ok, string Message) ScanDevices()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = "/scan-devices",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var process = Process.Start(psi);
            if (process == null)
            {
                return (false, "无法启动 PnP 扫描。");
            }
            if (!process.WaitForExit(20000))
            {
                try { process.Kill(true); } catch { }
                return (false, "PnP 扫描超时。");
            }
            var output = (process.StandardOutput.ReadToEnd() + " " + process.StandardError.ReadToEnd()).Trim();
            return (process.ExitCode == 0, string.IsNullOrWhiteSpace(output)
                ? (process.ExitCode == 0 ? "硬件扫描完成" : "硬件扫描失败")
                : output);
        }
        catch (Exception ex)
        {
            AppLog.Write("WARN", "pnputil /scan-devices 失败: " + ex.Message);
            return (false, "无法执行硬件扫描：" + ex.Message);
        }
    }
}
