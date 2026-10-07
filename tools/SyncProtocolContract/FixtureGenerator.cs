using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SyncProtocolContract;

internal static class FixtureGenerator
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private static readonly Guid DeviceA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid DeviceB = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static int messageCounter;
    private static int eventCounter;

    public static void Generate(string repositoryRoot)
    {
        string protocolRoot = Path.Combine(repositoryRoot, "sync-protocol", "v1");
        string validRoot = Path.Combine(protocolRoot, "fixtures", "valid");
        string invalidRoot = Path.Combine(protocolRoot, "fixtures", "invalid");
        Directory.CreateDirectory(validRoot);
        Directory.CreateDirectory(invalidRoot);
        foreach (string file in Directory.EnumerateFiles(validRoot, "*.json")) File.Delete(file);
        foreach (string file in Directory.EnumerateFiles(invalidRoot, "*.json")) File.Delete(file);
        messageCounter = 0;
        eventCounter = 0;

        List<(string Name, JsonObject Fixture)> valid = BuildValidFixtures();
        List<(string Name, JsonObject Fixture)> invalid = BuildInvalidFixtures();
        foreach (var (name, fixture) in valid) WriteJson(Path.Combine(validRoot, name + ".json"), fixture);
        foreach (var (name, fixture) in invalid) WriteJson(Path.Combine(invalidRoot, name + ".json"), fixture);
        WriteManifest(protocolRoot);
    }

    private static List<(string, JsonObject)> BuildValidFixtures()
    {
        var hello = Envelope("hello", new JsonObject
        {
            ["deviceId"] = DeviceA.ToString("D"), ["deviceName"] = "Windows PC", ["appVersion"] = "1.4.9",
            ["supportedProtocolVersions"] = new JsonArray(1), ["capabilities"] = new JsonArray("text")
        });
        var unknownCapability = hello.DeepClone().AsObject();
        unknownCapability["messageId"] = NextMessageId();
        unknownCapability["body"]!["capabilities"] = new JsonArray("text", "future-note");

        JsonObject ascii = Text("example", 1);
        JsonObject chinese = Text("你好，ClipShelf", 2);
        JsonObject emoji = Text("hello 👋🏽 🌍", 3);
        JsonObject combining = Text("e\u0301", 4);
        JsonObject precomposed = Text("é", 5);
        JsonObject lf = Text("line 1\nline 2", 6);
        JsonObject crlf = Text("line 1\r\nline 2", 7);
        JsonObject spaces = Text("  keep me  ", 8);
        JsonObject multilingual = Text("English 简体中文 日本語 한국어 العربية 🚀", 9);
        string nearLimitText = new('a', ProtocolConstants.MaxTextUtf8Bytes);
        JsonObject nearLimit = Text("__GENERATED_TEXT__", 10, ContentHash.Compute(nearLimitText));
        var nearLimitFixture = Accepted("text-near-limit-generated", nearLimit);
        nearLimitFixture["generatedText"] = new JsonObject { ["character"] = "a", ["repeatCount"] = ProtocolConstants.MaxTextUtf8Bytes };

        JsonObject ack = Envelope("ack", new JsonObject
        {
            ["originDeviceId"] = DeviceA.ToString("D"), ["acceptedThroughSequence"] = 42
        });
        JsonObject optional = ascii.DeepClone().AsObject();
        optional["messageId"] = NextMessageId();
        optional["futureEnvelopeField"] = new JsonObject { ["ignored"] = true };
        optional["body"]!["futureBodyField"] = "retained-but-not-enabled";

        JsonObject error = Envelope("error", new JsonObject
        {
            ["code"] = "invalid_text", ["relatedMessageId"] = ascii["messageId"]!.GetValue<string>(),
            ["message"] = "Text payload was rejected."
        });
        JsonObject errorAscii256 = Error(new string('a', 256));
        JsonObject errorChinese256 = Error(new string('中', 256));
        JsonObject errorEmoji256 = Error(string.Concat(Enumerable.Repeat("😀", 256)));

        var twoFrames = new JsonObject
        {
            ["id"] = "framing-two-consecutive", ["kind"] = "framing", ["messages"] = new JsonArray(hello.DeepClone(), ascii.DeepClone()),
            ["chunkSizes"] = new JsonArray(2_000_000), ["expected"] = new JsonObject { ["accepted"] = true, ["messageCount"] = 2 }
        };
        var chunked = new JsonObject
        {
            ["id"] = "framing-chunked", ["kind"] = "framing", ["messages"] = new JsonArray(chinese.DeepClone()),
            ["chunkSizes"] = new JsonArray(1, 2, 3, 5, 8, 13, 21), ["expected"] = new JsonObject { ["accepted"] = true, ["messageCount"] = 1 }
        };

        JsonObject seq1 = Text("one", 1, origin: DeviceB);
        JsonObject seq3 = Text("three", 3, origin: DeviceB);
        JsonObject seq2 = Text("two", 2, origin: DeviceB);
        var state = new JsonObject
        {
            ["id"] = "sequence-gap-duplicate-fill", ["kind"] = "state",
            ["operations"] = new JsonArray(
                StateOperation(seq1, 1, true, false), StateOperation(seq3, 1, true, false),
                StateOperation(seq3.DeepClone().AsObject(), 1, false, true), StateOperation(seq2, 3, true, false),
                StateOperation(seq1.DeepClone().AsObject(), 3, false, true)),
            ["expected"] = new JsonObject { ["accepted"] = true, ["finalAck"] = 3 }
        };

        return
        [
            ("hello-minimal", Accepted("hello-minimal", hello)),
            ("hello-unknown-capability", Accepted("hello-unknown-capability", unknownCapability)),
            ("text-ascii", Accepted("text-ascii", ascii)),
            ("text-chinese", Accepted("text-chinese", chinese)),
            ("text-emoji", Accepted("text-emoji", emoji)),
            ("text-combining", Accepted("text-combining", combining)),
            ("text-precomposed", Accepted("text-precomposed", precomposed)),
            ("text-lf", Accepted("text-lf", lf)),
            ("text-crlf", Accepted("text-crlf", crlf)),
            ("text-leading-trailing-spaces", Accepted("text-leading-trailing-spaces", spaces)),
            ("text-multilingual", Accepted("text-multilingual", multilingual)),
            ("text-near-limit-generated", nearLimitFixture),
            ("ack-watermark", Accepted("ack-watermark", ack)),
            ("unknown-optional-field", Accepted("unknown-optional-field", optional)),
            ("error-message", Accepted("error-message", error)),
            ("error-message-ascii-256", Accepted("error-message-ascii-256", errorAscii256)),
            ("error-message-chinese-256", Accepted("error-message-chinese-256", errorChinese256)),
            ("error-message-emoji-256", Accepted("error-message-emoji-256", errorEmoji256)),
            ("framing-two-consecutive", twoFrames),
            ("framing-chunked", chunked),
            ("sequence-gap-duplicate-fill", state)
        ];
    }

    private static List<(string, JsonObject)> BuildInvalidFixtures()
    {
        JsonObject baseText = Text("valid", 1);
        List<(string, JsonObject)> fixtures = [];
        fixtures.Add(("json-damaged", RawJson("json-damaged", "{\"protocolVersion\":1,", "invalid_message")));
        byte[] badUtf8 = [0, 0, 0, 2, 0xC3, 0x28];
        fixtures.Add(("frame-invalid-utf8", RawFrame("frame-invalid-utf8", badUtf8, "invalid_frame")));
        byte[] bomPayload = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(baseText.ToJsonString())];
        byte[] bomFrame = new byte[4 + bomPayload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bomFrame, checked((uint)bomPayload.Length));
        bomPayload.CopyTo(bomFrame, 4);
        fixtures.Add(("frame-utf8-bom", RawFrame("frame-utf8-bom", bomFrame, "invalid_message")));
        fixtures.Add(("frame-zero-length", RawFrame("frame-zero-length", [0, 0, 0, 0], "invalid_frame")));
        byte[] overHeader = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(overHeader, ProtocolConstants.MaxFrameBytes + 1u);
        fixtures.Add(("frame-too-large", RawFrame("frame-too-large", overHeader, "frame_too_large")));
        byte[] truncated = FrameCodec.Encode(Encoding.UTF8.GetBytes(baseText.ToJsonString()));
        fixtures.Add(("frame-truncated", RawFrame("frame-truncated", truncated[..^3], "invalid_frame")));

        fixtures.Add(("missing-protocol-version", Rejected("missing-protocol-version", Mutate(baseText, x => x.Remove("protocolVersion")), "invalid_message")));
        fixtures.Add(("protocol-version-not-integer", Rejected("protocol-version-not-integer", Mutate(baseText, x => x["protocolVersion"] = 1.5), "invalid_message")));
        fixtures.Add(("unsupported-version", Rejected("unsupported-version", Mutate(baseText, x => x["protocolVersion"] = 2), "unsupported_protocol_version")));
        fixtures.Add(("unknown-message-type", Rejected("unknown-message-type", Mutate(baseText, x => x["messageType"] = "fileEvent"), "unsupported_message_type")));
        fixtures.Add(("empty-uuid", Rejected("empty-uuid", MutateBody(baseText, b => b["eventId"] = Guid.Empty.ToString("D")), "invalid_event_id")));
        fixtures.Add(("illegal-uuid", Rejected("illegal-uuid", MutateBody(baseText, b => b["eventId"] = "not-a-uuid"), "invalid_event_id")));
        fixtures.Add(("sequence-zero", Rejected("sequence-zero", MutateBody(baseText, b => b["sequence"] = 0), "invalid_sequence")));
        fixtures.Add(("sequence-negative", Rejected("sequence-negative", MutateBody(baseText, b => b["sequence"] = -1), "invalid_sequence")));
        fixtures.Add(("sequence-overflow", RawJson("sequence-overflow", MutateBody(baseText, b => b["sequence"] = JsonNode.Parse("18446744073709551616")).ToJsonString(), "invalid_sequence")));
        fixtures.Add(("timestamp-without-z", Rejected("timestamp-without-z", MutateBody(baseText, b => b["capturedAtUtc"] = "2026-10-07T08:00:00.000+08:00"), "invalid_timestamp")));
        fixtures.Add(("text-empty", Rejected("text-empty", InvalidText(baseText, ""), "invalid_text")));
        fixtures.Add(("text-whitespace", Rejected("text-whitespace", InvalidText(baseText, " \t\r\n"), "invalid_text")));
        string oversized = new('a', ProtocolConstants.MaxTextUtf8Bytes + 1);
        JsonObject oversizedMessage = Text("__GENERATED_TEXT__", 2, ContentHash.Compute(oversized));
        JsonObject oversizedFixture = Rejected("text-over-limit-generated", oversizedMessage, "text_too_large");
        oversizedFixture["generatedText"] = new JsonObject { ["character"] = "a", ["repeatCount"] = ProtocolConstants.MaxTextUtf8Bytes + 1 };
        fixtures.Add(("text-over-limit-generated", oversizedFixture));
        fixtures.Add(("hash-format-invalid", Rejected("hash-format-invalid", MutateBody(baseText, b => b["contentHash"] = "SHA256:ABC"), "invalid_content_hash")));
        fixtures.Add(("hash-mismatch", Rejected("hash-mismatch", MutateBody(baseText, b => b["contentHash"] = "sha256:" + new string('0', 64)), "content_hash_mismatch")));
        fixtures.Add(("device-id-empty", Rejected("device-id-empty", MutateBody(baseText, b => b["originDeviceId"] = Guid.Empty.ToString("D")), "invalid_device_id")));
        JsonObject helloBadCapability = Envelope("hello", new JsonObject
        {
            ["deviceId"] = DeviceA.ToString("D"), ["deviceName"] = "Device", ["appVersion"] = "1",
            ["supportedProtocolVersions"] = new JsonArray(1), ["capabilities"] = new JsonArray("Text")
        });
        fixtures.Add(("capability-uppercase", Rejected("capability-uppercase", helloBadCapability, "unsupported_capability")));
        JsonObject helloDuplicateCapability = helloBadCapability.DeepClone().AsObject();
        helloDuplicateCapability["body"]!["capabilities"] = new JsonArray("text", "text");
        fixtures.Add(("capability-duplicate", Rejected("capability-duplicate", helloDuplicateCapability, "unsupported_capability")));
        JsonObject helloVersionZero = helloBadCapability.DeepClone().AsObject();
        helloVersionZero["body"]!["capabilities"] = new JsonArray("text");
        helloVersionZero["body"]!["supportedProtocolVersions"] = new JsonArray(0, 1);
        fixtures.Add(("supported-version-zero", Rejected("supported-version-zero", helloVersionZero, "unsupported_protocol_version")));
        JsonObject helloVersionNegative = helloVersionZero.DeepClone().AsObject();
        helloVersionNegative["body"]!["supportedProtocolVersions"] = new JsonArray(-1, 1);
        fixtures.Add(("supported-version-negative", Rejected("supported-version-negative", helloVersionNegative, "unsupported_protocol_version")));
        JsonObject helloMissingV1 = helloVersionZero.DeepClone().AsObject();
        helloMissingV1["body"]!["supportedProtocolVersions"] = new JsonArray(2);
        fixtures.Add(("supported-version-missing-v1", Rejected("supported-version-missing-v1", helloMissingV1, "unsupported_protocol_version")));
        JsonObject helloEmptyVersions = helloVersionZero.DeepClone().AsObject();
        helloEmptyVersions["body"]!["supportedProtocolVersions"] = new JsonArray();
        fixtures.Add(("supported-version-empty", Rejected("supported-version-empty", helloEmptyVersions, "invalid_message")));
        JsonObject helloNonIntegerVersion = helloVersionZero.DeepClone().AsObject();
        helloNonIntegerVersion["body"]!["supportedProtocolVersions"] = new JsonArray(1, "2");
        fixtures.Add(("supported-version-non-integer", Rejected("supported-version-non-integer", helloNonIntegerVersion, "unsupported_protocol_version")));

        fixtures.Add(("timestamp-invalid-calendar", Rejected("timestamp-invalid-calendar",
            MutateBody(baseText, body => body["capturedAtUtc"] = "2026-02-30T08:00:00.000Z"), "invalid_timestamp")));

        JsonObject relatedZero = Error("safe");
        relatedZero["body"]!["relatedMessageId"] = Guid.Empty.ToString("D");
        fixtures.Add(("error-related-message-id-zero", Rejected("error-related-message-id-zero", relatedZero, "invalid_message")));
        fixtures.Add(("error-message-ascii-257", Rejected("error-message-ascii-257", Error(new string('a', 257)), "invalid_message")));
        fixtures.Add(("error-message-chinese-257", Rejected("error-message-chinese-257", Error(new string('中', 257)), "invalid_message")));
        fixtures.Add(("error-message-emoji-257", Rejected("error-message-emoji-257", Error(string.Concat(Enumerable.Repeat("😀", 257))), "invalid_message")));

        JsonObject conflictA = Text("first", 1, origin: DeviceB);
        JsonObject conflictB = Text("second", 1, origin: DeviceB);
        fixtures.Add(("state-sequence-conflict", ConflictFixture("state-sequence-conflict", conflictA, conflictB)));
        JsonObject eventOriginal = Text("original", 1, origin: DeviceB);
        JsonObject eventChanged = Text("changed", 2, origin: DeviceB);
        eventChanged["body"]!["eventId"] = eventOriginal["body"]!["eventId"]!.GetValue<string>();
        fixtures.Add(("state-event-conflict", ConflictFixture("state-event-conflict", eventOriginal, eventChanged)));
        JsonObject gap1 = Text("one", 1, origin: DeviceB);
        JsonObject gap3 = Text("three", 3, origin: DeviceB);
        fixtures.Add(("state-gap-cannot-advance-ack", new JsonObject
        {
            ["id"] = "state-gap-cannot-advance-ack", ["kind"] = "invalidAckClaim",
            ["operations"] = new JsonArray(gap1, gap3), ["claimedAck"] = 3,
            ["expected"] = new JsonObject { ["accepted"] = false, ["errorCode"] = "sequence_conflict", ["actualAck"] = 1 }
        }));
        JsonObject disguised = baseText.DeepClone().AsObject(); disguised["messageType"] = "imageEvent";
        disguised["body"]!["payload"] = new JsonObject { ["text"] = "C:\\picture.png", ["image"] = "not-supported" };
        fixtures.Add(("unsupported-image-file-disguise", Rejected("unsupported-image-file-disguise", disguised, "unsupported_message_type")));
        return fixtures;
    }

    private static JsonObject Envelope(string type, JsonObject body) => new()
    {
        ["protocolVersion"] = 1, ["messageType"] = type, ["messageId"] = NextMessageId(),
        ["sentAtUtc"] = "2026-10-07T08:00:00.000Z", ["body"] = body
    };

    private static JsonObject Text(string text, ulong sequence, string? hash = null, Guid? origin = null) => Envelope("textEvent", new JsonObject
    {
        ["eventId"] = NextEventId(), ["originDeviceId"] = (origin ?? DeviceA).ToString("D"), ["sequence"] = sequence,
        ["capturedAtUtc"] = "2026-10-07T08:00:00.000Z", ["contentHash"] = hash ?? ContentHash.Compute(text),
        ["payload"] = new JsonObject { ["text"] = text }
    });

    private static JsonObject Error(string message) => Envelope("error", new JsonObject
    {
        ["code"] = "invalid_message",
        ["relatedMessageId"] = "00000000-0000-4000-8000-000000000001",
        ["message"] = message
    });

    private static JsonObject Accepted(string id, JsonObject message) => new()
    {
        ["id"] = id, ["kind"] = "json", ["message"] = message,
        ["expected"] = new JsonObject { ["accepted"] = true, ["messageType"] = message["messageType"]!.GetValue<string>() }
    };

    private static JsonObject Rejected(string id, JsonObject message, string errorCode) => new()
    {
        ["id"] = id, ["kind"] = "json", ["message"] = message,
        ["expected"] = new JsonObject { ["accepted"] = false, ["errorCode"] = errorCode }
    };

    private static JsonObject RawJson(string id, string raw, string code) => new()
    {
        ["id"] = id, ["kind"] = "rawJson", ["rawUtf8Base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)),
        ["expected"] = new JsonObject { ["accepted"] = false, ["errorCode"] = code }
    };

    private static JsonObject RawFrame(string id, byte[] bytes, string code) => new()
    {
        ["id"] = id, ["kind"] = "rawFrame", ["bytesBase64"] = Convert.ToBase64String(bytes),
        ["expected"] = new JsonObject { ["accepted"] = false, ["errorCode"] = code }
    };

    private static JsonObject ConflictFixture(string id, JsonObject first, JsonObject second) => new()
    {
        ["id"] = id, ["kind"] = "stateConflict", ["operations"] = new JsonArray(first, second),
        ["expected"] = new JsonObject { ["accepted"] = false, ["errorCode"] = "sequence_conflict" }
    };

    private static JsonObject StateOperation(JsonObject message, ulong ack, bool imported, bool duplicate) => new()
    {
        ["message"] = message, ["expectedAck"] = ack, ["imported"] = imported, ["duplicate"] = duplicate
    };

    private static JsonObject Mutate(JsonObject source, Action<JsonObject> mutation)
    {
        JsonObject clone = source.DeepClone().AsObject(); mutation(clone); return clone;
    }

    private static JsonObject MutateBody(JsonObject source, Action<JsonObject> mutation) =>
        Mutate(source, envelope => mutation(envelope["body"]!.AsObject()));

    private static JsonObject InvalidText(JsonObject source, string text) => MutateBody(source, body =>
    {
        body["payload"]!["text"] = text; body["contentHash"] = ContentHash.Compute(text);
    });

    private static string NextMessageId() => $"00000000-0000-4000-8000-{++messageCounter:000000000000}";
    private static string NextEventId() => $"33333333-3333-4333-8333-{++eventCounter:000000000000}";

    private static void WriteJson(string path, JsonNode node)
    {
        string json = node.ToJsonString(Pretty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n') + "\n";
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteManifest(string protocolRoot)
    {
        string[] schemas = Directory.EnumerateFiles(Path.Combine(protocolRoot, "schema"), "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
        string[] fixtures = Directory.EnumerateFiles(Path.Combine(protocolRoot, "fixtures"), "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        JsonArray schemaEntries = [], fixtureEntries = [];
        foreach (string file in schemas) schemaEntries.Add(FileEntry(protocolRoot, file));
        foreach (string file in fixtures) fixtureEntries.Add(FileEntry(protocolRoot, file));
        JsonObject manifest = new()
        {
            ["protocolVersion"] = 1, ["status"] = "draft-1", ["generatedAtUtc"] = DateTimeOffset.UtcNow.ToString(ProtocolConstants.TimestampFormat),
            ["referenceValidatorVersion"] = ProtocolConstants.ValidatorVersion,
            ["limits"] = new JsonObject { ["maxFrameBytes"] = ProtocolConstants.MaxFrameBytes, ["maxTextUtf8Bytes"] = ProtocolConstants.MaxTextUtf8Bytes },
            ["schemas"] = schemaEntries, ["fixtures"] = fixtureEntries
        };
        WriteJson(Path.Combine(protocolRoot, "manifest.json"), manifest);
    }

    private static JsonObject FileEntry(string root, string file) => new()
    {
        ["path"] = Path.GetRelativePath(root, file).Replace('\\', '/'),
        ["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant()
    };
}
