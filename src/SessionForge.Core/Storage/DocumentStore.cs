namespace SessionForge.Core.Storage;

/// <summary>Simple load/mutate/save wrapper for a JSON document (settings, macros, tunnels).</summary>
public sealed class DocumentStore<T> where T : class, new()
{
    private readonly string _path;
    private readonly object _lock = new();

    public DocumentStore(string path)
    {
        _path = path;
        Value = JsonFile.Load<T>(path);
    }

    public T Value { get; private set; }

    public event Action? Changed;

    public void Update(Action<T> mutate)
    {
        lock (_lock)
        {
            mutate(Value);
            JsonFile.Save(_path, Value);
        }
        Changed?.Invoke();
    }

    public void Replace(T value)
    {
        lock (_lock)
        {
            Value = value;
            JsonFile.Save(_path, Value);
        }
        Changed?.Invoke();
    }

    public void Save()
    {
        lock (_lock) JsonFile.Save(_path, Value);
        Changed?.Invoke();
    }
}
