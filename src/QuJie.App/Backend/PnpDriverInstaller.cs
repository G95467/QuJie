using System.Diagnostics;
using System.IO;
using System.Text;

namespace QuJie.App.Backend;

public sealed class PnpDriverInstaller
{
    public OperationResultDto InstallFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return new OperationResultDto { Ok = false, Message = "驱动文件夹不存在。" };
        }

        List<string> infFiles;
        try
        {
            infFiles = Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories)
                .Take(400)
                .ToList();
        }
        catch (Exception ex)
        {
            return new OperationResultDto { Ok = false, Message = "无法读取驱动文件夹：" + ex.Message };
        }

        if (infFiles.Count == 0)
        {
            return new OperationResultDto
            {
                Ok = false,
                Message = "所选文件夹里没有找到 .inf 驱动安装文件。",
            };
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = "/add-driver \"" + folder + "\" /subdirs /install",
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
                return new OperationResultDto { Ok = false, Message = "无法启动 pnputil 驱动安装器。" };
            }

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(600000))
            {
                try { process.Kill(true); } catch { }
                return new OperationResultDto { Ok = false, Message = "驱动安装超时，请检查设备管理器。" };
            }

            string combined = (output + Environment.NewLine + error).Trim();
            AppLog.Write("INFO", "pnputil add-driver exit=" + process.ExitCode + " output=" + combined);
            bool ok = process.ExitCode == 0 || process.ExitCode == 3010;
            return new OperationResultDto
            {
                Ok = ok,
                Message = ok
                    ? "驱动包已提交安装" + (process.ExitCode == 3010 ? "，需要重启电脑后生效。" : "。") +
                      (string.IsNullOrWhiteSpace(combined) ? string.Empty : "\n" + Tail(combined, 1200))
                    : "驱动安装失败（pnputil 退出代码 " + process.ExitCode + "）。\n" + Tail(combined, 1200),
            };
        }
        catch (Exception ex)
        {
            AppLog.Write("ERROR", "导入驱动失败: " + ex);
            return new OperationResultDto { Ok = false, Message = "导入驱动失败：" + ex.Message };
        }
    }

    public static OperationResultDto OpenWindowsUpdate()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:windowsupdate-optionalupdates",
                UseShellExecute = true,
            });
            return new OperationResultDto { Ok = true, Message = "已打开 Windows 更新可选更新。" };
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:windowsupdate",
                    UseShellExecute = true,
                });
                return new OperationResultDto { Ok = true, Message = "已打开 Windows 更新。" };
            }
            catch (Exception ex)
            {
                return new OperationResultDto { Ok = false, Message = "无法打开 Windows 更新：" + ex.Message };
            }
        }
    }

    private static string Tail(string text, int length)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= length)
        {
            return text;
        }
        return text.Substring(text.Length - length);
    }
}
