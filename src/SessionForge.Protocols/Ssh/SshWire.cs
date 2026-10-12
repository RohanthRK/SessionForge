using System.Buffers.Binary;
using System.Text;

namespace SessionForge.Protocols.Ssh;

/// <summary>Minimal SSH wire-format (RFC 4251) reader/writer.</summary>
public sealed class SshWriter
{
    private readonly MemoryStream _ms = new();

    public SshWriter Byte(byte b) { _ms.WriteByte(b); return this; }

    public SshWriter UInt32(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        _ms.Write(b);
        return this;
    }

    public SshWriter String(ReadOnlySpan<byte> data) { UInt32((uint)data.Length); _ms.Write(data); return this; }
    public SshWriter String(string s) => String(Encoding.UTF8.GetBytes(s));

    /// <summary>mpint: two's complement big-endian with a leading zero when the high bit is set.</summary>
    public SshWriter MPInt(ReadOnlySpan<byte> unsignedBigEndian)
    {
        int i = 0;
        while (i < unsignedBigEndian.Length && unsignedBigEndian[i] == 0) i++;
        var v = unsignedBigEndian[i..];
        if (v.Length > 0 && (v[0] & 0x80) != 0)
        {
            UInt32((uint)v.Length + 1);
            _ms.WriteByte(0);
            _ms.Write(v);
        }
        else String(v);
        return this;
    }

    public SshWriter Raw(ReadOnlySpan<byte> data) { _ms.Write(data); return this; }

    public byte[] ToArray() => _ms.ToArray();
}

public sealed class SshReader
{
    private readonly byte[] _data;
    private int _pos;

    public SshReader(byte[] data, int offset = 0) { _data = data; _pos = offset; }

    public bool AtEnd => _pos >= _data.Length;
    public int Remaining => _data.Length - _pos;

    public byte Byte()
    {
        if (_pos >= _data.Length) throw new InvalidDataException("Unexpected end of SSH data.");
        return _data[_pos++];
    }

    public uint UInt32()
    {
        if (_pos + 4 > _data.Length) throw new InvalidDataException("Unexpected end of SSH data.");
        var v = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_pos));
        _pos += 4;
        return v;
    }

    public byte[] Bytes()
    {
        var len = UInt32();
        if (len > (uint)Remaining) throw new InvalidDataException("SSH string length exceeds buffer.");
        var r = _data.AsSpan(_pos, (int)len).ToArray();
        _pos += (int)len;
        return r;
    }

    public string String() => Encoding.UTF8.GetString(Bytes());

    public byte[] RawBytes(int count)
    {
        if (count > Remaining) throw new InvalidDataException("Unexpected end of SSH data.");
        var r = _data.AsSpan(_pos, count).ToArray();
        _pos += count;
        return r;
    }

    public byte[] Rest() => RawBytes(Remaining);
}
