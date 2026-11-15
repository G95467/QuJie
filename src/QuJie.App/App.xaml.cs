using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using QuJie.App.Backend;

namespace QuJie.App;

public partial class App : Application
{
    private Mutex? _singleInstance;

    public static bool IsAdmin
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\QuJie-DriverCare-4A1C7F3E", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("驱界已经在运行。", "驱界", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppPaths.Ensure();
        AppLog.Write("INFO", "驱界启动，版本 " + AppInfo.Version + "，管理员=" + IsAdmin);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Write("ERROR", "后台异常: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write("ERROR", "后台任务异常: " + args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("ERROR", "界面线程异常: " + e.Exception);
        try
        {
            MessageBox.Show(
                "驱界遇到一个界面异常：\n" + e.Exception.Message + "\n\n日志：" + AppPaths.LogFile,
                "驱界",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
        }
        e.Handled = true;
    }
}

internal static class AppInfo
{
    public const string Name = "驱界";
    public const string EnglishName = "QuJie";
    public const string Version = "1.0.0";
    public const string Tagline = "驱动检测 · 更新 · 设备修复";
}

internal static class AppPaths
{
    public static string BaseDirectory => AppContext.BaseDirectory;

    private static string? _resolvedDataRoot;

    public static string DataRoot
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_resolvedDataRoot))
            {
                return _resolvedDataRoot;
            }

            var custom = Environment.GetEnvironmentVariable("QUJIE_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(custom))
            {
                try
                {
                    _resolvedDataRoot = Path.GetFullPath(custom.Trim().Trim('"'));
                    return _resolvedDataRoot;
                }
                catch
                {
                }
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _resolvedDataRoot = Path.Combine(local, "QuJie");
            return _resolvedDataRoot;
        }
    }

    public static string WebViewData => Path.Combine(DataRoot, "WebView2");
    public static string LogDirectory => Path.Combine(DataRoot, "logs");
    public static string LogFile => Path.Combine(LogDirectory, "app.log");
    public static string StateFile => Path.Combine(DataRoot, "state.json");
    public static string UiDirectory => Path.Combine(BaseDirectory, "ui");
    public static string AssetsDirectory => Path.Combine(BaseDirectory, "assets");

    public static void Ensure()
    {
        var fallbacks = new[]
        {
            Path.Combine(Path.GetTempPath(), "QuJie"),
            Path.Combine(BaseDirectory, "data"),
        };

        try
        {
            EnsureAt(DataRoot);
            return;
        }
        catch
        {
        }

        foreach (var fallback in fallbacks)
        {
            try
            {
                _resolvedDataRoot = fallback;
                EnsureAt(fallback);
                return;
            }
            catch
            {
            }
        }

        throw new InvalidOperationException("无法创建驱界数据目录，请检查用户目录权限。");
    }

    private static void EnsureAt(string root)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "WebView2"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
    }
}

internal static class AppLog
{
    private static readonly object Sync = new object();

    public static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message + Environment.NewLine;
            lock (Sync)
            {
                File.AppendAllText(AppPaths.LogFile, line, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
