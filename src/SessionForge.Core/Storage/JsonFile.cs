using System.Text.Json;
using System.Text.Json.Serialization;

namespace SessionForge.Core.Storage;

/// <summary>Crash-safe JSON persistence: write to a temp file, flush, then atomically replace (keeping a .bak).</summary>
public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public static T Load<T>(string path) where T : new()
    {
        if (!File.Exists(path))
        {
            // Recover from an interrupted save that left only the backup.
            var bak = path + ".bak";
            if (File.Exists(bak)) return Deserialize<T>(File.ReadAllText(bak)) ?? new T();
            return new T();
        }
        try
        {
            return Deserialize<T>(File.ReadAllText(path)) ?? new T();
        }
        catch (JsonException)
        {
            var bak = path + ".bak";
            if (File.Exists(bak))
            {
                // Preserve the corrupt file for diagnosis, fall back to the last good copy.
                File.Copy(path, path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
                return Deserialize<T>(File.ReadAllText(bak)) ?? new T();
            }
            throw;
        }
    }

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static void Save<T>(string path, T value) => WriteAllTextAtomic(path, Serialize(value));

    public static void WriteAllTextAtomic(string path, string contents) => WriteAllBytesAtomic(path, System.Text.Encoding.UTF8.GetBytes(contents));

    public static void WriteAllBytesAtomic(string path, byte[] contents)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(contents);
            fs.Flush(true);
        }
        try
        {
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>Deep clone through JSON (used for edit-with-cancel and duplicate).</summary>
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
