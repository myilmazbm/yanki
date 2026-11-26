namespace Yanki.Realtime.Protocol;

/// <summary>
/// A <see cref="TextReader"/> over an already-decoded character buffer. Lets the JSON parser read
/// a frame without materialising a <see cref="string"/>. The buffer is owned by the caller.
/// </summary>
internal sealed class PooledCharReader(ReadOnlyMemory<char> buffer) : TextReader
{
    private readonly ReadOnlyMemory<char> _buffer = buffer;
    private int _position;

    public override int Peek() =>
        _position < _buffer.Length ? _buffer.Span[_position] : -1;

    public override int Read() =>
        _position < _buffer.Length ? _buffer.Span[_position++] : -1;

    public override int Read(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(index, count));
    }

    public override int Read(Span<char> buffer)
    {
        int available = Math.Min(buffer.Length, _buffer.Length - _position);
        if (available <= 0)
        {
            return 0;
        }

        _buffer.Span.Slice(_position, available).CopyTo(buffer);
        _position += available;
        return available;
    }

    public override string ReadToEnd()
    {
        string rest = _buffer.Span[_position..].ToString();
        _position = _buffer.Length;
        return rest;
    }
}
