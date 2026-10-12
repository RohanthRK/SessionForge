using System.Windows;
using System.Windows.Input;
using SessionForge.App.Services;

namespace SessionForge.App.Dialogs;

/// <summary>Common behaviour for modal dialogs: theming, owner, Esc to cancel.</summary>
public class DialogBase : Window
{
    public DialogBase()
    {
        Style = (Style)Application.Current.FindResource("DialogWindow");
        Owner = FindOwner();
        WindowStartupLocation = Owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ThemeManager.ApplyChrome(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };
    }

    public static Window? FindOwner()
    {
        var app = Application.Current;
        if (app == null) return null;
        var active = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);
        return active ?? (app.MainWindow is { IsVisible: true } mw ? mw : null);
    }

    /// <summary>Runs a function on the UI thread (blocking the caller) — used from background connection threads.</summary>
    public static T OnUi<T>(Func<T> f)
    {
        var d = Application.Current.Dispatcher;
        return d.CheckAccess() ? f() : d.Invoke(f);
    }

    public static void OnUi(Action a)
    {
        var d = Application.Current.Dispatcher;
        if (d.CheckAccess()) a(); else d.Invoke(a);
    }
}
