using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Eslee.QuickSend.Windows.Diagnostics;
using Eslee.QuickSend.Windows.Persistence;

namespace Eslee.QuickSend.Windows.Security;

public sealed class TlsChannelFactory(
    TrustedDeviceStore trust,
    DeviceIdentityService identityService,
    DiagnosticLog log)
{
    public async ValueTask<SslStream> ConnectAsync(string host, int port, bool pairingOnly, CancellationToken cancellationToken)
    {
        var identity = await identityService.GetOrCreateAsync(cancellationToken);
        var trusted = await trust.GetFingerprintsAsync(cancellationToken);
        var client = new TcpClient { NoDelay = true, SendBufferSize = 4 * 1024 * 1024, ReceiveBufferSize = 4 * 1024 * 1024 };
        await log.InfoAsync("outgoing.tcp.begin", new { host, port }).ConfigureAwait(false);
        try
        {
            await client.ConnectAsync(host, port, cancellationToken);
            await log.InfoAsync("outgoing.tcp.success", new
            {
                host,
                port,
                localEndpoint = client.Client.LocalEndPoint?.ToString(),
                remoteEndpoint = client.Client.RemoteEndPoint?.ToString()
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            client.Dispose();
            await log.ErrorAsync("outgoing.tcp.failed", ex, new { host, port }).ConfigureAwait(false);
            throw;
        }

        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, certificate, _, _) =>
            certificate is not null && (pairingOnly || trusted.Contains(DeviceIdentityService.Fingerprint(new X509Certificate2(certificate)))));
        try
        {
            await log.InfoAsync("outgoing.tls.begin", new { host, port, pairingOnly, trustedCount = trusted.Count }).ConfigureAwait(false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "eslee-quicksend.local",
                ClientCertificates = [identity.Certificate],
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cancellationToken);
            var remoteCertificate = ssl.RemoteCertificate is null ? null : new X509Certificate2(ssl.RemoteCertificate);
            await log.InfoAsync("outgoing.tls.success", new
            {
                host,
                port,
                protocol = ssl.SslProtocol.ToString(),
                cipher = ssl.NegotiatedCipherSuite.ToString(),
                remoteFingerprint = remoteCertificate is null ? null : DeviceIdentityService.Fingerprint(remoteCertificate)
            }).ConfigureAwait(false);
            return ssl;
        }
        catch (Exception ex)
        {
            await log.ErrorAsync("outgoing.tls.failed", ex, new { host, port, pairingOnly }).ConfigureAwait(false);
            await ssl.DisposeAsync();
            client.Dispose();
            throw new TlsHandshakeException(host, port, ex);
        }
    }

    public async ValueTask<SslStream> AcceptAsync(TcpClient client, bool allowPairing, CancellationToken cancellationToken)
    {
        var identity = await identityService.GetOrCreateAsync(cancellationToken);
        var trusted = await trust.GetFingerprintsAsync(cancellationToken);
        var remoteEndpoint = client.Client.RemoteEndPoint?.ToString();
        client.NoDelay = true;
        client.SendBufferSize = 4 * 1024 * 1024;
        client.ReceiveBufferSize = 4 * 1024 * 1024;
        var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
            certificate is not null && (allowPairing || trusted.Contains(DeviceIdentityService.Fingerprint(new X509Certificate2(certificate)))));
        try
        {
            await log.InfoAsync("incoming.tls.begin", new { remoteEndpoint, allowPairing, trustedCount = trusted.Count }).ConfigureAwait(false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = identity.Certificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cancellationToken);
            var remoteCertificate = ssl.RemoteCertificate is null ? null : new X509Certificate2(ssl.RemoteCertificate);
            await log.InfoAsync("incoming.tls.success", new
            {
                remoteEndpoint,
                protocol = ssl.SslProtocol.ToString(),
                cipher = ssl.NegotiatedCipherSuite.ToString(),
                remoteFingerprint = remoteCertificate is null ? null : DeviceIdentityService.Fingerprint(remoteCertificate)
            }).ConfigureAwait(false);
            return ssl;
        }
        catch (Exception ex)
        {
            await log.ErrorAsync("incoming.tls.failed", ex, new { remoteEndpoint, allowPairing }).ConfigureAwait(false);
            await ssl.DisposeAsync();
            client.Dispose();
            throw;
        }
    }
}

public sealed class TlsHandshakeException(string host, int port, Exception innerException)
    : IOException($"TLS handshake with {host}:{port} failed.", innerException);
