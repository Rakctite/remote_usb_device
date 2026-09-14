using System.Reflection;
using System.Text.Json;
using System.Buffers.Binary;

namespace NetworkHidBridge.Core.Tests.Protocol;

public sealed class ProtocolFixtureTests
{
    [Fact]
    public void SharedFixtureHasValidSchemaAndLiteralHex()
    {
        using var fixture = LoadFixture();
        var root = fixture.RootElement;
        var constants = root.GetProperty("constants");

        Assert.Equal("network-hid-bridge/protocol-v1", root.GetProperty("contract").GetString());
        Assert.Equal("lowercase-hex", root.GetProperty("encoding").GetString());
        Assert.Equal(20, constants.GetProperty("header_length").GetInt32());
        Assert.Equal(16, constants.GetProperty("tag_length").GetInt32());
        Assert.Equal(4096, constants.GetProperty("maximum_payload_length").GetInt32());
        Assert.Equal("4e484944", constants.GetProperty("magic").GetString());
        Assert.Equal("01", constants.GetProperty("version").GetString());
        Assert.Equal("0000", constants.GetProperty("flags").GetString());
        foreach (var name in new[] { "psk", "client_nonce", "server_nonce", "server_proof", "client_proof", "session_key" })
        {
            AssertLowercaseHex(constants.GetProperty(name).GetString()!, 32);
        }

        var handshakeLengths = new Dictionary<string, int>
        {
            ["01"] = 32,
            ["02"] = 64,
            ["03"] = 32,
        };
        foreach (var vector in root.GetProperty("handshake").EnumerateArray())
        {
            var header = vector.GetProperty("header").GetString()!;
            var payload = vector.GetProperty("payload").GetString()!;
            AssertLowercaseHex(vector.GetProperty("type").GetString()!, 1);
            AssertLowercaseHex(header, 20);
            AssertLowercaseHex(payload);
            Assert.Equal(header + payload, vector.GetProperty("frame").GetString());
            Assert.Equal(0UL, vector.GetProperty("sequence").GetUInt64());
            Assert.Equal(handshakeLengths[vector.GetProperty("type").GetString()!], payload.Length / 2);
        }

        var messageTypes = root.GetProperty("messages").EnumerateArray()
            .Select(vector => vector.GetProperty("type").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string?> { "10", "11", "12", "13", "14", "20", "21" }, messageTypes);

        var messageLengths = new Dictionary<string, int>
        {
            ["10"] = 8,
            ["11"] = 13,
            ["12"] = 2,
            ["13"] = 11,
            ["14"] = 0,
            ["20"] = 8,
            ["21"] = 8,
        };
        foreach (var vector in root.GetProperty("messages").EnumerateArray())
        {
            AssertLowercaseHex(vector.GetProperty("header").GetString()!, 20);
            Assert.Equal(messageLengths[vector.GetProperty("type").GetString()!], vector.GetProperty("payload_length").GetInt32());
            AssertLowercaseHex(
                vector.GetProperty("payload").GetString()!,
                vector.GetProperty("payload_length").GetInt32());
        }

        var authenticated = root.GetProperty("authenticated_frames");
        foreach (var name in new[] { "valid", "invalid_tag", "invalid_sequence" })
        {
            var vector = authenticated.GetProperty(name);
            var header = vector.GetProperty("header").GetString()!;
            var payload = vector.GetProperty("payload").GetString()!;
            var tag = vector.GetProperty("tag").GetString()!;
            AssertLowercaseHex(header, 20);
            AssertLowercaseHex(payload);
            AssertLowercaseHex(tag, 16);
            var frame = vector.GetProperty("frame").GetString()!;
            AssertLowercaseHex(frame);
            Assert.Equal(header + payload + tag, frame);
        }
        AssertLowercaseHex(authenticated.GetProperty("oversize").GetProperty("header").GetString()!, 20);
    }

    [Fact]
    public void SharedFixtureHeadersEncodeDeclaredFields()
    {
        using var fixture = LoadFixture();
        var root = fixture.RootElement;
        var vectors = root.GetProperty("handshake").EnumerateArray()
            .Concat(root.GetProperty("messages").EnumerateArray());

        foreach (var vector in vectors)
        {
            var name = vector.GetProperty("name").GetString();
            var header = Convert.FromHexString(vector.GetProperty("header").GetString()!);
            var payload = Convert.FromHexString(vector.GetProperty("payload").GetString()!);
            Assert.True(header.AsSpan(0, 4).SequenceEqual("NHID"u8), $"{name}: magic");
            Assert.True(header[4] == 1, $"{name}: version");
            Assert.True(header[5] == Convert.ToByte(vector.GetProperty("type").GetString(), 16), $"{name}: message type");
            Assert.True(ReadBigEndian(header.AsSpan(6, 2)) == 0UL, $"{name}: flags");
            Assert.True(ReadBigEndian(header.AsSpan(8, 8)) == vector.GetProperty("sequence").GetUInt64(), $"{name}: sequence");
            Assert.True(ReadBigEndian(header.AsSpan(16, 4)) == (ulong)payload.Length, $"{name}: payload length");
        }
    }

    [Fact]
    public void SharedFixturePayloadLayoutsAndInvalidCasesAreUnambiguous()
    {
        using var fixture = LoadFixture();
        var root = fixture.RootElement;
        var messageVectors = root.GetProperty("messages").EnumerateArray()
            .ToDictionary(
                vector => vector.GetProperty("name").GetString()!,
                vector => vector.Clone(),
                StringComparer.Ordinal);
        var messages = messageVectors
            .ToDictionary(
                pair => pair.Key,
                pair => Convert.FromHexString(pair.Value.GetProperty("payload").GetString()!),
                StringComparer.Ordinal);
        var mouse = messages["mouse_state"];
        var full = messages["full_state"];

        Assert.Equal(5, mouse[0]);
        Assert.Equal(-123456, BinaryPrimitives.ReadInt32BigEndian(mouse.AsSpan(1, 4)));
        Assert.Equal(654321, BinaryPrimitives.ReadInt32BigEndian(mouse.AsSpan(5, 4)));
        Assert.Equal(-120, BinaryPrimitives.ReadInt16BigEndian(mouse.AsSpan(9, 2)));
        Assert.Equal(120, BinaryPrimitives.ReadInt16BigEndian(mouse.AsSpan(11, 2)));
        Assert.Equal(messages["keyboard_state"], full[..8]);
        Assert.Equal(mouse[0], full[8]);
        Assert.Equal(messages["consumer_state"], full[9..11]);
        Assert.Empty(messages["release_all"]);
        Assert.Equal(messages["heartbeat"], messages["heartbeat_ack"]);

        var authenticated = root.GetProperty("authenticated_frames");
        var valid = authenticated.GetProperty("valid");
        var invalidTag = authenticated.GetProperty("invalid_tag");
        var invalidSequence = authenticated.GetProperty("invalid_sequence");
        var oversize = authenticated.GetProperty("oversize");
        var handshake = root.GetProperty("handshake").EnumerateArray()
            .ToDictionary(
                vector => vector.GetProperty("name").GetString()!,
                vector => vector.Clone(),
                StringComparer.Ordinal);
        var constants = root.GetProperty("constants");
        AssertFieldEqual(
            constants.GetProperty("client_nonce").GetString()!,
            handshake["client_hello"].GetProperty("payload").GetString(),
            "client_hello.payload");
        AssertFieldEqual(
            constants.GetProperty("server_nonce").GetString() + constants.GetProperty("server_proof").GetString(),
            handshake["server_hello"].GetProperty("payload").GetString(),
            "server_hello.payload");
        AssertFieldEqual(
            constants.GetProperty("client_proof").GetString()!,
            handshake["client_auth"].GetProperty("payload").GetString(),
            "client_auth.payload");

        var referencedMessage = messageVectors[valid.GetProperty("message").GetString()!];
        AssertFieldEqual(
            referencedMessage.GetProperty("header").GetString()!,
            valid.GetProperty("header").GetString(),
            "authenticated_frames.valid.header");
        AssertFieldEqual(
            referencedMessage.GetProperty("payload").GetString()!,
            valid.GetProperty("payload").GetString(),
            "authenticated_frames.valid.payload");
        AssertFieldEqual("4f8aa829f97fc236254a3d176abc6c63", valid.GetProperty("tag").GetString(), "authenticated_frames.valid.tag");
        AssertFieldEqual(
            valid.GetProperty("header").GetString() + valid.GetProperty("payload").GetString() + valid.GetProperty("tag").GetString(),
            valid.GetProperty("frame").GetString(),
            "authenticated_frames.valid.frame");
        AssertFieldEqual("authentication", invalidTag.GetProperty("expected_error").GetString(), "authenticated_frames.invalid_tag.expected_error");
        AssertFieldEqual(valid.GetProperty("message").GetString()!, invalidTag.GetProperty("message").GetString(), "authenticated_frames.invalid_tag.message");
        AssertFieldEqual(valid.GetProperty("header").GetString()!, invalidTag.GetProperty("header").GetString(), "authenticated_frames.invalid_tag.header");
        AssertFieldEqual(valid.GetProperty("payload").GetString()!, invalidTag.GetProperty("payload").GetString(), "authenticated_frames.invalid_tag.payload");
        AssertFieldEqual("0f8aa829f97fc236254a3d176abc6c63", invalidTag.GetProperty("tag").GetString(), "authenticated_frames.invalid_tag.tag");
        var validFrame = Convert.FromHexString(valid.GetProperty("frame").GetString()!);
        var invalidTagFrame = Convert.FromHexString(invalidTag.GetProperty("frame").GetString()!);
        Assert.True(validFrame.AsSpan(..^16).SequenceEqual(invalidTagFrame.AsSpan(..^16)), "authenticated_frames.invalid_tag.frame body");
        Assert.False(validFrame.AsSpan(^16..).SequenceEqual(invalidTagFrame.AsSpan(^16..)), "authenticated_frames.invalid_tag.frame tag");
        Assert.Equal(1UL, ReadBigEndian(Convert.FromHexString(valid.GetProperty("header").GetString()!).AsSpan(8, 8)));
        AssertFieldEqual("sequence", invalidSequence.GetProperty("expected_error").GetString(), "authenticated_frames.invalid_sequence.expected_error");
        AssertFieldEqual(valid.GetProperty("message").GetString()!, invalidSequence.GetProperty("message").GetString(), "authenticated_frames.invalid_sequence.message");
        Assert.Equal(2UL, invalidSequence.GetProperty("expected_sequence").GetUInt64());
        Assert.Equal(3UL, invalidSequence.GetProperty("actual_sequence").GetUInt64());
        var validHeader = Convert.FromHexString(valid.GetProperty("header").GetString()!);
        var invalidSequenceHeader = Convert.FromHexString(invalidSequence.GetProperty("header").GetString()!);
        Assert.True(validHeader.AsSpan(..8).SequenceEqual(invalidSequenceHeader.AsSpan(..8)), "authenticated_frames.invalid_sequence.header prefix");
        Assert.True(validHeader.AsSpan(16..).SequenceEqual(invalidSequenceHeader.AsSpan(16..)), "authenticated_frames.invalid_sequence.header length");
        Assert.Equal(3UL, ReadBigEndian(invalidSequenceHeader.AsSpan(8, 8)));
        AssertFieldEqual(valid.GetProperty("payload").GetString()!, invalidSequence.GetProperty("payload").GetString(), "authenticated_frames.invalid_sequence.payload");
        AssertFieldEqual("b8042bd2e9aa2886ae840a83476687e2", invalidSequence.GetProperty("tag").GetString(), "authenticated_frames.invalid_sequence.tag");
        var oversizeHeader = Convert.FromHexString(oversize.GetProperty("header").GetString()!);
        AssertFieldEqual("payload-too-large", oversize.GetProperty("expected_error").GetString(), "authenticated_frames.oversize.expected_error");
        AssertFieldEqual(valid.GetProperty("message").GetString()!, oversize.GetProperty("message").GetString(), "authenticated_frames.oversize.message");
        Assert.True(validHeader.AsSpan(..8).SequenceEqual(oversizeHeader.AsSpan(..8)), "authenticated_frames.oversize.header prefix");
        Assert.Equal(2UL, ReadBigEndian(oversizeHeader.AsSpan(8, 8)));
        var declaredOversize = ReadBigEndian(oversizeHeader.AsSpan(16, 4));
        Assert.Equal(oversize.GetProperty("declared_payload_length").GetUInt64(), declaredOversize);
        Assert.Equal(constants.GetProperty("maximum_payload_length").GetUInt64() + 1, declaredOversize);
    }

    [Fact]
    public void WindowsFrameCodecDecodeAuthenticatedConsumesSharedVector()
    {
        using var fixture = LoadFixture();
        var frameBytes = Convert.FromHexString(
            fixture.RootElement.GetProperty("authenticated_frames").GetProperty("valid").GetProperty("frame").GetString()!);
        var sessionKey = Convert.FromHexString(fixture.RootElement.GetProperty("constants").GetProperty("session_key").GetString()!);
        var codecType = Type.GetType("NetworkHidBridge.Core.Protocol.FrameCodec, NetworkHidBridge.Core");

        Assert.True(
            codecType is not null,
            "Missing planned API NetworkHidBridge.Core.Protocol.FrameCodec.DecodeAuthenticated");
        var decoder = codecType!.GetMethod(
            "DecodeAuthenticated",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(byte[]), typeof(byte[]), typeof(ulong)],
            modifiers: null);
        Assert.True(
            decoder is not null,
            "Missing exact planned API FrameCodec.DecodeAuthenticated(byte[], byte[], ulong)");
        Assert.Equal("NetworkHidBridge.Core.Protocol.Frame", decoder!.ReturnType.FullName);
        Assert.IsType(decoder.ReturnType, decoder.Invoke(null, [frameBytes, sessionKey, 1UL]));
    }

    private static JsonDocument LoadFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "tests", "fixtures", "protocol-v1.json");
            if (File.Exists(path))
            {
                return JsonDocument.Parse(File.ReadAllText(path));
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate tests/fixtures/protocol-v1.json from the test output directory.");
    }

    private static void AssertLowercaseHex(string value, int? byteLength = null)
    {
        Assert.Equal(0, value.Length % 2);
        Assert.All(value, character => Assert.Contains(character, "0123456789abcdef"));
        if (byteLength is not null)
        {
            Assert.Equal(byteLength.Value * 2, value.Length);
        }
    }

    private static void AssertFieldEqual(string expected, string? actual, string field)
    {
        Assert.True(StringComparer.Ordinal.Equals(expected, actual), $"{field}: literal value mismatch");
    }

    private static ulong ReadBigEndian(ReadOnlySpan<byte> value)
    {
        ulong result = 0;
        foreach (var item in value)
        {
            result = (result << 8) | item;
        }
        return result;
    }
}
