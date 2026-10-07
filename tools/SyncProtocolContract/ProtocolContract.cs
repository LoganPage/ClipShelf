using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace SyncProtocolContract;

internal static class ProtocolConstants
{
    public const int ProtocolVersion = 1;
    public const int MaxFrameBytes = 1_200_000;
    public const int MaxTextUtf8Bytes = 1_048_576;
    public const int MaxDeviceNameCharacters = 128;
    public const int MaxAppVersionCharacters = 64;
    public const string ValidatorVersion = "1.0.0";
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static readonly HashSet<string> ErrorCodes = new(StringComparer.Ordinal)
    {
        "invalid_message", "invalid_frame", "frame_too_large", "unsupported_protocol_version",
        "unsupported_message_type", "unsupported_capability", "invalid_device_id", "invalid_event_id",
        "invalid_sequence", "invalid_timestamp", "invalid_text", "text_too_large",
        "invalid_content_hash", "content_hash_mismatch", "sequence_conflict"
    };
}

internal sealed class ProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal static class ContentHash
{
    private static readonly byte[] Prefix = Encoding.UTF8.GetBytes("text\0");

    public static string Compute(string text)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        byte[] input = new byte[Prefix.Length + body.Length];
        Prefix.CopyTo(input, 0);
        body.CopyTo(input, Prefix.Length);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    public static bool IsCanonical(string value) =>
        value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    public static bool FixedTimeEquals(string left, string right)
    {
        byte[] a = Encoding.ASCII.GetBytes(left);
        byte[] b = Encoding.ASCII.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

internal static class ProtocolValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ProtocolMessage ValidateUtf8(ReadOnlySpan<byte> json)
    {
        byte[] raw = json.ToArray();
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            throw new ProtocolException("invalid_message", "UTF-8 BOM is not allowed.");
        try { _ = StrictUtf8.GetString(raw); }
        catch (DecoderFallbackException) { throw new ProtocolException("invalid_frame", "Frame is not valid UTF-8."); }

        JsonDocument document;
        try { document = JsonDocument.Parse(raw, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 64 }); }
        catch (JsonException) { throw new ProtocolException("invalid_message", "Message is not valid JSON."); }
        using (document) return Validate(document.RootElement);
    }

    public static ProtocolMessage Validate(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new ProtocolException("invalid_message", "Envelope must be an object.");
        if (!root.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int protocolVersion))
            throw new ProtocolException("invalid_message", "protocolVersion must be an integer.");
        if (protocolVersion != ProtocolConstants.ProtocolVersion)
            throw new ProtocolException("unsupported_protocol_version", "Only protocol version 1 is supported.");
        string type = RequiredString(root, "messageType", "invalid_message");
        Guid messageId = RequiredCanonicalGuid(root, "messageId", "invalid_message");
        DateTimeOffset sentAt = RequiredTimestamp(root, "sentAtUtc");
        if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            throw new ProtocolException("invalid_message", "body must be an object.");

        return type switch
        {
            "hello" => ValidateHello(messageId, sentAt, body),
            "textEvent" => ValidateText(messageId, sentAt, body),
            "ack" => ValidateAck(messageId, sentAt, body),
            "error" => ValidateError(messageId, sentAt, body),
            _ => throw new ProtocolException("unsupported_message_type", "Message type is not supported by v1.")
        };
    }

    private static HelloMessage ValidateHello(Guid messageId, DateTimeOffset sentAt, JsonElement body)
    {
        Guid deviceId = RequiredCanonicalGuid(body, "deviceId", "invalid_device_id");
        string deviceName = RequiredString(body, "deviceName", "invalid_message");
        if (UnicodeScalarCount(deviceName) is < 1 or > ProtocolConstants.MaxDeviceNameCharacters)
            throw new ProtocolException("invalid_message", "deviceName length is invalid.");
        string appVersion = RequiredString(body, "appVersion", "invalid_message");
        if (UnicodeScalarCount(appVersion) is < 1 or > ProtocolConstants.MaxAppVersionCharacters)
            throw new ProtocolException("invalid_message", "appVersion length is invalid.");
        if (!body.TryGetProperty("supportedProtocolVersions", out var versions) || versions.ValueKind != JsonValueKind.Array || versions.GetArrayLength() == 0)
            throw new ProtocolException("invalid_message", "supportedProtocolVersions must be a non-empty array.");
        bool supportsV1 = false;
        List<int> supportedVersions = [];
        foreach (var value in versions.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int parsed) || parsed < 1)
                throw new ProtocolException("unsupported_protocol_version", "Protocol versions must be positive integers.");
            supportedVersions.Add(parsed);
            supportsV1 |= parsed == 1;
        }
        if (!supportsV1) throw new ProtocolException("unsupported_protocol_version", "Peer does not advertise v1.");
        if (!body.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array)
            throw new ProtocolException("invalid_message", "capabilities must be an array.");
        List<string> capabilityNames = [];
        HashSet<string> uniqueCapabilities = new(StringComparer.Ordinal);
        foreach (var value in capabilities.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) throw new ProtocolException("unsupported_capability", "Capability names must be strings.");
            string capability = value.GetString()!;
            if (capability.Length is < 1 or > 32 || capability[0] is < 'a' or > 'z' ||
                capability.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.')))
                throw new ProtocolException("unsupported_capability", "Capability names must be lowercase ASCII.");
            if (!uniqueCapabilities.Add(capability))
                throw new ProtocolException("unsupported_capability", "Capability names must be unique.");
            capabilityNames.Add(capability);
            // Unknown but syntactically valid capabilities are retained by decoders and remain disabled.
        }
        return new(messageId, sentAt, deviceId, deviceName, appVersion, supportedVersions.ToArray(), capabilityNames.ToArray());
    }

    private static TextEventMessage ValidateText(Guid messageId, DateTimeOffset sentAt, JsonElement body)
    {
        Guid eventId = RequiredCanonicalGuid(body, "eventId", "invalid_event_id");
        Guid origin = RequiredCanonicalGuid(body, "originDeviceId", "invalid_device_id");
        ulong sequence = RequiredPositiveUInt64(body, "sequence");
        DateTimeOffset capturedAt = RequiredTimestamp(body, "capturedAtUtc");
        string hash = RequiredString(body, "contentHash", "invalid_content_hash");
        if (!ContentHash.IsCanonical(hash)) throw new ProtocolException("invalid_content_hash", "contentHash format is invalid.");
        if (!body.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("text", out var textValue) || textValue.ValueKind != JsonValueKind.String)
            throw new ProtocolException("invalid_text", "payload.text must be a string.");
        string text = textValue.GetString()!;
        if (string.IsNullOrWhiteSpace(text)) throw new ProtocolException("invalid_text", "Text must contain a non-whitespace character.");
        if (Encoding.UTF8.GetByteCount(text) > ProtocolConstants.MaxTextUtf8Bytes)
            throw new ProtocolException("text_too_large", "Text exceeds the v1 UTF-8 byte limit.");
        string actual = ContentHash.Compute(text);
        if (!ContentHash.FixedTimeEquals(hash, actual))
            throw new ProtocolException("content_hash_mismatch", "Text content does not match contentHash.");
        return new(messageId, sentAt, eventId, origin, sequence, capturedAt, hash, text);
    }

    private static AckMessage ValidateAck(Guid messageId, DateTimeOffset sentAt, JsonElement body)
    {
        Guid origin = RequiredCanonicalGuid(body, "originDeviceId", "invalid_device_id");
        if (!body.TryGetProperty("acceptedThroughSequence", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out ulong watermark))
            throw new ProtocolException("invalid_sequence", "ACK watermark must be an unsigned integer.");
        return new(messageId, sentAt, origin, watermark);
    }

    private static ErrorMessage ValidateError(Guid messageId, DateTimeOffset sentAt, JsonElement body)
    {
        string code = RequiredString(body, "code", "invalid_message");
        if (!ProtocolConstants.ErrorCodes.Contains(code)) throw new ProtocolException("invalid_message", "Unknown error code.");
        Guid? relatedMessageId = null;
        if (body.TryGetProperty("relatedMessageId", out var related) && related.ValueKind != JsonValueKind.Null)
            relatedMessageId = CanonicalGuid(related, "invalid_message");
        string? detail = null;
        if (body.TryGetProperty("message", out var message))
        {
            if (message.ValueKind != JsonValueKind.String || UnicodeScalarCount(message.GetString()!) > 256)
                throw new ProtocolException("invalid_message", "Error message is invalid.");
            detail = message.GetString();
        }
        return new(messageId, sentAt, code, relatedMessageId, detail);
    }

    private static string RequiredString(JsonElement parent, string name, string code)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()))
            throw new ProtocolException(code, $"{name} must be a non-empty string.");
        return value.GetString()!;
    }

    private static Guid RequiredCanonicalGuid(JsonElement parent, string name, string code)
    {
        if (!parent.TryGetProperty(name, out var value)) throw new ProtocolException(code, $"{name} is required.");
        return CanonicalGuid(value, code);
    }

    private static Guid CanonicalGuid(JsonElement value, string code)
    {
        if (value.ValueKind != JsonValueKind.String || !Guid.TryParseExact(value.GetString(), "D", out Guid parsed) || parsed == Guid.Empty || value.GetString() != parsed.ToString("D"))
            throw new ProtocolException(code, "UUID must be a non-empty lowercase canonical UUID.");
        return parsed;
    }

    private static DateTimeOffset RequiredTimestamp(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParseExact(value.GetString(), ProtocolConstants.TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            throw new ProtocolException("invalid_timestamp", $"{name} must be UTC with Z and milliseconds.");
        return parsed;
    }

    private static ulong RequiredPositiveUInt64(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out ulong parsed) || parsed == 0)
            throw new ProtocolException("invalid_sequence", $"{name} must be an unsigned integer starting at 1.");
        return parsed;
    }

    private static int UnicodeScalarCount(string value) => value.EnumerateRunes().Count();
}

internal static class FrameCodec
{
    public static byte[] Encode(ReadOnlySpan<byte> json)
    {
        if (json.Length == 0) throw new ProtocolException("invalid_frame", "Frame payload cannot be empty.");
        if (json.Length > ProtocolConstants.MaxFrameBytes) throw new ProtocolException("frame_too_large", "Frame exceeds v1 limit.");
        _ = ProtocolValidator.ValidateUtf8(json);
        byte[] framed = new byte[4 + json.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed, checked((uint)json.Length));
        json.CopyTo(framed.AsSpan(4));
        return framed;
    }
}

internal sealed class IncrementalFrameDecoder
{
    private readonly List<byte> buffer = [];

    public IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes) buffer.Add(value);
        List<byte[]> messages = [];
        while (buffer.Count >= 4)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(CollectionsMarshal.AsSpan(buffer)[..4]);
            if (length == 0) throw new ProtocolException("invalid_frame", "Zero-length frame is invalid.");
            if (length > ProtocolConstants.MaxFrameBytes) throw new ProtocolException("frame_too_large", "Frame exceeds v1 limit.");
            if (buffer.Count < 4 + length) break;
            byte[] payload = buffer.GetRange(4, checked((int)length)).ToArray();
            _ = ProtocolValidator.ValidateUtf8(payload);
            messages.Add(payload);
            buffer.RemoveRange(0, 4 + checked((int)length));
        }
        return messages;
    }

    public void Complete()
    {
        if (buffer.Count != 0) throw new ProtocolException("invalid_frame", "Frame stream ended with a truncated frame.");
    }
}
