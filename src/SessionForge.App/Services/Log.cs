using System.IO;
using System.Text;

namespace SessionForge.App.Services;

/// <summary>Minimal thread-safe diagnostics log (rotates at 5 MB). Never logs secrets.</summary>
public static class Log
{
    private static readonly object Lock = new();
    private static string? _path;
    private const long MaxSize = 5 * 1024 * 1024;

    public static void Initialize(string path)
    {
        _path = path;
        Info($"SessionForge {typeof(Log).Assembly.GetName().Version} starting on {Environment.OSVersion} ({(Environment.Is64BitProcess ? "x64" : "x86")}), .NET {Environment.Version}");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        if (_path == null) return;
        lock (Lock)
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > MaxSize)
                {
                    var old = _path + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_path, old);
                }
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
