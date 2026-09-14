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
            var header = Convert.FromHexString(vector.GetProperty("header").GetString()!);
            var payload = Convert.FromHexString(vector.GetProperty("payload").GetString()!);
            Assert.Equal("NHID"u8.ToArray(), header[..4]);
            Assert.Equal(1, header[4]);
            Assert.Equal(Convert.ToByte(vector.GetProperty("type").GetString(), 16), header[5]);
            Assert.Equal(0UL, ReadBigEndian(header.AsSpan(6, 2)));
            Assert.Equal(vector.GetProperty("sequence").GetUInt64(), ReadBigEndian(header.AsSpan(8, 8)));
            Assert.Equal((ulong)payload.Length, ReadBigEndian(header.AsSpan(16, 4)));
        }
    }

    [Fact]
    public void SharedFixturePayloadLayoutsAndInvalidCasesAreUnambiguous()
    {
        using var fixture = LoadFixture();
        var root = fixture.RootElement;
        var messages = root.GetProperty("messages").EnumerateArray()
            .ToDictionary(
                vector => vector.GetProperty("name").GetString()!,
                vector => Convert.FromHexString(vector.GetProperty("payload").GetString()!),
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
        Assert.Equal(valid.GetProperty("header").GetString(), invalidTag.GetProperty("header").GetString());
        Assert.Equal(valid.GetProperty("payload").GetString(), invalidTag.GetProperty("payload").GetString());
        Assert.NotEqual(valid.GetProperty("tag").GetString(), invalidTag.GetProperty("tag").GetString());
        Assert.Equal(1UL, ReadBigEndian(Convert.FromHexString(valid.GetProperty("header").GetString()!).AsSpan(8, 8)));
        Assert.Equal(2UL, invalidSequence.GetProperty("expected_sequence").GetUInt64());
        Assert.Equal(3UL, invalidSequence.GetProperty("actual_sequence").GetUInt64());
        Assert.Equal(3UL, ReadBigEndian(Convert.FromHexString(invalidSequence.GetProperty("header").GetString()!).AsSpan(8, 8)));
        var declaredOversize = ReadBigEndian(Convert.FromHexString(oversize.GetProperty("header").GetString()!).AsSpan(16, 4));
        Assert.Equal(4097UL, declaredOversize);
        Assert.Equal(oversize.GetProperty("declared_payload_length").GetUInt64(), declaredOversize);
        Assert.True(declaredOversize > root.GetProperty("constants").GetProperty("maximum_payload_length").GetUInt64());
    }

    [Fact]
    public void WindowsProtocolApiConsumesSharedVectors()
    {
        using var fixture = LoadFixture();
        var frameBytes = Convert.FromHexString(
            fixture.RootElement.GetProperty("authenticated_frames").GetProperty("valid").GetProperty("frame").GetString()!);
        var frameType = Type.GetType("NetworkHidBridge.Core.Protocol.Frame, NetworkHidBridge.Core");

        Assert.NotNull(frameType);
        var parser = frameType.GetMethod("FromBytes", BindingFlags.Public | BindingFlags.Static, [typeof(byte[])]);
        Assert.NotNull(parser);
        Assert.NotNull(parser.Invoke(null, [frameBytes]));
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
