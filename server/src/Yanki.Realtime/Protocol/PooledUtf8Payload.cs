using System.Buffers;

namespace Yanki.Realtime.Protocol;

/// <summary>
/// A UTF-8 payload backed by a pooled array. Dispose returns the array to
/// <see cref="ArrayPool{T}.Shared"/>; the memory must not be used afterwards.
/// </summary>
public readonly struct PooledUtf8Payload : IDisposable, IEquatable<PooledUtf8Payload>
{
    private readonly byte[]? _buffer;

    internal PooledUtf8Payload(byte[] buffer, int length)
    {
        _buffer = buffer;
        Length = length;
    }

    /// <summary>Number of meaningful bytes in <see cref="Memory"/>.</summary>
    public int Length { get; }

    /// <summary>The encoded payload.</summary>
    public ReadOnlyMemory<byte> Memory => _buffer is null ? ReadOnlyMemory<byte>.Empty : _buffer.AsMemory(0, Length);

    /// <summary>The encoded payload.</summary>
    public ReadOnlySpan<byte> Span => _buffer is null ? ReadOnlySpan<byte>.Empty : _buffer.AsSpan(0, Length);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }
    }

    /// <inheritdoc />
    public bool Equals(PooledUtf8Payload other) =>
        ReferenceEquals(_buffer, other._buffer) && Length == other.Length;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PooledUtf8Payload other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_buffer, Length);

    /// <summary>Reference equality of the underlying pooled buffer.</summary>
    public static bool operator ==(PooledUtf8Payload left, PooledUtf8Payload right) => left.Equals(right);

    /// <summary>Reference inequality of the underlying pooled buffer.</summary>
    public static bool operator !=(PooledUtf8Payload left, PooledUtf8Payload right) => !left.Equals(right);
}
