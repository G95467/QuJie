using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace QuJie.App.Backend;

internal static class NativeDeviceApi
{
    public const uint DIGCF_PRESENT = 0x00000002;
    public const uint DIGCF_ALLCLASSES = 0x00000004;

    public const uint SPDRP_DEVICEDESC = 0x00000000;
    public const uint SPDRP_HARDWAREID = 0x00000001;
    public const uint SPDRP_SERVICE = 0x00000004;
    public const uint SPDRP_CLASS = 0x00000007;
    public const uint SPDRP_CLASSGUID = 0x00000008;
    public const uint SPDRP_DRIVER = 0x00000009;
    public const uint SPDRP_MFG = 0x0000000B;
    public const uint SPDRP_FRIENDLYNAME = 0x0000000C;
    public const uint SPDRP_LOCATION_INFORMATION = 0x0000000D;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(
        IntPtr classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        StringBuilder deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_DevNode_Status(
        out uint status,
        out uint problemNumber,
        uint devInst,
        uint flags);

    public static string GetPropertyString(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA data,
        uint property)
    {
        uint type;
        uint required;
        var buffer = new byte[4096];
        if (!SetupDiGetDeviceRegistryPropertyW(
                deviceInfoSet,
                ref data,
                property,
                out type,
                buffer,
                (uint)buffer.Length,
                out required))
        {
            return string.Empty;
        }

        if (required <= 2)
        {
            return string.Empty;
        }

        var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min((uint)buffer.Length, required));
        return text.TrimEnd('\0').Replace("\0", "; ").Trim();
    }

    public static string GetInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA data)
    {
        var builder = new StringBuilder(1024);
        uint required;
        if (SetupDiGetDeviceInstanceIdW(
                deviceInfoSet,
                ref data,
                builder,
                (uint)builder.Capacity,
                out required))
        {
            return builder.ToString();
        }
        return string.Empty;
    }

    public static (uint Status, uint Problem) GetStatus(uint devInst)
    {
        try
        {
            if (CM_Get_DevNode_Status(out var status, out var problem, devInst, 0) == 0)
            {
                return (status, problem);
            }
        }
        catch
        {
        }
        return (0, 0);
    }

    public static DriverRegistryInfo ReadDriverInfo(string? driverKey)
    {
        var info = new DriverRegistryInfo();
        if (string.IsNullOrWhiteSpace(driverKey))
        {
            return info;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\" + driverKey.TrimStart('\\'));
            if (key == null)
            {
                return info;
            }

            info.Version = key.GetValue("DriverVersion") as string ?? string.Empty;
            info.DateText = key.GetValue("DriverDate") as string ?? string.Empty;
            info.Provider = key.GetValue("ProviderName") as string ?? string.Empty;
            info.Description = key.GetValue("DriverDesc") as string ?? string.Empty;
            info.Date = ParseDriverDate(info.DateText);
        }
        catch
        {
        }

        return info;
    }

    public static DateTime? ParseDriverDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] formats =
        {
            "M-d-yyyy", "MM-dd-yyyy", "M/d/yyyy", "MM/dd/yyyy",
            "yyyy-MM-dd", "yyyy/MM/dd", "yyyyMMdd"
        };

        if (DateTime.TryParseExact(
                value.Trim(),
                formats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var exact))
        {
            return exact;
        }

        if (DateTime.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    public static string ProblemText(uint problem)
    {
        return problem switch
        {
            1 => "设备未配置",
            2 => "设备加载器未找到",
            3 => "设备驱动未安装",
            10 => "设备无法启动",
            12 => "资源不足",
            14 => "需要重启电脑",
            18 => "需要重新安装驱动",
            19 => "注册表信息不完整",
            21 => "设备正在移除",
            22 => "设备已禁用",
            24 => "设备不存在",
            28 => "缺少驱动程序",
            31 => "设备工作不正常",
            32 => "找不到驱动",
            37 => "驱动初始化失败",
            39 => "驱动加载失败",
            43 => "设备已被系统停止",
            48 => "驱动被系统阻止",
            _ => problem == 0 ? string.Empty : "设备异常（代码 " + problem + "）",
        };
    }

    public static string StatusText(uint problem)
    {
        if (problem == 28)
        {
            return "缺少驱动";
        }
        if (problem != 0)
        {
            return "驱动异常";
        }
        return "正常";
    }

    public static string ClassDisplayName(string? className)
    {
        return (className ?? string.Empty) switch
        {
            "Display" => "显示适配器",
            "Net" => "网络适配器",
            "MEDIA" => "音频设备",
            "HDC" => "存储控制器",
            "DiskDrive" => "磁盘驱动器",
            "USB" => "USB 设备",
            "HIDClass" => "人体学输入设备",
            "Keyboard" => "键盘",
            "Mouse" => "鼠标",
            "Bluetooth" => "蓝牙",
            "Printer" => "打印机",
            "System" => "系统设备",
            "Firmware" => "固件",
            "Image" => "图像设备",
            "Camera" => "摄像头",
            "Monitor" => "显示器",
            "Battery" => "电池",
            "Processor" => "处理器",
            "SoftwareDevice" => "软件设备",
            "SecurityDevices" => "安全设备",
            "SmartCardReader" => "智能卡读取器",
            "WPD" => "便携设备",
            "XnaComposite" => "游戏设备",
            _ => string.IsNullOrWhiteSpace(className) ? "其他设备" : className!,
        };
    }
}

internal sealed class DriverRegistryInfo
{
    public string Version { get; set; } = string.Empty;
    public string DateText { get; set; } = string.Empty;
    public DateTime? Date { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
