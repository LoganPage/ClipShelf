using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

namespace SyncProtocolContract;

internal sealed record ValidationCheck(string Name, bool Passed, string Detail);

internal sealed class ValidationReport
{
    public string ProtocolStatus { get; init; } = "draft-1";
    public string ValidatorVersion { get; init; } = ProtocolConstants.ValidatorVersion;
    public DateTimeOffset CompletedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public List<ValidationCheck> Checks { get; init; } = [];
    public int TotalChecks => Checks.Count;
    public int PassedChecks => Checks.Count(check => check.Passed);
    public int FailedChecks => Checks.Count(check => !check.Passed);
    public bool Passed => FailedChecks == 0;

    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Sync Protocol v1 validation").AppendLine();
        builder.AppendLine($"- Status: `{ProtocolStatus}`");
        builder.AppendLine($"- Validator: `{ValidatorVersion}`");
        builder.AppendLine($"- Result: **{PassedChecks}/{TotalChecks} passed; {FailedChecks} failed**");
        builder.AppendLine($"- Completed: `{CompletedAtUtc:O}`").AppendLine();
        builder.AppendLine("| Check | Result | Detail |").AppendLine("|---|---|---|");
        foreach (ValidationCheck check in Checks)
            builder.AppendLine($"| {Escape(check.Name)} | {(check.Passed ? "PASS" : "FAIL")} | {Escape(check.Detail)} |");
        return builder.ToString();
    }

    private static string Escape(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}

internal static class FixtureRunner
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static ValidationReport Run(string repositoryRoot)
    {
        var report = new ValidationReport();
        string protocolRoot = Path.Combine(repositoryRoot, "sync-protocol", "v1");
        RunCheck(report, "schema-contract-alignment", () => ValidateSchemaContract(protocolRoot));
        RunCheck(report, "manifest-hashes", () => ValidateManifest(protocolRoot));
        RunCheck(report, "validator-isolation", () => ValidateIsolation(repositoryRoot));
        RunCheck(report, "adversarial-contract-coverage", () => ValidateAdversarialCoverage(protocolRoot));

        foreach (string path in Directory.EnumerateFiles(Path.Combine(protocolRoot, "fixtures", "valid"), "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
            RunCheck(report, "valid/" + Path.GetFileNameWithoutExtension(path), () => ExecuteFixture(path, expectAccepted: true));
        foreach (string path in Directory.EnumerateFiles(Path.Combine(protocolRoot, "fixtures", "invalid"), "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
            RunCheck(report, "invalid/" + Path.GetFileNameWithoutExtension(path), () => ExecuteFixture(path, expectAccepted: false));

        RunCheck(report, "roundtrip-semantic-equivalence", () => ValidateRoundTrips(protocolRoot));
        RunCheck(report, "unknown-extension-policy", () => ValidateUnknownExtensionPolicy(protocolRoot));
        RunCheck(report, "property-order-independent", ValidatePropertyOrder);
        RunCheck(report, "culture-independent", () => ValidateCultures(protocolRoot));
        RunCheck(report, "unicode-scalar-length", ValidateUnicodeScalarLength);
        RunCheck(report, "content-hash-distinguishes-normalization", ValidateNormalizationHashes);
        RunCheck(report, "framing-boundaries", ValidateFramingBoundaries);
        return report;
    }

    private static void RunCheck(ValidationReport report, string name, Action action)
    {
        try { action(); report.Checks.Add(new(name, true, "ok")); }
        catch (Exception exception) { report.Checks.Add(new(name, false, exception is ProtocolException pe ? $"{pe.Code}: {pe.Message}" : exception.Message)); }
    }

    private static void ExecuteFixture(string path, bool expectAccepted)
    {
        JsonObject fixture = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        bool declaredAccepted = fixture["expected"]!["accepted"]!.GetValue<bool>();
        if (declaredAccepted != expectAccepted) throw new InvalidDataException("Fixture directory and expected.accepted disagree.");
        string? expectedCode = fixture["expected"]!["errorCode"]?.GetValue<string>();
        try
        {
            string kind = fixture["kind"]!.GetValue<string>();
            switch (kind)
            {
                case "json": ExecuteJson(fixture); break;
                case "rawJson": _ = ProtocolValidator.ValidateUtf8(Convert.FromBase64String(fixture["rawUtf8Base64"]!.GetValue<string>())); break;
                case "rawFrame": ExecuteRawFrame(fixture); break;
                case "framing": ExecuteFraming(fixture); break;
                case "state": ExecuteState(fixture); break;
                case "stateConflict": ExecuteStateConflict(fixture); break;
                case "invalidAckClaim": ExecuteInvalidAckClaim(fixture); break;
                default: throw new InvalidDataException($"Unknown fixture kind: {kind}");
            }
            if (!expectAccepted) throw new InvalidDataException("Invalid fixture was accepted.");
        }
        catch (ProtocolException exception)
        {
            if (expectAccepted) throw;
            if (!string.Equals(exception.Code, expectedCode, StringComparison.Ordinal))
                throw new InvalidDataException($"Expected {expectedCode}, received {exception.Code}.");
        }
    }

    private static void ExecuteJson(JsonObject fixture)
    {
        JsonObject message = ExpandedMessage(fixture);
        byte[] utf8 = Encoding.UTF8.GetBytes(message.ToJsonString(Compact));
        ProtocolMessage envelope = ProtocolCodec.Decode(utf8);
        string expectedType = fixture["expected"]!["messageType"]!.GetValue<string>();
        if (envelope.MessageType != expectedType) throw new InvalidDataException("Decoded messageType differs from expectation.");
        byte[] frame = FrameCodec.Encode(utf8);
        var decoder = new IncrementalFrameDecoder();
        IReadOnlyList<byte[]> decoded = decoder.Feed(frame);
        decoder.Complete();
        if (decoded.Count != 1 || !utf8.SequenceEqual(decoded[0])) throw new InvalidDataException("Frame round trip changed JSON bytes.");
        if (envelope is TextEventMessage textEvent && !string.Equals(textEvent.Text, message["body"]!["payload"]!["text"]!.GetValue<string>(), StringComparison.Ordinal))
            throw new InvalidDataException("Text content changed during decoding.");
    }

    private static JsonObject ExpandedMessage(JsonObject fixture)
    {
        JsonObject message = fixture["message"]!.DeepClone().AsObject();
        if (fixture["generatedText"] is JsonObject generated)
        {
            string character = generated["character"]!.GetValue<string>();
            int repeat = generated["repeatCount"]!.GetValue<int>();
            message["body"]!["payload"]!["text"] = string.Concat(Enumerable.Repeat(character, repeat));
        }
        return message;
    }

    private static void ExecuteRawFrame(JsonObject fixture)
    {
        byte[] bytes = Convert.FromBase64String(fixture["bytesBase64"]!.GetValue<string>());
        var decoder = new IncrementalFrameDecoder();
        _ = decoder.Feed(bytes);
        decoder.Complete();
    }

    private static void ExecuteFraming(JsonObject fixture)
    {
        List<byte> stream = [];
        foreach (JsonNode? node in fixture["messages"]!.AsArray())
            stream.AddRange(FrameCodec.Encode(Encoding.UTF8.GetBytes(node!.ToJsonString(Compact))));
        int[] chunks = fixture["chunkSizes"]!.AsArray().Select(node => node!.GetValue<int>()).ToArray();
        var decoder = new IncrementalFrameDecoder();
        List<byte[]> decoded = [];
        int offset = 0, index = 0;
        while (offset < stream.Count)
        {
            int length = Math.Min(chunks[index++ % chunks.Length], stream.Count - offset);
            decoded.AddRange(decoder.Feed(CollectionsMarshal.AsSpan(stream).Slice(offset, length)));
            offset += length;
        }
        decoder.Complete();
        int expected = fixture["expected"]!["messageCount"]!.GetValue<int>();
        if (decoded.Count != expected) throw new InvalidDataException($"Expected {expected} decoded frames, got {decoded.Count}.");
    }

    private static void ExecuteState(JsonObject fixture)
    {
        var state = new SequenceState();
        foreach (JsonObject operation in fixture["operations"]!.AsArray().Select(node => node!.AsObject()))
        {
            TextEventMessage message = RequireTextEvent(ValidateNode(operation["message"]!));
            SequenceResult result = state.Accept(message);
            if (result.AcceptedThroughSequence != operation["expectedAck"]!.GetValue<ulong>() ||
                result.Imported != operation["imported"]!.GetValue<bool>() || result.Duplicate != operation["duplicate"]!.GetValue<bool>())
                throw new InvalidDataException("Sequence state result differs from fixture.");
        }
        ulong expected = fixture["expected"]!["finalAck"]!.GetValue<ulong>();
        JsonObject first = fixture["operations"]![0]!["message"]!.AsObject();
        Guid origin = Guid.Parse(first["body"]!["originDeviceId"]!.GetValue<string>());
        if (state.GetWatermark(origin) != expected) throw new InvalidDataException("Final ACK watermark differs.");
    }

    private static void ExecuteStateConflict(JsonObject fixture)
    {
        var state = new SequenceState();
        JsonArray operations = fixture["operations"]!.AsArray();
        _ = state.Accept(RequireTextEvent(ValidateNode(operations[0]!)));
        _ = state.Accept(RequireTextEvent(ValidateNode(operations[1]!)));
    }

    private static void ExecuteInvalidAckClaim(JsonObject fixture)
    {
        var state = new SequenceState();
        Guid origin = Guid.Empty;
        foreach (JsonNode? operation in fixture["operations"]!.AsArray())
        {
            TextEventMessage envelope = RequireTextEvent(ValidateNode(operation!));
            origin = envelope.OriginDeviceId;
            _ = state.Accept(envelope);
        }
        ulong actual = state.GetWatermark(origin);
        ulong claimed = fixture["claimedAck"]!.GetValue<ulong>();
        ulong expectedActual = fixture["expected"]!["actualAck"]!.GetValue<ulong>();
        if (actual != expectedActual) throw new InvalidDataException("Reference watermark is not the expected gap-safe value.");
        if (claimed > actual) throw new ProtocolException("sequence_conflict", "ACK claim crosses a sequence gap.");
    }

    private static ProtocolMessage ValidateNode(JsonNode node) => ProtocolCodec.Decode(Encoding.UTF8.GetBytes(node.ToJsonString(Compact)));

    private static TextEventMessage RequireTextEvent(ProtocolMessage message) => message as TextEventMessage ??
        throw new InvalidDataException("Fixture operation must decode as a textEvent.");

    private static void ValidateRoundTrips(string protocolRoot)
    {
        HashSet<string> roundTrippedTypes = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(Path.Combine(protocolRoot, "fixtures", "valid"), "*.json"))
        {
            JsonObject fixture = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (fixture["kind"]!.GetValue<string>() != "json") continue;
            JsonObject message = ExpandedMessage(fixture);
            ProtocolMessage first = ValidateNode(message);
            byte[] canonical = ProtocolCodec.Encode(first);
            ProtocolMessage second = ProtocolCodec.Decode(canonical);
            if (!ProtocolCodec.SemanticallyEquals(first, second))
                throw new InvalidDataException($"Strongly typed semantic round trip failed for {Path.GetFileName(path)}.");
            roundTrippedTypes.Add(first.MessageType);
        }
        string[] requiredTypes = ["hello", "textEvent", "ack", "error"];
        if (!roundTrippedTypes.SetEquals(requiredTypes))
            throw new InvalidDataException("Strongly typed round trips did not cover every v1 message type.");
    }

    private static void ValidatePropertyOrder()
    {
        const string reordered = "{\"body\":{\"payload\":{\"text\":\"order\"},\"contentHash\":\"sha256:7c0d47bd4b8bf21caaa8d7e5a83d9d58638f2b0b30253c80bc66c37a6fd83413\",\"capturedAtUtc\":\"2026-10-07T08:00:00.000Z\",\"sequence\":1,\"originDeviceId\":\"11111111-1111-4111-8111-111111111111\",\"eventId\":\"33333333-3333-4333-8333-333333333333\"},\"sentAtUtc\":\"2026-10-07T08:00:00.000Z\",\"messageId\":\"00000000-0000-4000-8000-000000000099\",\"messageType\":\"textEvent\",\"protocolVersion\":1}";
        // Replace the hand-written hash with the independently computed value while preserving unusual property order.
        using JsonDocument doc = JsonDocument.Parse(reordered);
        JsonObject node = JsonNode.Parse(reordered)!.AsObject();
        node["body"]!["contentHash"] = ContentHash.Compute("order");
        _ = ProtocolValidator.ValidateUtf8(Encoding.UTF8.GetBytes(node.ToJsonString()));
    }

    private static void ValidateUnknownExtensionPolicy(string protocolRoot)
    {
        JsonObject helloFixture = JsonNode.Parse(File.ReadAllText(Path.Combine(protocolRoot, "fixtures", "valid", "hello-unknown-capability.json")))!.AsObject();
        HelloMessage hello = ValidateNode(ExpandedMessage(helloFixture)) as HelloMessage ??
            throw new InvalidDataException("Unknown-capability fixture did not decode as Hello.");
        if (!hello.Capabilities.SequenceEqual(["text", "future-note"], StringComparer.Ordinal))
            throw new InvalidDataException("A syntactically valid unknown capability was not retained.");
        HelloMessage helloRoundTrip = ProtocolCodec.Decode(ProtocolCodec.Encode(hello)) as HelloMessage ??
            throw new InvalidDataException("Round-tripped Hello changed type.");
        if (!helloRoundTrip.Capabilities.SequenceEqual(hello.Capabilities, StringComparer.Ordinal))
            throw new InvalidDataException("Unknown capability was lost during strong-type round trip.");

        JsonObject optionalFixture = JsonNode.Parse(File.ReadAllText(Path.Combine(protocolRoot, "fixtures", "valid", "unknown-optional-field.json")))!.AsObject();
        ProtocolMessage optional = ValidateNode(ExpandedMessage(optionalFixture));
        JsonObject canonical = JsonNode.Parse(ProtocolCodec.Encode(optional))!.AsObject();
        if (canonical.ContainsKey("futureEnvelopeField") || canonical["body"]!.AsObject().ContainsKey("futureBodyField"))
            throw new InvalidDataException("Unknown optional fields must be ignored and omitted by canonical re-encoding.");
    }

    private static void ValidateCultures(string protocolRoot)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo originalUi = CultureInfo.CurrentUICulture;
        try
        {
            string fixturePath = Path.Combine(protocolRoot, "fixtures", "valid", "text-multilingual.json");
            JsonObject fixture = JsonNode.Parse(File.ReadAllText(fixturePath))!.AsObject();
            foreach (string cultureName in new[] { "en-US", "tr-TR", "zh-CN", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
                TextEventMessage result = RequireTextEvent(ValidateNode(ExpandedMessage(fixture)));
                if (result.ContentHash != ContentHash.Compute(result.Text)) throw new InvalidDataException($"Culture changed the hash in {cultureName}.");
            }
        }
        finally { CultureInfo.CurrentCulture = original; CultureInfo.CurrentUICulture = originalUi; }
    }

    private static void ValidateNormalizationHashes()
    {
        string combining = ContentHash.Compute("e\u0301");
        string precomposed = ContentHash.Compute("é");
        if (combining == precomposed) throw new InvalidDataException("Normalization-distinct text produced the same hash.");
        if (ContentHash.Compute("line\n") == ContentHash.Compute("line\r\n")) throw new InvalidDataException("LF and CRLF produced the same hash.");
        if (ContentHash.Compute(" text ") == ContentHash.Compute("text")) throw new InvalidDataException("Whitespace was normalized before hashing.");
    }

    private static void ValidateUnicodeScalarLength()
    {
        JsonObject message = new()
        {
            ["protocolVersion"] = 1,
            ["messageType"] = "hello",
            ["messageId"] = "00000000-0000-4000-8000-000000000124",
            ["sentAtUtc"] = "2026-10-07T08:00:00.000Z",
            ["body"] = new JsonObject
            {
                ["deviceId"] = "11111111-1111-4111-8111-111111111111",
                ["deviceName"] = string.Concat(Enumerable.Repeat("😀", ProtocolConstants.MaxDeviceNameCharacters)),
                ["appVersion"] = "1.4.9",
                ["supportedProtocolVersions"] = new JsonArray(1),
                ["capabilities"] = new JsonArray("text")
            }
        };
        _ = ValidateNode(message);
        message["body"]!["deviceName"] = string.Concat(Enumerable.Repeat("😀", ProtocolConstants.MaxDeviceNameCharacters + 1));
        try
        {
            _ = ValidateNode(message);
            throw new InvalidDataException("Device name above the Unicode-scalar limit was accepted.");
        }
        catch (ProtocolException exception) when (exception.Code == "invalid_message") { }
    }

    private static void ValidateFramingBoundaries()
    {
        JsonObject fixture = new()
        {
            ["protocolVersion"] = 1, ["messageType"] = "ack", ["messageId"] = "00000000-0000-4000-8000-000000000123",
            ["sentAtUtc"] = "2026-10-07T08:00:00.000Z",
            ["body"] = new JsonObject { ["originDeviceId"] = "11111111-1111-4111-8111-111111111111", ["acceptedThroughSequence"] = 0 }
        };
        byte[] frame = FrameCodec.Encode(Encoding.UTF8.GetBytes(fixture.ToJsonString()));
        var decoder = new IncrementalFrameDecoder();
        for (int index = 0; index < frame.Length; index++)
        {
            IReadOnlyList<byte[]> messages = decoder.Feed(frame.AsSpan(index, 1));
            if (index < frame.Length - 1 && messages.Count != 0) throw new InvalidDataException("Decoder emitted a truncated frame.");
            if (index == frame.Length - 1 && messages.Count != 1) throw new InvalidDataException("Decoder did not emit completed frame.");
        }
        decoder.Complete();
    }

    private static void ValidateSchemaContract(string protocolRoot)
    {
        string schemaRoot = Path.Combine(protocolRoot, "schema");
        using JsonDocument envelope = JsonDocument.Parse(File.ReadAllText(Path.Combine(schemaRoot, "envelope.schema.json")));
        JsonElement root = envelope.RootElement;
        if (root.GetProperty("$schema").GetString() != "https://json-schema.org/draft/2020-12/schema") throw new InvalidDataException("Schema draft is not frozen.");
        if (root.GetProperty("properties").GetProperty("protocolVersion").GetProperty("const").GetInt32() != ProtocolConstants.ProtocolVersion) throw new InvalidDataException("Schema version differs from validator.");
        RequireFields(root, "protocolVersion", "messageType", "messageId", "sentAtUtc", "body");
        RequireType(root.GetProperty("properties").GetProperty("protocolVersion"), "integer");
        RequireType(root.GetProperty("properties").GetProperty("messageType"), "string");
        RequireType(root.GetProperty("properties").GetProperty("messageId"), "string");
        RequireType(root.GetProperty("properties").GetProperty("sentAtUtc"), "string");
        RequireType(root.GetProperty("properties").GetProperty("body"), "object");
        RequireEnum(root.GetProperty("properties").GetProperty("messageType"), "hello", "textEvent", "ack", "error");
        RequirePattern(root.GetProperty("properties").GetProperty("messageId"), "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");
        RequirePattern(root.GetProperty("properties").GetProperty("sentAtUtc"), "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{3}Z$");
        RequireFormat(root.GetProperty("properties").GetProperty("sentAtUtc"), "date-time");

        using JsonDocument hello = JsonDocument.Parse(File.ReadAllText(Path.Combine(schemaRoot, "hello.schema.json")));
        JsonElement helloBody = MessageBody(hello, "hello");
        RequireFields(helloBody, "deviceId", "deviceName", "appVersion", "supportedProtocolVersions", "capabilities");
        JsonElement helloProperties = helloBody.GetProperty("properties");
        RequireType(helloProperties.GetProperty("deviceId"), "string");
        RequireStringBounds(helloProperties.GetProperty("deviceName"), 1, ProtocolConstants.MaxDeviceNameCharacters);
        RequireStringBounds(helloProperties.GetProperty("appVersion"), 1, ProtocolConstants.MaxAppVersionCharacters);
        RequireType(helloProperties.GetProperty("supportedProtocolVersions"), "array");
        JsonElement versionArray = helloProperties.GetProperty("supportedProtocolVersions");
        RequireType(versionArray.GetProperty("items"), "integer");
        if (versionArray.GetProperty("minItems").GetInt32() != 1 ||
            versionArray.GetProperty("items").GetProperty("minimum").GetInt32() != 1 ||
            versionArray.GetProperty("contains").GetProperty("const").GetInt32() != 1 ||
            versionArray.GetProperty("minContains").GetInt32() != 1)
            throw new InvalidDataException("supportedProtocolVersions constraints differ.");
        RequireType(helloProperties.GetProperty("capabilities"), "array");
        JsonElement capabilityArray = helloProperties.GetProperty("capabilities");
        RequirePattern(capabilityArray.GetProperty("items"), "^[a-z][a-z0-9._-]{0,31}$");
        if (!capabilityArray.GetProperty("uniqueItems").GetBoolean())
            throw new InvalidDataException("Capability uniqueness differs.");

        using JsonDocument text = JsonDocument.Parse(File.ReadAllText(Path.Combine(schemaRoot, "text-event.schema.json")));
        JsonElement textBody = MessageBody(text, "textEvent");
        RequireFields(textBody, "eventId", "originDeviceId", "sequence", "capturedAtUtc", "contentHash", "payload");
        JsonElement textProperties = textBody.GetProperty("properties");
        RequireType(textProperties.GetProperty("eventId"), "string");
        RequireType(textProperties.GetProperty("originDeviceId"), "string");
        RequireType(textProperties.GetProperty("sequence"), "integer");
        if (textProperties.GetProperty("sequence").GetProperty("minimum").GetUInt64() != 1 ||
            textProperties.GetProperty("sequence").GetProperty("maximum").GetUInt64() != ulong.MaxValue)
            throw new InvalidDataException("Text sequence bounds differ.");
        RequirePattern(textProperties.GetProperty("capturedAtUtc"), "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{3}Z$");
        RequireFormat(textProperties.GetProperty("capturedAtUtc"), "date-time");
        RequirePattern(textProperties.GetProperty("contentHash"), "^sha256:[0-9a-f]{64}$");
        JsonElement payload = textProperties.GetProperty("payload");
        RequireType(payload, "object");
        RequireFields(payload, "text");
        RequireType(payload.GetProperty("properties").GetProperty("text"), "string");

        using JsonDocument ack = JsonDocument.Parse(File.ReadAllText(Path.Combine(schemaRoot, "ack.schema.json")));
        JsonElement ackBody = MessageBody(ack, "ack");
        RequireFields(ackBody, "originDeviceId", "acceptedThroughSequence");
        JsonElement ackProperties = ackBody.GetProperty("properties");
        RequireType(ackProperties.GetProperty("originDeviceId"), "string");
        RequireType(ackProperties.GetProperty("acceptedThroughSequence"), "integer");
        if (ackProperties.GetProperty("acceptedThroughSequence").GetProperty("minimum").GetUInt64() != 0 ||
            ackProperties.GetProperty("acceptedThroughSequence").GetProperty("maximum").GetUInt64() != ulong.MaxValue)
            throw new InvalidDataException("ACK sequence bounds differ.");

        using JsonDocument error = JsonDocument.Parse(File.ReadAllText(Path.Combine(schemaRoot, "error.schema.json")));
        JsonElement errorBody = MessageBody(error, "error");
        RequireFields(errorBody, "code");
        JsonElement errorProperties = errorBody.GetProperty("properties");
        RequireType(errorProperties.GetProperty("code"), "string");
        var schemaCodes = errorProperties.GetProperty("code").GetProperty("enum").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
        if (!schemaCodes.SetEquals(ProtocolConstants.ErrorCodes)) throw new InvalidDataException("Error-code schema differs from validator.");
        JsonElement relatedMessageId = errorProperties.GetProperty("relatedMessageId");
        RequirePattern(relatedMessageId, "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");
        if (relatedMessageId.GetProperty("not").GetProperty("const").GetString() != Guid.Empty.ToString("D"))
            throw new InvalidDataException("relatedMessageId zero-UUID rule differs.");
        JsonElement errorMessage = errorProperties.GetProperty("message");
        RequireType(errorMessage, "string");
        if (errorMessage.GetProperty("maxLength").GetInt32() != 256 ||
            !errorMessage.GetProperty("$comment").GetString()!.Contains("Unicode scalar", StringComparison.Ordinal))
            throw new InvalidDataException("Error message limit differs.");
    }

    private static JsonElement MessageBody(JsonDocument schema, string messageType)
    {
        JsonElement specialized = schema.RootElement.GetProperty("allOf")[1];
        if (specialized.GetProperty("properties").GetProperty("messageType").GetProperty("const").GetString() != messageType)
            throw new InvalidDataException($"Schema messageType differs for {messageType}.");
        return specialized.GetProperty("properties").GetProperty("body");
    }

    private static void RequireFields(JsonElement schema, params string[] fields)
    {
        var actual = schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(fields)) throw new InvalidDataException($"Required fields differ: expected {string.Join(", ", fields)}.");
    }

    private static void RequireType(JsonElement schema, string type)
    {
        if (schema.GetProperty("type").GetString() != type) throw new InvalidDataException($"Schema type differs; expected {type}.");
    }

    private static void RequireEnum(JsonElement schema, params string[] values)
    {
        var actual = schema.GetProperty("enum").EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(values)) throw new InvalidDataException("Schema enum differs from validator.");
    }

    private static void RequirePattern(JsonElement schema, string pattern)
    {
        if (schema.GetProperty("pattern").GetString() != pattern) throw new InvalidDataException($"Schema pattern differs; expected {pattern}.");
    }

    private static void RequireFormat(JsonElement schema, string format)
    {
        if (schema.GetProperty("format").GetString() != format) throw new InvalidDataException($"Schema format differs; expected {format}.");
    }

    private static void RequireStringBounds(JsonElement schema, int minimum, int maximum)
    {
        RequireType(schema, "string");
        if (schema.GetProperty("minLength").GetInt32() != minimum || schema.GetProperty("maxLength").GetInt32() != maximum)
            throw new InvalidDataException($"Schema string bounds differ; expected {minimum}..{maximum}.");
    }

    private static void ValidateManifest(string protocolRoot)
    {
        string manifestPath = Path.Combine(protocolRoot, "manifest.json");
        ValidateProtocolJsonBytes(manifestPath);
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = manifest.RootElement;
        if (root.GetProperty("limits").GetProperty("maxFrameBytes").GetInt32() != ProtocolConstants.MaxFrameBytes ||
            root.GetProperty("limits").GetProperty("maxTextUtf8Bytes").GetInt32() != ProtocolConstants.MaxTextUtf8Bytes)
            throw new InvalidDataException("Manifest limits differ from validator.");
        var listedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string section in new[] { "schemas", "fixtures" })
        foreach (JsonElement entry in root.GetProperty(section).EnumerateArray())
        {
            string relative = entry.GetProperty("path").GetString()!;
            if (Path.IsPathRooted(relative) || relative.Contains("..", StringComparison.Ordinal)) throw new InvalidDataException("Manifest contains a non-portable path.");
            if (!listedPaths.Add(relative)) throw new InvalidDataException($"Manifest contains a duplicate path: {relative}");
            string path = Path.Combine(protocolRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            ValidateProtocolJsonBytes(path);
            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (actual != entry.GetProperty("sha256").GetString()) throw new InvalidDataException($"Manifest hash mismatch: {relative}");
        }
        if (listedPaths.Count != 64) throw new InvalidDataException($"Manifest must contain exactly 64 schema/fixture entries; found {listedPaths.Count}.");

        HashSet<string> actualPaths = Directory.EnumerateFiles(protocolRoot, "*.json", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, manifestPath, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(protocolRoot, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        if (!actualPaths.SetEquals(listedPaths)) throw new InvalidDataException("Manifest paths differ from the protocol schema/fixture JSON files on disk.");
    }

    private static void ValidateProtocolJsonBytes(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)) throw new InvalidDataException($"Protocol JSON contains a UTF-8 BOM: {path}");
        if (bytes.Contains((byte)'\r')) throw new InvalidDataException($"Protocol JSON contains a CR or CRLF line ending: {path}");
        if (bytes.Length == 0 || bytes[^1] != (byte)'\n' || (bytes.Length > 1 && bytes[^2] == (byte)'\n'))
            throw new InvalidDataException($"Protocol JSON must end with exactly one LF: {path}");
    }

    private static void ValidateAdversarialCoverage(string protocolRoot)
    {
        var requiredInvalid = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["capability-duplicate.json"] = "unsupported_capability",
            ["supported-version-zero.json"] = "unsupported_protocol_version",
            ["supported-version-negative.json"] = "unsupported_protocol_version",
            ["supported-version-missing-v1.json"] = "unsupported_protocol_version",
            ["supported-version-empty.json"] = "invalid_message",
            ["supported-version-non-integer.json"] = "unsupported_protocol_version",
            ["error-related-message-id-zero.json"] = "invalid_message",
            ["error-message-ascii-257.json"] = "invalid_message",
            ["error-message-chinese-257.json"] = "invalid_message",
            ["error-message-emoji-257.json"] = "invalid_message",
            ["timestamp-invalid-calendar.json"] = "invalid_timestamp"
        };
        foreach (var pair in requiredInvalid)
        {
            string path = Path.Combine(protocolRoot, "fixtures", "invalid", pair.Key);
            JsonObject fixture = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (fixture["expected"]!["accepted"]!.GetValue<bool>() ||
                fixture["expected"]!["errorCode"]!.GetValue<string>() != pair.Value)
                throw new InvalidDataException($"Adversarial fixture expectation differs: {pair.Key}.");
        }
        foreach (string file in new[] { "error-message-ascii-256.json", "error-message-chinese-256.json", "error-message-emoji-256.json" })
        {
            string path = Path.Combine(protocolRoot, "fixtures", "valid", file);
            JsonObject fixture = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (!fixture["expected"]!["accepted"]!.GetValue<bool>())
                throw new InvalidDataException($"Boundary fixture must be accepted: {file}.");
        }
    }

    private static void ValidateIsolation(string repositoryRoot)
    {
        string toolRoot = Path.Combine(repositoryRoot, "tools", "SyncProtocolContract");
        string source = string.Join('\n', Directory.EnumerateFiles(toolRoot, "*.cs").Select(File.ReadAllText));
        foreach (string forbidden in new[] { "Http" + "Client", "System." + "Net", "Clip" + "board", "LocalApplication" + "Data", "ClipShelf\\history" + ".json" })
            if (source.Contains(forbidden, StringComparison.Ordinal)) throw new InvalidDataException($"Validator source contains forbidden integration: {forbidden}");
        string project = File.ReadAllText(Path.Combine(toolRoot, "SyncProtocolContract.csproj"));
        if (project.Contains("PackageReference", StringComparison.Ordinal) || project.Contains("UseWPF", StringComparison.Ordinal))
            throw new InvalidDataException("Validator must remain dependency-free and UI-free.");
    }
}
