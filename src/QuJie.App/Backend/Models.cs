namespace QuJie.App.Backend;

public sealed class SettingsDto
{
    public string Theme { get; set; } = "dark";
    public bool AutoScan { get; set; } = true;
    public int OldDriverYears { get; set; } = 5;

    public void Normalize()
    {
        if (Theme != "light" && Theme != "dark" && Theme != "system")
        {
            Theme = "dark";
        }
        if (OldDriverYears < 2 || OldDriverYears > 12)
        {
            OldDriverYears = 5;
        }
    }
}

public sealed class SystemInfoDto
{
    public string ComputerName { get; set; } = string.Empty;
    public string OsName { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string OsBuild { get; set; } = string.Empty;
    public string CpuName { get; set; } = string.Empty;
    public bool Is64Bit { get; set; }
    public bool IsAdmin { get; set; }
    public string AppVersion { get; set; } = string.Empty;
    public string DataDirectory { get; set; } = string.Empty;
    public string LogFile { get; set; } = string.Empty;
}

public sealed class DriverDeviceDto
{
    public string Id { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string ClassKey { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Status { get; set; } = "ok";
    public string StatusText { get; set; } = "正常";
    public int ProblemCode { get; set; }
    public string ProblemText { get; set; } = string.Empty;
    public string DriverVersion { get; set; } = string.Empty;
    public string DriverDate { get; set; } = string.Empty;
    public string DriverProvider { get; set; } = string.Empty;
    public string HardwareId { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public int AgeYears { get; set; }
    public bool IsMissing { get; set; }
    public bool IsOld { get; set; }
    public bool HasProblem { get; set; }
    public bool IsVirtual { get; set; }
}

public sealed class DriverScanResultDto
{
    public DateTime ScannedAt { get; set; }
    public int TotalDevices { get; set; }
    public int MissingCount { get; set; }
    public int ProblemCount { get; set; }
    public int OldCount { get; set; }
    public int OkCount { get; set; }
    public int UnknownCount { get; set; }
    public int HealthScore { get; set; }
    public string HealthText { get; set; } = string.Empty;
    public long ScanMilliseconds { get; set; }
    public List<DriverDeviceDto> Devices { get; set; } = new();
    public string RescanMessage { get; set; } = string.Empty;
}

public sealed class DriverUpdateDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string DriverClass { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string VersionDate { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool IsDownloaded { get; set; }
    public bool RebootRequired { get; set; }
}

public sealed class UpdateScanResultDto
{
    public DateTime ScannedAt { get; set; }
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<DriverUpdateDto> Updates { get; set; } = new();
}

public sealed class InstallResultDto
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
    public int Installed { get; set; }
    public int Failed { get; set; }
    public bool RebootRequired { get; set; }
}

public sealed class ScanProgressDto
{
    public int Percent { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class InstallProgressDto
{
    public int Percent { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class InitialDataDto
{
    public SystemInfoDto System { get; set; } = new();
    public SettingsDto Settings { get; set; } = new();
    public bool IsAdmin { get; set; }
}

public sealed class OperationResultDto
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
}
