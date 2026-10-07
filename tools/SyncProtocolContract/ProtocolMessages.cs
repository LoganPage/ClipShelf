using System.Text;
using System.Text.Json.Nodes;

namespace SyncProtocolContract;

internal abstract record ProtocolMessage(Guid MessageId, DateTimeOffset SentAtUtc)
{
    public abstract string MessageType { get; }
}

internal sealed record HelloMessage(
    Guid MessageId,
    DateTimeOffset SentAtUtc,
    Guid DeviceId,
    string DeviceName,
    string AppVersion,
    int[] SupportedProtocolVersions,
    string[] Capabilities) : ProtocolMessage(MessageId, SentAtUtc)
{
    public override string MessageType => "hello";
}

internal sealed record TextEventMessage(
    Guid MessageId,
    DateTimeOffset SentAtUtc,
    Guid EventId,
    Guid OriginDeviceId,
    ulong Sequence,
    DateTimeOffset CapturedAtUtc,
    string ContentHash,
    string Text) : ProtocolMessage(MessageId, SentAtUtc)
{
    public override string MessageType => "textEvent";
}

internal sealed record AckMessage(
    Guid MessageId,
    DateTimeOffset SentAtUtc,
    Guid OriginDeviceId,
    ulong AcceptedThroughSequence) : ProtocolMessage(MessageId, SentAtUtc)
{
    public override string MessageType => "ack";
}

internal sealed record ErrorMessage(
    Guid MessageId,
    DateTimeOffset SentAtUtc,
    string Code,
    Guid? RelatedMessageId,
    string? Message) : ProtocolMessage(MessageId, SentAtUtc)
{
    public override string MessageType => "error";
}

internal static class ProtocolCodec
{
    public static ProtocolMessage Decode(ReadOnlySpan<byte> utf8) => ProtocolValidator.ValidateUtf8(utf8);

    public static byte[] Encode(ProtocolMessage message)
    {
        JsonObject body = message switch
        {
            HelloMessage hello => new JsonObject
            {
                ["deviceId"] = Canonical(hello.DeviceId),
                ["deviceName"] = hello.DeviceName,
                ["appVersion"] = hello.AppVersion,
                ["supportedProtocolVersions"] = new JsonArray(hello.SupportedProtocolVersions.Select(value => JsonValue.Create(value)).ToArray()),
                ["capabilities"] = new JsonArray(hello.Capabilities.Select(value => JsonValue.Create(value)).ToArray())
            },
            TextEventMessage text => new JsonObject
            {
                ["eventId"] = Canonical(text.EventId),
                ["originDeviceId"] = Canonical(text.OriginDeviceId),
                ["sequence"] = text.Sequence,
                ["capturedAtUtc"] = FormatTimestamp(text.CapturedAtUtc),
                ["contentHash"] = text.ContentHash,
                ["payload"] = new JsonObject { ["text"] = text.Text }
            },
            AckMessage ack => new JsonObject
            {
                ["originDeviceId"] = Canonical(ack.OriginDeviceId),
                ["acceptedThroughSequence"] = ack.AcceptedThroughSequence
            },
            ErrorMessage error => EncodeError(error),
            _ => throw new ArgumentOutOfRangeException(nameof(message), "Unknown protocol message model.")
        };

        JsonObject envelope = new()
        {
            ["protocolVersion"] = ProtocolConstants.ProtocolVersion,
            ["messageType"] = message.MessageType,
            ["messageId"] = Canonical(message.MessageId),
            ["sentAtUtc"] = FormatTimestamp(message.SentAtUtc),
            ["body"] = body
        };
        byte[] encoded = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        _ = ProtocolValidator.ValidateUtf8(encoded);
        return encoded;
    }

    public static bool SemanticallyEquals(ProtocolMessage left, ProtocolMessage right) => (left, right) switch
    {
        (HelloMessage a, HelloMessage b) =>
            SameEnvelope(a, b) && a.DeviceId == b.DeviceId && a.DeviceName == b.DeviceName &&
            a.AppVersion == b.AppVersion && a.SupportedProtocolVersions.SequenceEqual(b.SupportedProtocolVersions) &&
            a.Capabilities.SequenceEqual(b.Capabilities, StringComparer.Ordinal),
        (TextEventMessage a, TextEventMessage b) =>
            SameEnvelope(a, b) && a.EventId == b.EventId && a.OriginDeviceId == b.OriginDeviceId &&
            a.Sequence == b.Sequence && a.CapturedAtUtc == b.CapturedAtUtc && a.ContentHash == b.ContentHash && a.Text == b.Text,
        (AckMessage a, AckMessage b) =>
            SameEnvelope(a, b) && a.OriginDeviceId == b.OriginDeviceId && a.AcceptedThroughSequence == b.AcceptedThroughSequence,
        (ErrorMessage a, ErrorMessage b) =>
            SameEnvelope(a, b) && a.Code == b.Code && a.RelatedMessageId == b.RelatedMessageId && a.Message == b.Message,
        _ => false
    };

    private static JsonObject EncodeError(ErrorMessage error)
    {
        JsonObject body = new() { ["code"] = error.Code };
        if (error.RelatedMessageId is Guid related) body["relatedMessageId"] = Canonical(related);
        if (error.Message is not null) body["message"] = error.Message;
        return body;
    }

    private static bool SameEnvelope(ProtocolMessage left, ProtocolMessage right) =>
        left.MessageType == right.MessageType && left.MessageId == right.MessageId && left.SentAtUtc == right.SentAtUtc;

    private static string Canonical(Guid value) => value.ToString("D");
    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(ProtocolConstants.TimestampFormat, System.Globalization.CultureInfo.InvariantCulture);
}
