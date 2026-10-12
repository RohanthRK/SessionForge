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

    private bool _errorDialogPending;
    private string? _lastErrorMessage;
    private DateTime _lastErrorShown;

    /// <summary>
    /// Logs every UI exception; shows at most one dialog at a time, deferred to a safe dispatcher priority
    /// (an exception raised during layout or inside a window procedure must never open a modal dialog synchronously),
    /// and suppresses repeats of the same error for 30 seconds.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        e.Handled = true;
        var message = e.Exception.Message;
        if (_errorDialogPending) return;
        if (message == _lastErrorMessage && DateTime.UtcNow - _lastErrorShown < TimeSpan.FromSeconds(30)) return;
        _errorDialogPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                _lastErrorMessage = message;
                _lastErrorShown = DateTime.UtcNow;
                MessageDialog.Show("Unexpected error", "An unexpected error occurred. The details were written to the diagnostics log.", MessageKind.Error, detail: message);
            }
            catch (Exception ex)
            {
                Log.Error("Could not show the error dialog", ex);
            }
            finally { _errorDialogPending = false; }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { AppServices.Vault?.Dispose(); } catch (Exception) { }
        Log.Info("Exit");
        base.OnExit(e);
    }
}
