using System.Globalization;
using System.Text;
using SessionForge.Core.Models;
using SessionForge.Terminal;

namespace SessionForge.Protocols.Logging;

/// <summary>
/// Writes terminal output to a log file. Raw mode stores the exact byte stream (replayable with `cat`);
/// plain-text modes strip escape sequences and apply backspace/carriage-return editing so the log reads like the screen.
/// Writes are buffered and flushed periodically by a background timer.
/// </summary>
public sealed class SessionLogger : IDisposable
{
    private readonly LogMode _mode;
    private readonly FileStream _file;
    private readonly StreamWriter? _text;
    private readonly PlainTextExtractor? _extractor;
    private readonly Decoder _decoder;
    private readonly Timer _flushTimer;
    private readonly object _lock = new();
    private bool _disposed;

    public SessionLogger(string path, LogMode mode, Encoding encoding)
    {
        if (mode == LogMode.Off) throw new ArgumentException("Logging is off.", nameof(mode));
        _mode = mode;
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024);
        _decoder = encoding.GetDecoder();
        if (mode != LogMode.Raw)
        {
            _text = new StreamWriter(_file, new UTF8Encoding(false), 64 * 1024) { AutoFlush = false, NewLine = "\r\n" };
            _extractor = new PlainTextExtractor(line =>
            {
                if (_mode == LogMode.PlainTextWithTimestamps)
                    _text.Write(DateTime.Now.ToString("[yyyy-MM-dd HH:mm:ss.fff] ", CultureInfo.InvariantCulture));
                _text.WriteLine(line);
            });
            _text.WriteLine($"===== Session log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        }
        _flushTimer = new Timer(_ => Flush(), null, 2000, 2000);
    }

    public string Path { get; }

    public static string BuildPath(string directory, string pattern, string sessionName)
    {
        var safe = string.Concat(sessionName.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var name = pattern.Replace("{session}", safe, StringComparison.OrdinalIgnoreCase);
        // {date:format}
        int i;
        while ((i = name.IndexOf("{date", StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = name.IndexOf('}', i);
            if (end < 0) break;
            var spec = name[(i + 5)..end].TrimStart(':');
            var value = DateTime.Now.ToString(string.IsNullOrEmpty(spec) ? "yyyyMMdd_HHmmss" : spec, CultureInfo.InvariantCulture);
            name = name[..i] + value + name[(end + 1)..];
        }
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return System.IO.Path.Combine(directory, name);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (_mode == LogMode.Raw) { _file.Write(data); return; }
            var chars = new char[_decoder.GetCharCount(data, false)];
            int n = _decoder.GetChars(data, chars, false);
            _extractor!.Feed(chars.AsSpan(0, n));
        }
    }

    /// <summary>Annotates the log (connection events) — only in text modes.</summary>
    public void Annotate(string message)
    {
        lock (_lock)
        {
            if (_disposed || _text == null) return;
            _extractor!.FlushPartial();
            _text.WriteLine($"===== {message} ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) =====");
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (_disposed) return;
            try { _text?.Flush(); _file.Flush(); } catch (IOException) { }
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        lock (_lock)
        {
            if (_disposed) return;
            _extractor?.FlushPartial();
            _text?.WriteLine($"===== Session log ended {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
            _disposed = true;
            _text?.Dispose();
            _file.Dispose();
        }
    }
}

/// <summary>
/// Turns a terminal output stream into plain text lines: escape sequences removed, CR/BS line editing applied.
/// Full-screen redraws (cursor addressing) are not reconstructed — they appear as the printed characters.
/// </summary>
public sealed class PlainTextExtractor : IVtHandler
{
    private readonly VtParser _parser;
    private readonly Action<string> _emit;
    private readonly StringBuilder _line = new();
    private int _col;

    public PlainTextExtractor(Action<string> emitLine)
    {
        _emit = emitLine;
        _parser = new VtParser(this);
    }

    public void Feed(ReadOnlySpan<char> text) => _parser.Feed(text);

    /// <summary>The current, not yet terminated line.</summary>
    public string PeekPartial() => _line.ToString();

    /// <summary>Drops the first <paramref name="count"/> characters of the partial line (macro wait-for consumption).</summary>
    public void ConsumePartial(int count)
    {
        if (count <= 0) return;
        count = Math.Min(count, _line.Length);
        _line.Remove(0, count);
        _col = Math.Max(0, _col - count);
    }

    public void FlushPartial()
    {
        if (_line.Length > 0) { _emit(_line.ToString().TrimEnd()); _line.Clear(); _col = 0; }
    }

    public void Print(int cp)
    {
        var s = cp < 0x10000 ? ((char)cp).ToString() : char.ConvertFromUtf32(cp);
        if (_col < _line.Length)
        {
            // overwrite after CR (e.g. progress bars)
            int len = Math.Min(s.Length, _line.Length - _col);
            _line.Remove(_col, len).Insert(_col, s);
        }
        else
        {
            while (_line.Length < _col) _line.Append(' ');
            _line.Append(s);
        }
        _col += s.Length;
    }

    public void Execute(int c)
    {
        switch (c)
        {
            case '\n':
                _emit(_line.ToString().TrimEnd());
                _line.Clear();
                _col = 0;
                break;
            case '\r': _col = 0; break;
            case '\b': if (_col > 0) _col--; break;
            case '\t':
                int next = (_col / 8 + 1) * 8;
                while (_line.Length < next) _line.Append(' ');
                _col = next;
                break;
        }
    }

    public void CsiDispatch(CsiParams p, char privateMarker, string intermediates, char final)
    {
        // Honour the editing sequences shells use for line editing so the log matches what was typed.
        switch (final)
        {
            case 'K' when privateMarker == '\0':
                if (p[0] == 0 && _col < _line.Length) _line.Length = _col;
                else if (p[0] == 2) { _line.Clear(); }
                break;
            case 'C' when privateMarker == '\0': _col += p.Get(0, 1); break;
            case 'D' when privateMarker == '\0': _col = Math.Max(0, _col - p.Get(0, 1)); break;
            case 'G' when privateMarker == '\0': _col = Math.Max(0, p.Get(0, 1) - 1); break;
            case 'P' when privateMarker == '\0':
                if (_col < _line.Length) _line.Remove(_col, Math.Min(p.Get(0, 1), _line.Length - _col));
                break;
            case '@' when privateMarker == '\0':
                if (_col <= _line.Length) _line.Insert(_col, new string(' ', p.Get(0, 1)));
                break;
        }
    }

    public void EscDispatch(string intermediates, char final) { }
    public void OscDispatch(string data) { }
    public void DcsDispatch(char privateMarker, CsiParams p, string intermediates, char final, string data) { }
}
