using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SessionForge.App.Dialogs;
using SessionForge.App.Services;
using SessionForge.App.Views;
using SessionForge.Core.Models;

namespace SessionForge.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log.Error("Unhandled exception", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Log.Error("Unobserved task exception", ev.Exception);
            ev.SetObserved();
        };

        try
        {
            AppServices.Initialize();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"SessionForge could not load its configuration:\n\n{ex.Message}", "SessionForge", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Replace the initial dictionary with the configured theme (keeps Styles.xaml).
        Resources.MergedDictionaries.RemoveAt(0);
        ThemeManager.Apply(AppServices.Settings.Theme);

        var main = new MainWindow();
        MainWindow = main;
        main.Show();

        // Vault: unlock automatically on this Windows account if configured, otherwise optionally prompt at startup.
        var vault = AppServices.Vault;
        if (vault.Exists && !vault.TryUnlockWithWindows() && AppServices.Settings.VaultUnlockPolicy == VaultUnlockPolicy.EveryStartup)
        {
            main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => UnlockVaultDialog.EnsureUnlocked("Unlock the vault to use saved passwords this session. You can also unlock it later when needed."));
        }
    }

    private bool _reportingError;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        e.Handled = true;
        // Guard against recursion: if showing the themed dialog itself fails, fall back to a native message box once.
        if (_reportingError) return;
        _reportingError = true;
        try
        {
            MessageDialog.Show("Unexpected error", "An unexpected error occurred. The details were written to the diagnostics log.", MessageKind.Error, detail: e.Exception.Message);
        }
        catch (Exception)
        {
            try { MessageBox.Show(e.Exception.Message, "SessionForge — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error); }
            catch (Exception) { /* nothing else we can do */ }
        }
        finally { _reportingError = false; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { AppServices.Vault?.Dispose(); } catch (Exception) { }
        Log.Info("Exit");
        base.OnExit(e);
    }
}
