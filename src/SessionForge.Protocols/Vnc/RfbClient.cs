using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace SessionForge.Protocols.Vnc;

public readonly record struct RfbRect(int X, int Y, int Width, int Height);

/// <summary>
/// VNC (RFB 3.3/3.7/3.8) client. Framebuffer is 32-bit BGRA (matches WPF Bgra32/Bgr32).
/// Encodings: ZRLE, Hextile, RRE, CopyRect, Raw, DesktopSize. Security: None, VNC Authentication.
/// </summary>
public sealed class RfbClient : IAsyncDisposable
{
    private const int EncRaw = 0, EncCopyRect = 1, EncRre = 2, EncHextile = 5, EncZrle = 16, EncDesktopSize = -223, EncLastRect = -224;

    private TcpClient? _tcp;
    private Stream _s = Stream.Null;
    private readonly object _writeLock = new();
    private readonly object _fbLock = new();
    private CancellationTokenSource? _cts;
    private ZlibFeed? _zrleFeed;
    private ZLibStream? _zrle;
    private int _closed;
    private byte[] _fb = [];

    public int Width { get; private set; }
    public int Height { get; private set; }
    public string DesktopName { get; private set; } = string.Empty;
    public string ServerVersion { get; private set; } = string.Empty;
    public bool ViewOnly { get; set; }

    /// <summary>Raised after each framebuffer update with the changed rectangles (background thread).</summary>
    public event Action<IReadOnlyList<RfbRect>>? FramebufferUpdated;
    public event Action<int, int>? DesktopResized;
    public event Action<string>? ServerClipboard;
    public event Action? BellRung;
    public event Action<string?>? Closed;

    /// <summary>Copies the framebuffer region into <paramref name="dest"/> (stride = Width*4) under the framebuffer lock.</summary>
    public void WithFramebuffer(Action<byte[], int, int> action)
    {
        lock (_fbLock) action(_fb, Width, Height);
    }

    public async Task ConnectAsync(string host, int port, Func<string?> passwordProvider, bool shared, CancellationToken ct)
    {
        _tcp = new TcpClient { NoDelay = true };
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await _tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        }
        _s = new BufferedStream(_tcp.GetStream(), 64 * 1024);

        // Protocol version
        var ver = Encoding.ASCII.GetString(await ReadExactAsync(12, ct).ConfigureAwait(false));
        if (!ver.StartsWith("RFB ", StringComparison.Ordinal)) throw new IOException("Not a VNC server.");
        ServerVersion = ver.Trim();
        int major = int.Parse(ver.AsSpan(4, 3)), minor = int.Parse(ver.AsSpan(8, 3));
        int useMinor = major > 3 ? 8 : minor >= 8 ? 8 : minor >= 7 ? 7 : 3;
        WriteRaw(Encoding.ASCII.GetBytes($"RFB 003.00{useMinor}\n"));

        // Security
        int secType;
        if (useMinor == 3)
        {
            secType = (int)await ReadU32Async(ct).ConfigureAwait(false);
            if (secType == 0) throw new IOException("Server refused connection: " + await ReadReasonAsync(ct).ConfigureAwait(false));
        }
        else
        {
            int n = await ReadU8Async(ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("Server refused connection: " + await ReadReasonAsync(ct).ConfigureAwait(false));
            var types = await ReadExactAsync(n, ct).ConfigureAwait(false);
            if (types.Contains((byte)1)) secType = 1;
            else if (types.Contains((byte)2)) secType = 2;
            else throw new NotSupportedException($"No supported VNC security type (server offers: {string.Join(",", types)}). Supported: None, VNC Authentication.");
            WriteRaw([(byte)secType]);
        }

        if (secType == 2)
        {
            var challenge = await ReadExactAsync(16, ct).ConfigureAwait(false);
            var pw = passwordProvider() ?? throw new ConnectionCancelledException("VNC password entry cancelled.");
            WriteRaw(VncAuthResponse(pw, challenge));
        }
        if (secType == 2 || useMinor >= 8)
        {
            uint result = await ReadU32Async(ct).ConfigureAwait(false);
            if (result != 0)
            {
                var reason = useMinor >= 8 ? await ReadReasonAsync(ct).ConfigureAwait(false) : "authentication failed";
                throw new UnauthorizedAccessException($"VNC authentication failed: {reason}");
            }
        }

        // ClientInit / ServerInit
        WriteRaw([(byte)(shared ? 1 : 0)]);
        Width = await ReadU16Async(ct).ConfigureAwait(false);
        Height = await ReadU16Async(ct).ConfigureAwait(false);
        await ReadExactAsync(16, ct).ConfigureAwait(false); // server pixel format (we override)
        var nameLen = await ReadU32Async(ct).ConfigureAwait(false);
        DesktopName = Encoding.UTF8.GetString(await ReadExactAsync((int)Math.Min(nameLen, 4096), ct).ConfigureAwait(false));
        if (nameLen > 4096) await ReadExactAsync((int)(nameLen - 4096), ct).ConfigureAwait(false);
        lock (_fbLock) _fb = new byte[Width * Height * 4];

        SendPixelFormat();
        SendEncodings([EncZrle, EncHextile, EncRre, EncCopyRect, EncRaw, EncDesktopSize, EncLastRect]);
        RequestUpdate(false, 0, 0, Width, Height);

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    #region Security

    /// <summary>VNC Authentication: DES-encrypt the challenge with the (bit-reversed, 8-byte) password.</summary>
    public static byte[] VncAuthResponse(string password, byte[] challenge)
    {
        var key = new byte[8];
        var pwBytes = Encoding.Latin1.GetBytes(password);
        for (int i = 0; i < 8 && i < pwBytes.Length; i++) key[i] = ReverseBits(pwBytes[i]);
        var des = new DesEngine(); // BouncyCastle: no weak-key rejection, which VNC needs (e.g. empty password)
        des.Init(true, new KeyParameter(key));
        var output = new byte[16];
        des.ProcessBlock(challenge, 0, output, 0);
        des.ProcessBlock(challenge, 8, output, 8);
        return output;
    }

    private static byte ReverseBits(byte b)
    {
        int r = 0;
        for (int i = 0; i < 8; i++) if ((b & (1 << i)) != 0) r |= 1 << (7 - i);
        return (byte)r;
    }

    #endregion

    #region Client → server

    private void SendPixelFormat()
    {
        var m = new byte[20];
        m[0] = 0;
        // pixel format: bpp 32, depth 24, little-endian, true colour, max 255/255/255, shifts R16 G8 B0
        m[4] = 32; m[5] = 24; m[6] = 0; m[7] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(8), 255);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(10), 255);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(12), 255);
        m[14] = 16; m[15] = 8; m[16] = 0;
        WriteRaw(m);
    }

    private void SendEncodings(int[] encodings)
    {
        var m = new byte[4 + encodings.Length * 4];
        m[0] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), (ushort)encodings.Length);
        for (int i = 0; i < encodings.Length; i++) BinaryPrimitives.WriteInt32BigEndian(m.AsSpan(4 + i * 4), encodings[i]);
        WriteRaw(m);
    }

    public void RequestUpdate(bool incremental, int x, int y, int w, int h)
    {
        var m = new byte[10];
        m[0] = 3;
        m[1] = (byte)(incremental ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), (ushort)x);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(4), (ushort)y);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(6), (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(8), (ushort)h);
        WriteRaw(m);
    }

    public void SendKey(uint keysym, bool down)
    {
        if (ViewOnly) return;
        var m = new byte[8];
        m[0] = 4;
        m[1] = (byte)(down ? 1 : 0);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(4), keysym);
        WriteRaw(m);
    }

    /// <summary>Button mask bits: 1 left, 2 middle, 4 right, 8 wheel up, 16 wheel down.</summary>
    public void SendPointer(int buttonMask, int x, int y)
    {
        if (ViewOnly) return;
        var m = new byte[6];
        m[0] = 5;
        m[1] = (byte)buttonMask;
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), (ushort)Math.Clamp(x, 0, Math.Max(0, Width - 1)));
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(4), (ushort)Math.Clamp(y, 0, Math.Max(0, Height - 1)));
        WriteRaw(m);
    }

    public void SendClipboard(string text)
    {
        if (ViewOnly) return;
        var bytes = Encoding.Latin1.GetBytes(text.Replace("\r\n", "\n"));
        var m = new byte[8 + bytes.Length];
        m[0] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(4), (uint)bytes.Length);
        bytes.CopyTo(m, 8);
        WriteRaw(m);
    }

    public void SendCtrlAltDel()
    {
        const uint ctrl = 0xFFE3, alt = 0xFFE9, del = 0xFFFF;
        SendKey(ctrl, true); SendKey(alt, true); SendKey(del, true);
        SendKey(del, false); SendKey(alt, false); SendKey(ctrl, false);
    }

    private void WriteRaw(ReadOnlySpan<byte> data)
    {
        try
        {
            lock (_writeLock) { _s.Write(data); _s.Flush(); }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { Close(ex.Message); }
    }

    #endregion

    #region Server → client

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int type = await ReadU8Async(ct).ConfigureAwait(false);
                switch (type)
                {
                    case 0: await FramebufferUpdateAsync(ct).ConfigureAwait(false); break;
                    case 1: // SetColourMapEntries: we use true colour; consume
                        await ReadExactAsync(3, ct).ConfigureAwait(false);
                        int n = await ReadU16Async(ct).ConfigureAwait(false);
                        await ReadExactAsync(n * 6, ct).ConfigureAwait(false);
                        break;
                    case 2: BellRung?.Invoke(); break;
                    case 3:
                        await ReadExactAsync(3, ct).ConfigureAwait(false);
                        uint len = await ReadU32Async(ct).ConfigureAwait(false);
                        if (len > 16 * 1024 * 1024) throw new IOException("Clipboard text too large.");
                        var text = Encoding.Latin1.GetString(await ReadExactAsync((int)len, ct).ConfigureAwait(false));
                        ServerClipboard?.Invoke(text.Replace("\n", "\r\n"));
                        break;
                    default:
                        throw new IOException($"Unsupported VNC server message type {type}.");
                }
            }
        }
        catch (OperationCanceledException) { Close(null); }
        catch (EndOfStreamException) { Close("The VNC server closed the connection."); }
        catch (Exception ex) { Close(ex.Message); }
    }

    private async Task FramebufferUpdateAsync(CancellationToken ct)
    {
        await ReadExactAsync(1, ct).ConfigureAwait(false);
        int count = await ReadU16Async(ct).ConfigureAwait(false);
        var changed = new List<RfbRect>(count);
        for (int i = 0; i < count; i++)
        {
            int x = await ReadU16Async(ct).ConfigureAwait(false);
            int y = await ReadU16Async(ct).ConfigureAwait(false);
            int w = await ReadU16Async(ct).ConfigureAwait(false);
            int h = await ReadU16Async(ct).ConfigureAwait(false);
            int enc = (int)await ReadU32Async(ct).ConfigureAwait(false);
            if (enc == EncLastRect) break;
            if (enc == EncDesktopSize)
            {
                lock (_fbLock)
                {
                    Width = w; Height = h;
                    _fb = new byte[w * h * 4];
                }
                DesktopResized?.Invoke(w, h);
                changed.Add(new RfbRect(0, 0, w, h));
                continue;
            }
            if (x + w > Width || y + h > Height) throw new IOException("VNC rectangle outside framebuffer.");
            switch (enc)
            {
                case EncRaw: await DecodeRawAsync(x, y, w, h, ct).ConfigureAwait(false); break;
                case EncCopyRect: await DecodeCopyRectAsync(x, y, w, h, ct).ConfigureAwait(false); break;
                case EncRre: await DecodeRreAsync(x, y, w, h, ct).ConfigureAwait(false); break;
                case EncHextile: await DecodeHextileAsync(x, y, w, h, ct).ConfigureAwait(false); break;
                case EncZrle: await DecodeZrleAsync(x, y, w, h, ct).ConfigureAwait(false); break;
                default: throw new IOException($"Server used unrequested encoding {enc}.");
            }
            changed.Add(new RfbRect(x, y, w, h));
        }
        if (changed.Count > 0) FramebufferUpdated?.Invoke(changed);
        RequestUpdate(true, 0, 0, Width, Height);
    }

    private async Task DecodeRawAsync(int x, int y, int w, int h, CancellationToken ct)
    {
        var data = await ReadExactAsync(w * h * 4, ct).ConfigureAwait(false);
        lock (_fbLock)
        {
            for (int row = 0; row < h; row++)
                Buffer.BlockCopy(data, row * w * 4, _fb, ((y + row) * Width + x) * 4, w * 4);
            SetOpaque(x, y, w, h);
        }
    }

    private async Task DecodeCopyRectAsync(int x, int y, int w, int h, CancellationToken ct)
    {
        int sx = await ReadU16Async(ct).ConfigureAwait(false), sy = await ReadU16Async(ct).ConfigureAwait(false);
        lock (_fbLock)
        {
            if (sx + w > Width || sy + h > Height) return;
            var tmp = new byte[w * 4];
            // Copy order matters when regions overlap vertically.
            if (sy < y)
                for (int row = h - 1; row >= 0; row--) CopyRow(row);
            else
                for (int row = 0; row < h; row++) CopyRow(row);

            void CopyRow(int row)
            {
                Buffer.BlockCopy(_fb, ((sy + row) * Width + sx) * 4, tmp, 0, w * 4);
                Buffer.BlockCopy(tmp, 0, _fb, ((y + row) * Width + x) * 4, w * 4);
            }
        }
    }

    private async Task DecodeRreAsync(int x, int y, int w, int h, CancellationToken ct)
    {
        uint n = await ReadU32Async(ct).ConfigureAwait(false);
        var bg = await ReadExactAsync(4, ct).ConfigureAwait(false);
        var subs = await ReadExactAsync(checked((int)n * 12), ct).ConfigureAwait(false);
        lock (_fbLock)
        {
            Fill(x, y, w, h, Pixel(bg, 0));
            for (int i = 0; i < n; i++)
            {
                int o = i * 12;
                uint px = Pixel(subs, o);
                int sx = BinaryPrimitives.ReadUInt16BigEndian(subs.AsSpan(o + 4)), sy = BinaryPrimitives.ReadUInt16BigEndian(subs.AsSpan(o + 6));
                int sw = BinaryPrimitives.ReadUInt16BigEndian(subs.AsSpan(o + 8)), sh = BinaryPrimitives.ReadUInt16BigEndian(subs.AsSpan(o + 10));
                if (sx + sw <= w && sy + sh <= h) Fill(x + sx, y + sy, sw, sh, px);
            }
        }
    }

    private async Task DecodeHextileAsync(int x, int y, int w, int h, CancellationToken ct)
    {
        uint bg = 0xFF000000, fg = 0xFFFFFFFF;
        for (int ty = y; ty < y + h; ty += 16)
        {
            int th = Math.Min(16, y + h - ty);
            for (int tx = x; tx < x + w; tx += 16)
            {
                int tw = Math.Min(16, x + w - tx);
                int sub = await ReadU8Async(ct).ConfigureAwait(false);
                if ((sub & 1) != 0)
                {
                    var raw = await ReadExactAsync(tw * th * 4, ct).ConfigureAwait(false);
                    lock (_fbLock)
                    {
                        for (int r = 0; r < th; r++) Buffer.BlockCopy(raw, r * tw * 4, _fb, ((ty + r) * Width + tx) * 4, tw * 4);
                        SetOpaque(tx, ty, tw, th);
                    }
                    continue;
                }
                if ((sub & 2) != 0) bg = Pixel(await ReadExactAsync(4, ct).ConfigureAwait(false), 0);
                if ((sub & 4) != 0) fg = Pixel(await ReadExactAsync(4, ct).ConfigureAwait(false), 0);
                lock (_fbLock) Fill(tx, ty, tw, th, bg);
                if ((sub & 8) != 0)
                {
                    int n = await ReadU8Async(ct).ConfigureAwait(false);
                    bool coloured = (sub & 16) != 0;
                    var data = await ReadExactAsync(n * (coloured ? 6 : 2), ct).ConfigureAwait(false);
                    lock (_fbLock)
                    {
                        int o = 0;
                        for (int i = 0; i < n; i++)
                        {
                            uint c = fg;
                            if (coloured) { c = Pixel(data, o); o += 4; }
                            int xy = data[o], wh = data[o + 1];
                            o += 2;
                            int sx = xy >> 4, sy = xy & 15, sw = (wh >> 4) + 1, sh = (wh & 15) + 1;
                            if (sx + sw <= tw && sy + sh <= th) Fill(tx + sx, ty + sy, sw, sh, c);
                        }
                    }
                }
            }
        }
    }

    private async Task DecodeZrleAsync(int x, int y, int w, int h, CancellationToken ct)
    {
        uint len = await ReadU32Async(ct).ConfigureAwait(false);
        if (len > 64 * 1024 * 1024) throw new IOException("ZRLE rectangle too large.");
        var compressed = await ReadExactAsync((int)len, ct).ConfigureAwait(false);
        _zrleFeed ??= new ZlibFeed();
        _zrle ??= new ZLibStream(_zrleFeed, CompressionMode.Decompress);
        _zrleFeed.Append(compressed);
        var z = _zrle;

        var palette = new uint[128];
        var tile = new uint[64 * 64];
        for (int ty = y; ty < y + h; ty += 64)
        {
            int th = Math.Min(64, y + h - ty);
            for (int tx = x; tx < x + w; tx += 64)
            {
                int tw = Math.Min(64, x + w - tx);
                int sub = ZByte(z);
                int pixels = tw * th;
                if (sub == 0)
                {
                    for (int i = 0; i < pixels; i++) tile[i] = CPixel(z);
                }
                else if (sub == 1)
                {
                    var c = CPixel(z);
                    Array.Fill(tile, c, 0, pixels);
                }
                else if (sub <= 16)
                {
                    for (int i = 0; i < sub; i++) palette[i] = CPixel(z);
                    int bits = sub == 2 ? 1 : sub <= 4 ? 2 : 4;
                    int mask = (1 << bits) - 1;
                    for (int row = 0; row < th; row++)
                    {
                        int bitPos = 8, b = 0;
                        for (int col = 0; col < tw; col++)
                        {
                            if (bitPos == 8) { b = ZByte(z); bitPos = 0; }
                            bitPos += bits;
                            int idx = (b >> (8 - bitPos)) & mask;
                            tile[row * tw + col] = palette[Math.Min(idx, sub - 1)];
                        }
                    }
                }
                else if (sub == 128)
                {
                    int i = 0;
                    while (i < pixels)
                    {
                        var c = CPixel(z);
                        int run = RunLength(z);
                        int end = Math.Min(pixels, i + run);
                        for (; i < end; i++) tile[i] = c;
                    }
                }
                else if (sub >= 130)
                {
                    int ps = sub - 128;
                    for (int k = 0; k < ps; k++) palette[k] = CPixel(z);
                    int i = 0;
                    while (i < pixels)
                    {
                        int v = ZByte(z);
                        var c = palette[Math.Min(v & 0x7F, ps - 1)];
                        int run = (v & 0x80) != 0 ? RunLength(z) : 1;
                        int end = Math.Min(pixels, i + run);
                        for (; i < end; i++) tile[i] = c;
                    }
                }
                else throw new IOException($"Invalid ZRLE subencoding {sub}.");

                lock (_fbLock)
                {
                    for (int row = 0; row < th; row++)
                    {
                        int dst = ((ty + row) * Width + tx) * 4;
                        for (int col = 0; col < tw; col++, dst += 4)
                            BinaryPrimitives.WriteUInt32LittleEndian(_fb.AsSpan(dst), tile[row * tw + col]);
                    }
                }
            }
        }
    }

    private static int ZByte(Stream z)
    {
        int b = z.ReadByte();
        if (b < 0) throw new IOException("ZRLE stream ended unexpectedly.");
        return b;
    }

    /// <summary>CPIXEL: 3 bytes (B, G, R for our little-endian 24-bit format) → opaque BGRA.</summary>
    private static uint CPixel(Stream z)
    {
        int b = ZByte(z), g = ZByte(z), r = ZByte(z);
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private static int RunLength(Stream z)
    {
        int run = 1, v;
        do { v = ZByte(z); run += v; } while (v == 255);
        return run;
    }

    private static uint Pixel(byte[] b, int o) => 0xFF000000u | BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));

    private void Fill(int x, int y, int w, int h, uint color)
    {
        for (int row = 0; row < h; row++)
        {
            var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(_fb.AsSpan(((y + row) * Width + x) * 4, w * 4));
            span.Fill(color);
        }
    }

    private void SetOpaque(int x, int y, int w, int h)
    {
        for (int row = 0; row < h; row++)
        {
            int o = ((y + row) * Width + x) * 4 + 3;
            for (int col = 0; col < w; col++, o += 4) _fb[o] = 0xFF;
        }
    }

    #endregion

    #region IO helpers

    private async Task<byte[]> ReadExactAsync(int n, CancellationToken ct)
    {
        var buf = new byte[n];
        if (n > 0) await _s.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
        return buf;
    }

    private async Task<int> ReadU8Async(CancellationToken ct) => (await ReadExactAsync(1, ct).ConfigureAwait(false))[0];
    private async Task<int> ReadU16Async(CancellationToken ct) => BinaryPrimitives.ReadUInt16BigEndian(await ReadExactAsync(2, ct).ConfigureAwait(false));
    private async Task<uint> ReadU32Async(CancellationToken ct) => BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(4, ct).ConfigureAwait(false));

    private async Task<string> ReadReasonAsync(CancellationToken ct)
    {
        uint len = await ReadU32Async(ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(await ReadExactAsync((int)Math.Min(len, 4096), ct).ConfigureAwait(false));
    }

    #endregion

    private void Close(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        Closed?.Invoke(reason);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        _cts?.Cancel();
        _zrle?.Dispose();
        _tcp?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Stream that the persistent ZRLE inflater reads from; data is appended per rectangle.</summary>
    private sealed class ZlibFeed : Stream
    {
        private readonly Queue<byte[]> _chunks = new();
        private byte[]? _cur;
        private int _pos;

        public void Append(byte[] data) { if (data.Length > 0) _chunks.Enqueue(data); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (_cur == null || _pos >= _cur.Length)
            {
                if (_chunks.Count == 0) return 0;
                _cur = _chunks.Dequeue();
                _pos = 0;
            }
            int n = Math.Min(buffer.Length, _cur.Length - _pos);
            _cur.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
