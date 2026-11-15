using System.Runtime.InteropServices;

namespace QuJie.App.Backend;

public sealed class WindowsUpdateDrivers
{
    private const string DriverQuery = "IsInstalled=0 and Type='Driver' and IsHidden=0";

    public UpdateScanResultDto Search(
        Action<ScanProgressDto>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new UpdateScanResultDto
        {
            ScannedAt = DateTime.Now,
            Ok = false,
            Message = string.Empty,
        };

        try
        {
            progress?.Invoke(new ScanProgressDto { Percent = 12, Message = "正在连接 Windows Update…" });

            dynamic session = CreateComObject("Microsoft.Update.Session");
            dynamic searcher = session.CreateUpdateSearcher();
            progress?.Invoke(new ScanProgressDto { Percent = 38, Message = "正在查询驱动更新…" });

            dynamic searchResult = searcher.Search(DriverQuery);
            cancellationToken.ThrowIfCancellationRequested();

            dynamic updates = searchResult.Updates;
            int count = SafeInt(() => updates.Count);
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                dynamic update = updates.Item(i);
                string id = SafeString(() => update.Identity.UpdateID);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var dto = new DriverUpdateDto
                {
                    Id = id,
                    Title = SafeString(() => update.Title),
                    DriverClass = SafeString(() => update.DriverClass),
                    Manufacturer = SafeString(() => update.DriverManufacturer),
                    Provider = SafeString(() => update.DriverProvider),
                    VersionDate = SafeDate(() => update.DriverVerDate),
                    Description = SafeString(() => update.Description),
                    SizeBytes = SafeLong(() => update.MaxDownloadSize),
                    IsDownloaded = SafeBool(() => update.IsDownloaded),
                    RebootRequired = SafeBool(() => update.RebootRequired),
                };

                if (string.IsNullOrWhiteSpace(dto.Title))
                {
                    dto.Title = "驱动更新 " + (i + 1);
                }
                result.Updates.Add(dto);

                int percent = 42 + (int)((i + 1) / (double)Math.Max(1, count) * 46);
                progress?.Invoke(new ScanProgressDto
                {
                    Percent = Math.Clamp(percent, 42, 90),
                    Message = "已发现 " + result.Updates.Count + " 个驱动更新…",
                });
            }

            result.Updates = result.Updates
                .OrderBy(u => u.Title, StringComparer.CurrentCulture)
                .ToList();
            result.Ok = true;
            result.Message = result.Updates.Count == 0
                ? "Windows Update 当前没有提供可用的驱动更新。"
                : "Windows Update 提供 " + result.Updates.Count + " 个驱动更新。";
            progress?.Invoke(new ScanProgressDto { Percent = 100, Message = "驱动更新查询完成" });
            AppLog.Write("INFO", "Windows Update 驱动查询完成: " + result.Updates.Count);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Ok = false;
            result.Message = FriendlyError(ex);
            AppLog.Write("WARN", "Windows Update 驱动查询失败: " + ex);
            progress?.Invoke(new ScanProgressDto { Percent = 100, Message = "查询失败" });
            return result;
        }
    }

    public InstallResultDto Install(
        IReadOnlyCollection<string> updateIds,
        Action<InstallProgressDto>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new InstallResultDto();
        if (updateIds == null || updateIds.Count == 0)
        {
            result.Message = "没有选择要安装的驱动更新。";
            return result;
        }

        try
        {
            progress?.Invoke(new InstallProgressDto
            {
                Percent = 5, Stage = "prepare", Message = "正在重新读取 Windows Update 更新…"
            });

            dynamic session = CreateComObject("Microsoft.Update.Session");
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic searchResult = searcher.Search(DriverQuery);
            dynamic updates = searchResult.Updates;
            int count = SafeInt(() => updates.Count);
            var selected = new HashSet<string>(updateIds, StringComparer.OrdinalIgnoreCase);

            dynamic collection = CreateComObject("Microsoft.Update.UpdateColl");
            int found = 0;
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                dynamic update = updates.Item(i);
                string id = SafeString(() => update.Identity.UpdateID);
                if (string.IsNullOrWhiteSpace(id) || !selected.Contains(id))
                {
                    continue;
                }

                try
                {
                    if (!SafeBool(() => update.EulaAccepted))
                    {
                        update.AcceptEula();
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("WARN", "接受驱动更新许可失败: " + ex.Message);
                }

                collection.Add(update);
                found++;
            }

            if (found == 0)
            {
                result.Message = "选择的驱动更新已不可用，请重新检查更新。";
                return result;
            }

            progress?.Invoke(new InstallProgressDto
            {
                Percent = 16, Stage = "download", Message = "正在下载 " + found + " 个驱动更新，可能需要几分钟…"
            });

            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = collection;
            dynamic downloadResult = downloader.Download();
            int downloadCode = SafeInt(() => downloadResult.ResultCode);
            if (downloadCode != 2 && downloadCode != 3)
            {
                result.Message = "驱动下载失败（代码 " + downloadCode + "）。请检查网络和 Windows Update 服务。";
                return result;
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(new InstallProgressDto
            {
                Percent = 58, Stage = "install", Message = "正在安装驱动更新，请勿关闭电脑…"
            });

            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = collection;
            dynamic installResult = installer.Install();
            int code = SafeInt(() => installResult.ResultCode);
            result.RebootRequired = SafeBool(() => installResult.RebootRequired);

            int installed = 0;
            int failed = 0;
            try
            {
                for (int i = 0; i < found; i++)
                {
                    try
                    {
                        dynamic one = installResult.GetUpdateResult(i);
                        int oneCode = SafeInt(() => one.ResultCode);
                        if (oneCode == 2 || oneCode == 3)
                        {
                            installed++;
                        }
                        else
                        {
                            failed++;
                        }
                    }
                    catch
                    {
                        failed++;
                    }
                }
            }
            catch
            {
                if (code == 2 || code == 3)
                {
                    installed = found;
                }
                else
                {
                    failed = found;
                }
            }

            result.Installed = installed;
            result.Failed = failed;
            result.Ok = installed > 0;
            result.Message = result.Ok
                ? "已安装 " + installed + " 个驱动更新" +
                  (failed > 0 ? "，" + failed + " 个未完成" : string.Empty) +
                  (result.RebootRequired ? "。需要重启电脑后生效。" : "。")
                : "Windows Update 没有完成驱动安装（代码 " + code + "）。";

            progress?.Invoke(new InstallProgressDto
            {
                Percent = 100,
                Stage = "done",
                Message = result.Ok ? "驱动更新已提交安装" : "驱动安装未完成",
            });
            AppLog.Write("INFO", "驱动安装结果: " + result.Message);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Ok = false;
            result.Message = FriendlyError(ex);
            AppLog.Write("ERROR", "驱动安装失败: " + ex);
            progress?.Invoke(new InstallProgressDto
            {
                Percent = 100, Stage = "error", Message = "驱动安装失败"
            });
            return result;
        }
    }

    private static dynamic CreateComObject(string progId)
    {
        var type = Type.GetTypeFromProgID(progId);
        if (type == null)
        {
            throw new InvalidOperationException("系统缺少 Windows Update 组件（" + progId + "）。");
        }
        var instance = Activator.CreateInstance(type);
        if (instance == null)
        {
            throw new InvalidOperationException("无法启动 Windows Update 组件（" + progId + "）。");
        }
        return instance;
    }

    private static string FriendlyError(Exception ex)
    {
        string message = ex.Message;
        int code = ex.HResult;
        if (ex is UnauthorizedAccessException || code == unchecked((int)0x80070005))
        {
            return "Windows Update 查询需要管理员权限。请重新启动驱界，并在 UAC 弹窗中点击“是”。";
        }
        if (code == unchecked((int)0x8024402C) || message.Contains("0x8024402C", StringComparison.OrdinalIgnoreCase))
        {
            return "无法连接 Windows Update，请检查网络连接。";
        }
        if (code == unchecked((int)0x80240034) || message.Contains("0x80240034", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows Update 正在执行其他任务，请稍后重试。";
        }
        if (code == unchecked((int)0x80248007) || message.Contains("0x80248007", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows Update 数据库未准备好，请稍后重试。";
        }
        if (message.Contains("service", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("服务", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows Update 服务不可用，请确认服务已启动后重试。\n" + message;
        }
        return "Windows Update 操作失败：" + message;
    }

    private static string SafeString(Func<object?> getter)
    {
        try { return Convert.ToString(getter()) ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static int SafeInt(Func<object?> getter)
    {
        try { return Convert.ToInt32(getter()); }
        catch { return 0; }
    }

    private static long SafeLong(Func<object?> getter)
    {
        try { return Convert.ToInt64(getter()); }
        catch { return 0; }
    }

    private static bool SafeBool(Func<object?> getter)
    {
        try { return Convert.ToBoolean(getter()); }
        catch { return false; }
    }

    private static string SafeDate(Func<object?> getter)
    {
        try
        {
            var value = getter();
            return value is DateTime date ? date.ToString("yyyy-MM-dd") : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
