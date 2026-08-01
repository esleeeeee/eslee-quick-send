using System.Text;

namespace Eslee.QuickSend.Core.Devices;

/// <summary>
/// Transport encoding for the DNS-SD TXT <c>name</c> value.
/// </summary>
/// <remarks>
/// TXT values must stay printable ASCII for the Windows mDNS writer, but QuickSend
/// display names may contain Hangul. The value is percent-encoded UTF-8, so a plain
/// ASCII name is transmitted unchanged and the key keeps its original meaning.
/// The <c>id</c> and <c>fp</c> keys are unaffected.
/// </remarks>
public static class DeviceNameWire
{
    public static string Encode(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var builder = new StringBuilder(name.Length);
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            if (IsSafe(b)) builder.Append((char)b);
            else builder.Append('%').Append(b.ToString("X2"));
        }
        return builder.ToString();
    }

    public static string Decode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (!value.Contains('%')) return value;
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length &&
                TryHex(value[i + 1], out var high) && TryHex(value[i + 2], out var low))
            {
                bytes.Add((byte)((high << 4) | low));
                i += 2;
                continue;
            }
            bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool IsSafe(byte value) =>
        value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
            || value is (byte)' ' or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'(' or (byte)')';

    private static bool TryHex(char character, out int value)
    {
        value = character switch
        {
            >= '0' and <= '9' => character - '0',
            >= 'a' and <= 'f' => character - 'a' + 10,
            >= 'A' and <= 'F' => character - 'A' + 10,
            _ => -1
        };
        return value >= 0;
    }
}
