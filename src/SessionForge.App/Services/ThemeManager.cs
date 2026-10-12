using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using SessionForge.Core.Models;

namespace SessionForge.App.Services;

/// <summary>Switches between the light and dark resource dictionaries and themes native window chrome.</summary>
public static class ThemeManager
{
    private static ResourceDictionary? _current;

    public static bool IsDark { get; private set; } = true;

    public static event Action? ThemeChanged;

    public static void Apply(AppTheme theme)
    {
        bool dark = theme switch
        {
            AppTheme.Light => false,
            AppTheme.System => SystemUsesDarkTheme(),
            _ => true,
        };
        IsDark = dark;
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/SessionForge;component/Themes/{(dark ? "Dark" : "Light")}.xaml") };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current != null) merged.Remove(_current);
        merged.Insert(0, dict);
        _current = dict;
        foreach (Window w in Application.Current.Windows) ApplyChrome(w);
        ThemeChanged?.Invoke();
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception) { return true; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Uses a dark native title bar on Windows 10 20H1+ / 11 when the dark theme is active.</summary>
    public static void ApplyChrome(Window window)
    {
        void Set()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int)); // pre-20H1 attribute id
        }
        if (new WindowInteropHelper(window).Handle == IntPtr.Zero) window.SourceInitialized += (_, _) => Set();
        else Set();
    }
}
