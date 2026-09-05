using System.Buffers;
using System.Globalization;
using System.Text;

namespace Talky.Realtime.Protocol;

/// <summary>
/// A <see cref="TextWriter"/> that appends into a pooled character buffer, so formatting a frame
/// does not allocate an intermediate <see cref="string"/> or <see cref="StringBuilder"/> chain.
/// </summary>
internal sealed class PooledCharWriter : TextWriter
{
    private const int InitialCapacity = 512;

    private char[] _buffer = ArrayPool<char>.Shared.Rent(InitialCapacity);
    private int _length;

    public override Encoding Encoding => Encoding.UTF8;

    public override IFormatProvider FormatProvider => CultureInfo.InvariantCulture;

    public ReadOnlySpan<char> WrittenSpan => _buffer.AsSpan(0, _length);

    public override void Write(char value)
    {
        EnsureCapacity(1);
        _buffer[_length++] = value;
    }

    public override void Write(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Write(value.AsSpan());
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(buffer.AsSpan(index, count));
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        EnsureCapacity(buffer.Length);
        buffer.CopyTo(_buffer.AsSpan(_length));
        _length += buffer.Length;
    }

    private void EnsureCapacity(int additional)
    {
        if (_length + additional <= _buffer.Length)
        {
            return;
        }

        int required = Math.Max(_buffer.Length * 2, _length + additional);
        char[] grown = ArrayPool<char>.Shared.Rent(required);
        _buffer.AsSpan(0, _length).CopyTo(grown);
        ArrayPool<char>.Shared.Return(_buffer);
        _buffer = grown;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _buffer.Length > 0)
        {
            ArrayPool<char>.Shared.Return(_buffer);
            _buffer = [];
            _length = 0;
        }

        base.Dispose(disposing);
    }
}
