using System.Text.Json;

namespace Eslee.QuickSend.Core.Protocol;

public static class ControlFrameCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static byte[] Serialize<T>(T message) where T : notnull =>
        JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);

    public static T Deserialize<T>(ReadOnlySpan<byte> payload) where T : notnull =>
        JsonSerializer.Deserialize<T>(payload, JsonOptions)
        ?? throw new ProtocolException($"Invalid {typeof(T).Name} payload.");
}

