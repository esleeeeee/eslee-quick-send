namespace Eslee.QuickSend.Core.Devices;

public static class PeerIdentity
{
    public static bool MatchesFingerprint(string expected, string actual) =>
        !string.IsNullOrWhiteSpace(expected) && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    public static bool CanAuthenticate(string expected, string actual, bool pairingOnly, IReadOnlySet<string> trusted) =>
        MatchesFingerprint(expected, actual) && (pairingOnly || trusted.Contains(expected));

    public static void ValidateSelected(string expectedId, string expectedFingerprint, string actualId, string actualFingerprint)
    {
        if (!string.Equals(expectedId, actualId, StringComparison.Ordinal) || !MatchesFingerprint(expectedFingerprint, actualFingerprint))
            throw new IOException("The authenticated peer is not the selected destination.");
    }
}
