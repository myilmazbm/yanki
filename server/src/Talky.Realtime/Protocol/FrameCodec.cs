using System.Buffers;
using System.Text;
using Google.Protobuf;
using Talky.Protocol.V1;

namespace Talky.Realtime.Protocol;

/// <summary>
/// Wire codec for protocol version 1: one WebSocket text frame carries exactly one proto3-JSON
/// object. Parsing ignores unknown fields (forward compatibility); formatting uses the default
/// proto3 JSON mapping, so <c>int64</c> fields are emitted as strings and enums by name.
/// A frame whose <c>oneof body</c> is empty or unknown to this build still parses (its
/// <c>BodyCase</c> is <c>None</c>); the connection handler decides how to answer it.
/// </summary>
/// <remarks>
/// The parse path decodes UTF-8 into a pooled char buffer, and the format path writes into a
/// pooled char buffer before encoding to UTF-8, so neither direction allocates a string per frame.
/// </remarks>
public static class FrameCodec
{
    private static readonly JsonParser Parser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private static readonly JsonFormatter Formatter = JsonFormatter.Default;

    /// <summary>Reason a frame could not be decoded. Safe to log and to send in <c>Error.message</c>.</summary>
    public const string EmptyPayloadMessage = "Frame body is empty.";

    /// <summary>Reason reported, after the handshake, for a frame with no known <c>oneof body</c> member.</summary>
    public const string UnknownBodyMessage = "Frame carries no known body.";

    /// <summary>Reason reported when the frame exceeds the configured maximum size.</summary>
    public const string TooLargeMessage = "Frame exceeds the configured maximum size.";

    /// <summary>
    /// Attempts to decode a client frame from a UTF-8 encoded proto3-JSON payload.
    /// </summary>
    /// <param name="utf8Json">Raw payload of a single WebSocket text frame.</param>
    /// <param name="maxBytes">Maximum accepted payload size in bytes.</param>
    /// <param name="frame">The decoded frame when the method returns <see langword="true"/>.</param>
    /// <param name="error">A short English reason when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the payload is a well-formed frame, known body or not.</returns>
    public static bool TryParseClientFrame(
        ReadOnlySpan<byte> utf8Json,
        int maxBytes,
        [NotNullWhen(true)] out ClientFrame? frame,
        [NotNullWhen(false)] out string? error)
    {
        frame = null;

        if (utf8Json.Length > maxBytes)
        {
            error = TooLargeMessage;
            return false;
        }

        if (utf8Json.IsEmpty)
        {
            error = EmptyPayloadMessage;
            return false;
        }

        char[] buffer = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(utf8Json.Length));
        try
        {
            int charCount = Encoding.UTF8.GetChars(utf8Json, buffer);

            using var reader = new PooledCharReader(buffer.AsMemory(0, charCount));
            try
            {
                frame = Parser.Parse<ClientFrame>(reader);
            }
            catch (InvalidJsonException)
            {
                error = "Frame is not a valid JSON object.";
                return false;
            }
            catch (InvalidProtocolBufferException)
            {
                error = "Frame does not match the protocol schema.";
                return false;
            }

            error = null;
            return true;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Encodes a server frame as UTF-8 proto3 JSON into a pooled buffer. The caller must dispose
    /// the returned <see cref="PooledUtf8Payload"/> to return the buffer to the pool.
    /// </summary>
    public static PooledUtf8Payload FormatServerFrame(ServerFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        using var writer = new PooledCharWriter();
        Formatter.Format(frame, writer);
        ReadOnlySpan<char> chars = writer.WrittenSpan;

        byte[] bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(chars.Length));
        int byteCount = Encoding.UTF8.GetBytes(chars, bytes);
        return new PooledUtf8Payload(bytes, byteCount);
    }

    /// <summary>Encodes a server frame as a proto3-JSON string. Convenience for tests and fixtures.</summary>
    public static string ToJson(ServerFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Formatter.Format(frame);
    }

    /// <summary>Encodes a client frame as a proto3-JSON string. Convenience for tests and the load tool.</summary>
    public static string ToJson(ClientFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Formatter.Format(frame);
    }

    /// <summary>Decodes a server frame from proto3 JSON. Convenience for tests and the load tool.</summary>
    public static ServerFrame ParseServerFrame(string json) =>
        Parser.Parse<ServerFrame>(json);
}
