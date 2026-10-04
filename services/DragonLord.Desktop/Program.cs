using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DragonLord.Desktop;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new Application();
        try
        {
            var settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "desktop-settings.json"))) ?? throw new InvalidDataException("Desktop settings are missing.");
            if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out var origin) || origin.Scheme != "https" || !string.IsNullOrEmpty(origin.UserInfo))
                throw new InvalidDataException("Configure the desktop app with an HTTPS website URL.");
            app.Run(new WorkspaceWindow(settings, origin));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Dragon Lord Esports Gaming", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public sealed record DesktopSettings(string Name, string Url);

public sealed class WorkspaceWindow : Window
{
    private readonly WebView2 browser = new();
    private readonly TextBlock status = new() { Text = "Connecting…", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.LightGray, Margin = new Thickness(12, 0, 12, 0) };
    private readonly Uri origin;

    public WorkspaceWindow(DesktopSettings settings, Uri origin)
    {
        this.origin = origin;
        Title = settings.Name;
        Width = 1360; Height = 860; MinWidth = 650; MinHeight = 500;
        Background = new SolidColorBrush(Color.FromRgb(20, 21, 25));
        var layout = new DockPanel();
        var toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(12, 8, 12, 8) };
        var refresh = new Button { Content = "Reload", Padding = new Thickness(14, 5, 14, 5), ToolTip = "Reload the café workspace" };
        refresh.Click += (_, _) => { if (browser.CoreWebView2 is not null) browser.Reload(); };
        DockPanel.SetDock(refresh, Dock.Right); toolbar.Children.Add(refresh);
        toolbar.Children.Add(status);
        DockPanel.SetDock(toolbar, Dock.Top); layout.Children.Add(toolbar);
        layout.Children.Add(browser); Content = layout;
        Loaded += async (_, _) => await Initialize();
        Closed += (_, _) => browser.Dispose();
    }

    private async Task Initialize()
    {
        try
        {
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DragonLord", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            await browser.EnsureCoreWebView2Async(environment);
            browser.CoreWebView2.Settings.IsWebMessageEnabled = false;
            browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            browser.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            browser.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
            browser.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var next) || next.Scheme != "https" || next.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority))
                { e.Cancel = true; OpenExternal(e.Uri); }
                else status.Text = "Connecting…";
            };
            browser.CoreWebView2.NavigationCompleted += (_, e) => status.Text = e.IsSuccess ? "Connected · Desktop 0.1.0" : "Connection failed · Check internet and reload";
            browser.Source = origin;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            status.Text = "Microsoft Edge WebView2 Runtime is required.";
            if (MessageBox.Show("Install Microsoft Edge WebView2 Runtime from Microsoft, then reopen this app. Open the official download page?", Title, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                OpenExternal("https://developer.microsoft.com/microsoft-edge/webview2/");
        }
        catch (Exception)
        {
            status.Text = "Unable to open the workspace. Close the app and try again.";
        }
    }

    private static void OpenExternal(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo))
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }
}
