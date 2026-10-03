using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Eslee.QuickSend.Windows.Persistence;

namespace Eslee.QuickSend.Windows.Security;

public sealed class DeviceIdentityService(AppDatabase database, DeviceIdentity? suppliedIdentity = null)
{
    private const string CertificateSubject = "CN=eslee QuickSend Device";
    private DeviceIdentity? _cached = suppliedIdentity;

    public async ValueTask<DeviceIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null) return _cached;
        var deviceId = await database.GetSettingAsync("device.id", cancellationToken);
        if (deviceId is null)
        {
            deviceId = Guid.NewGuid().ToString("N");
            await database.SetSettingAsync("device.id", deviceId, cancellationToken);
        }

        var thumbprint = await database.GetSettingAsync("device.certificate.thumbprint", cancellationToken);
        var certificate = FindCertificate(thumbprint);
        if (certificate is null)
        {
            certificate = CreateCertificate(deviceId);
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(certificate);
            await database.SetSettingAsync("device.certificate.thumbprint", certificate.Thumbprint, cancellationToken);
        }

        _cached = new DeviceIdentity(deviceId, certificate, Fingerprint(certificate));
        return _cached;
    }

    public static string Fingerprint(X509Certificate2 certificate)
    {
        using var key = certificate.GetECDsaPublicKey() ?? throw new CryptographicException("QuickSend identity certificate must use ECDSA.");
        return Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
    }

    public static string PairingCode(string localFingerprint, string remoteFingerprint, string nonce)
    {
        var fingerprints = new[] { localFingerprint, remoteFingerprint };
        Array.Sort(fingerprints, StringComparer.Ordinal);
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{fingerprints[0]}|{fingerprints[1]}|{nonce}");
        var hash = SHA256.HashData(bytes);
        var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000;
        return value.ToString("000 000", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static X509Certificate2? FindCertificate(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) return null;
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        return matches.OfType<X509Certificate2>().FirstOrDefault(static c => c.HasPrivateKey && c.NotAfter > DateTime.UtcNow.AddDays(30));
    }

    private static X509Certificate2 CreateCertificate(string deviceId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(new X500DistinguishedName(CertificateSubject), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.57264.1.1", System.Text.Encoding.UTF8.GetBytes(deviceId), false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
    }
}

public sealed record DeviceIdentity(string DeviceId, X509Certificate2 Certificate, string Fingerprint);
