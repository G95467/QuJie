using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuJie.App.Backend;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace QuJie.App;

public partial class MainWindow : Window
{
    private const string AppHost = "app.qujie.local";
    private const string AppUrl = "https://app.qujie.local/index.html";
    private static readonly JsonSerializerOptions ReadJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly BridgeHost _host;
    private bool _captureDone;
    private bool _closing;
    private IntPtr _hwnd;

    public MainWindow()
    {
        InitializeComponent();
        _host = new BridgeHost(
            PostJson,
            () => Dispatcher.BeginInvoke(new Action(Close)),
            PickDriverFolder);
        Loaded += Window_Loaded;
        ApplyCaptureWindowSize();
    }

    private string? PickDriverFolder()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择包含 .inf 驱动的文件夹",
                Multiselect = false,
            };
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        }
        catch (Exception ex)
        {
            AppLog.Write("WARN", "打开驱动文件夹选择器失败: " + ex.Message);
            return null;
        }
    }

    private void ApplyCaptureWindowSize()
    {
        try
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("QUJIE_WINDOW_WIDTH"), out var w) && w >= MinWidth)
            {
                Width = Math.Min(w, SystemParameters.WorkArea.Width);
            }
            if (int.TryParse(Environment.GetEnvironmentVariable("QUJIE_WINDOW_HEIGHT"), out var h) && h >= MinHeight)
            {
                Height = Math.Min(h, SystemParameters.WorkArea.Height);
            }
        }
        catch
        {
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StartLoadingAnimation();
        _ = InitializeWebViewAsync();
    }

    private void StartLoadingAnimation()
    {
        var animation = new DoubleAnimation
        {
            From = -64,
            To = 210,
            Duration = TimeSpan.FromMilliseconds(1350),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        LoadingPulseTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, animation);
    }

    private void StopLoadingAnimation()
    {
        LoadingPulseTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        LoadingPulseTransform.X = -64;
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            if (!Directory.Exists(AppPaths.UiDirectory))
            {
                throw new DirectoryNotFoundException("界面资源目录缺失: " + AppPaths.UiDirectory);
            }

            Directory.CreateDirectory(AppPaths.WebViewData);
            var properties = new CoreWebView2CreationProperties
            {
                UserDataFolder = AppPaths.WebViewData,
            };
            var debugPort = Environment.GetEnvironmentVariable("QUJIE_REMOTE_DEBUG");
            if (int.TryParse(debugPort, out var port) && port is > 0 and < 65536)
            {
                properties.AdditionalBrowserArguments = $"--remote-debugging-port={port} --remote-allow-origins=*";
            }
            WebView.CreationProperties = properties;
            await WebView.EnsureCoreWebView2Async();

            var core = WebView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = string.Equals(
                Environment.GetEnvironmentVariable("QUJIE_DEVTOOLS"), "1", StringComparison.Ordinal);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.Settings.IsWebMessageEnabled = true;

            core.SetVirtualHostNameToFolderMapping(
                AppHost,
                AppPaths.UiDirectory,
                CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += Core_WebMessageReceived;
            core.NavigationStarting += Core_NavigationStarting;
            core.NavigationCompleted += Core_NavigationCompleted;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.ProcessFailed += (_, e) =>
            {
                AppLog.Write("ERROR", "WebView2 进程异常: " + e.ProcessFailedKind);
            };

            var theme = Environment.GetEnvironmentVariable("QUJIE_CAPTURE_THEME");
            var tab = Environment.GetEnvironmentVariable("QUJIE_CAPTURE_TAB");
            var capture = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QUJIE_CAPTURE_PATH"));
            var query = new List<string>();
            if (theme is "light" or "dark")
            {
                query.Add("theme=" + theme);
            }
            if (!string.IsNullOrWhiteSpace(tab))
            {
                query.Add("tab=" + Uri.EscapeDataString(tab));
            }
            if (capture)
            {
                query.Add("capture=1");
            }
            if (string.Equals(Environment.GetEnvironmentVariable("QUJIE_CAPTURE_AUTOSCAN"), "1", StringComparison.Ordinal))
            {
                query.Add("autoscan=1");
            }


            var url = AppUrl + (query.Count > 0 ? "?" + string.Join("&", query) : "");
            core.Navigate(url);
            AppLog.Write("INFO", "WebView2 初始化完成，加载界面。");
        }
        catch (Exception ex)
        {
            AppLog.Write("ERROR", "WebView2 初始化失败: " + ex);
            LoadingOverlay.Visibility = Visibility.Visible;
            ShowStartupError(
                "无法启动界面。\n\n请确认系统已安装 Microsoft Edge WebView2 运行时。\n\n" + ex.Message);
        }
    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Host, AppHost, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
        }
    }

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            AppLog.Write("ERROR", "界面加载失败: " + e.WebErrorStatus);
            ShowStartupError("界面加载失败：" + e.WebErrorStatus);
            return;
        }

        StopLoadingAnimation();
        LoadingOverlay.Visibility = Visibility.Collapsed;
        WebView.Visibility = Visibility.Visible;

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QUJIE_CAPTURE_PATH")))
        {
            _ = CaptureAfterDelayAsync();
        }
    }

    private async Task CaptureAfterDelayAsync()
    {
        if (_captureDone)
        {
            return;
        }

        try
        {
            await Task.Delay(GetCaptureDelay());
            _captureDone = true;

            var path = Environment.GetEnvironmentVariable("QUJIE_CAPTURE_PATH");
            if (string.IsNullOrWhiteSpace(path) || WebView.CoreWebView2 is null)
            {
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var stream = File.Create(path);
            await WebView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            AppLog.Write("INFO", "界面截图已保存: " + path);

            if (!string.Equals(Environment.GetEnvironmentVariable("QUJIE_CAPTURE_EXIT"), "0", StringComparison.Ordinal))
            {
                Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("ERROR", "界面截图失败: " + ex);
        }
    }

    private static int GetCaptureDelay()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("QUJIE_CAPTURE_DELAY_MS"), out var delay) &&
            delay >= 500 && delay <= 30000)
        {
            return delay;
        }
        return 3200;
    }

    private async void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var action = root.TryGetProperty("action", out var actionElement) ? actionElement.GetString() : null;
            AppLog.Write("INFO", "WebView message: " + (action ?? "(none)"));
            if (string.IsNullOrWhiteSpace(action))
            {
                return;
            }

            if (action.StartsWith("window.", StringComparison.Ordinal))
            {
                HandleWindowAction(action, root);
                return;
            }

            await _host.HandleAsync(json);
        }
        catch (Exception ex)
        {
            AppLog.Write("ERROR", "处理界面消息失败: " + ex);
        }
    }

    private void HandleWindowAction(string action, JsonElement root)
    {
        switch (action)
        {
            case "window.minimize":
                WindowState = WindowState.Minimized;
                break;
            case "window.toggleMaximize":
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                break;
            case "window.close":
                Close();
                break;
            case "window.drag":
                DragWindow();
                break;
        }

        // Keep the request id so the JS bridge can resolve the promise.
        var id = root.TryGetProperty("id", out var idElement) ? idElement.GetInt64() : 0;
        if (id > 0)
        {
            PostJson(JsonSerializer.Serialize(new
            {
                type = "response",
                id,
                ok = true,
                data = new { state = WindowState.ToString() },
            }));
        }
    }

    private void DragWindow()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }
        ReleaseCapture();
        SendMessage(_hwnd, 0x00A1, (IntPtr)2, IntPtr.Zero);
    }

    private void PostJson(string json)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => PostJson(json)), DispatcherPriority.Background);
            return;
        }

        try
        {
            if (!_closing && WebView.CoreWebView2 != null)
            {
                WebView.CoreWebView2.PostWebMessageAsJson(json);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("WARN", "向界面发送消息失败: " + ex.Message);
        }
    }

    private void ShowStartupError(string message)
    {
        try
        {
            MessageBox.Show(this, message, "驱界", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            var dark = 1;
            DwmSetWindowAttribute(_hwnd, 20, ref dark, sizeof(int));
            var round = 2;
            DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));
        }
        catch
        {
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        PostJson(JsonSerializer.Serialize(new
        {
            type = "event",
            @event = "windowState",
            data = new { state = WindowState.ToString() },
        }));
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }
        _closing = true;
        _host.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
