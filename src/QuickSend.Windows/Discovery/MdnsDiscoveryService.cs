using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Eslee.QuickSend.Core.Devices;
using Eslee.QuickSend.Core.Protocol;
using Makaretu.Dns;
using Windows.Networking.Connectivity;

namespace Eslee.QuickSend.Windows.Discovery;

public sealed class MdnsDiscoveryService : IDisposable
{
    private readonly ConcurrentDictionary<string, DiscoveredDevice> _devices = new(StringComparer.Ordinal);
    private MulticastService? _mdns;
    private ServiceDiscovery? _serviceDiscovery;
    private ServiceProfile? _profile;
    private string? _localDeviceId;
    private Timer? _expiryTimer;
    private InterfaceSnapshot[] _selectedInterfaces = [];

    public event EventHandler? DeviceChanged;
    public IReadOnlyCollection<DiscoveredDevice> Devices => _devices.Values.ToArray();

    public async Task StartAsync(string deviceId, string deviceName, string fingerprint, int port)
    {
        if (_mdns is not null) return;
        await AppServices.Log.InfoAsync("mdns.start.begin", new
        {
            serviceType = ProtocolConstants.ServiceType,
            port,
            networkProfile = ConnectionProfileSnapshot()
        }).ConfigureAwait(false);

        try
        {
            var selected = SelectPreferredInterfaces(MulticastService.GetNetworkInterfaces()).ToArray();
            _selectedInterfaces = DescribeInterfaces(selected);
            var addresses = SelectedIpv4Addresses(selected);
            if (selected.Length == 0 || addresses.Length == 0)
                throw new InvalidOperationException("No active multicast-capable LAN IPv4 interface is available.");

            foreach (var item in _selectedInterfaces)
                await AppServices.Log.InfoAsync("mdns.interface.selected", item).ConfigureAwait(false);

            _localDeviceId = deviceId;
            _mdns = new MulticastService(SelectPreferredInterfaces) { UseIpv4 = true };
            _serviceDiscovery = new ServiceDiscovery(_mdns);
            _mdns.AnswerReceived += OnAnswerReceived;
            _mdns.NetworkInterfaceDiscovered += OnNetworkInterfaceDiscovered;
            _mdns.MalformedMessage += OnMalformedMessage;
            _serviceDiscovery.ServiceInstanceShutdown += OnInstanceShutdown;
            _mdns.Start();

            _profile = new ServiceProfile(deviceId, ProtocolConstants.ServiceType, checked((ushort)port), addresses);
            var advertisedName = EncodeAdvertisedName(deviceName, deviceId);
            var txtRecords = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id"] = deviceId,
                ["name"] = advertisedName,
                ["fp"] = fingerprint,
                ["v"] = ProtocolConstants.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            foreach (var record in txtRecords) _profile.AddProperty(record.Key, record.Value);

            await AppServices.Log.InfoAsync("mdns.service.type", new
            {
                apiValue = ProtocolConstants.ServiceType,
                wireValue = WithTrailingDot(_profile.QualifiedServiceName.ToString())
            }).ConfigureAwait(false);
            await AppServices.Log.InfoAsync("mdns.service.name", new
            {
                instanceName = _profile.InstanceName.ToString(),
                fullyQualifiedName = WithTrailingDot(_profile.FullyQualifiedName.ToString()),
                hostName = WithTrailingDot(_profile.HostName.ToString())
            }).ConfigureAwait(false);
            await AppServices.Log.InfoAsync("mdns.port", new { advertisedPort = port }).ConfigureAwait(false);
            await AppServices.Log.InfoAsync("mdns.txt.records", txtRecords).ConfigureAwait(false);

            _serviceDiscovery.Advertise(_profile);
            _serviceDiscovery.QueryServiceInstances(ProtocolConstants.ServiceType);
            _expiryTimer = new Timer(ExpireDevices, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            await AppServices.Log.InfoAsync("mdns.advertise.success", new
            {
                service = WithTrailingDot(_profile.FullyQualifiedName.ToString()),
                addresses = addresses.Select(static address => address.ToString()).ToArray(),
                port
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await AppServices.Log.ErrorAsync("mdns.advertise.failed", ex, new
            {
                serviceType = ProtocolConstants.ServiceType,
                port,
                selectedInterfaces = _selectedInterfaces
            }).ConfigureAwait(false);
            CleanupAfterFailedStart();
            throw;
        }
    }

    public Task RefreshAsync()
    {
        _serviceDiscovery?.QueryServiceInstances(ProtocolConstants.ServiceType);
        return Task.CompletedTask;
    }

    public DiscoveredDevice? Find(string deviceId) => _devices.TryGetValue(deviceId, out var device) ? device : null;

    private void OnAnswerReceived(object? sender, MessageEventArgs e)
    {
        try
        {
            var records = e.Message.Answers.Concat(e.Message.AdditionalRecords).ToArray();
            foreach (var srv in records.OfType<SRVRecord>())
            {
                var fullName = srv.Name.ToString();
                if (!fullName.Contains(ProtocolConstants.ServiceType, StringComparison.OrdinalIgnoreCase)) continue;
                var txt = records.OfType<TXTRecord>().FirstOrDefault(r => r.Name.ToString() == fullName);
                var properties = ParseProperties(txt?.Strings);
                if (!properties.TryGetValue("id", out var id) || id == _localDeviceId) continue;
                properties.TryGetValue("name", out var advertisedName);
                properties.TryGetValue("fp", out var fingerprint);
                var name = DeviceNameWire.Decode(advertisedName);
                var address = records.OfType<AddressRecord>().FirstOrDefault(r => r.Name.Equals(srv.Target))?.Address
                    ?? e.RemoteEndPoint.Address;
                _devices[id] = new DiscoveredDevice(id, name.Length == 0 ? id : name, address, srv.Port, fingerprint ?? string.Empty, true, DateTimeOffset.UtcNow);
                AppServices.Log.Info("mdns.peer.resolved", new { deviceId = id, name, address = address.ToString(), port = srv.Port, service = fullName });
                DeviceChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            AppServices.Log.Warn("discovery.answer.invalid", new { error = ex.Message });
        }
    }

    private void OnInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        var match = _devices.Values.FirstOrDefault(d => e.ServiceInstanceName.ToString().StartsWith(d.DeviceId, StringComparison.Ordinal));
        if (match is null) return;
        _devices[match.DeviceId] = match with { IsOnline = false, LastSeen = DateTimeOffset.UtcNow };
        DeviceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnNetworkInterfaceDiscovered(object? sender, NetworkInterfaceEventArgs e)
    {
        foreach (var item in DescribeInterfaces(SelectPreferredInterfaces(e.NetworkInterfaces)))
            AppServices.Log.Info("mdns.interface.discovered", item);
    }

    private static void OnMalformedMessage(object? sender, byte[] message) =>
        AppServices.Log.Warn("mdns.message.malformed", new { byteCount = message.Length });

    private static Dictionary<string, string> ParseProperties(IEnumerable<string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values is null) return result;
        foreach (var value in values)
        {
            var split = value.IndexOf('=');
            if (split > 0) result[value[..split]] = value[(split + 1)..];
        }
        return result;
    }

    private void ExpireDevices(object? state)
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-35);
        var changed = false;
        foreach (var entry in _devices)
        {
            if (entry.Value.IsOnline && entry.Value.LastSeen < cutoff)
            {
                _devices[entry.Key] = entry.Value with { IsOnline = false };
                changed = true;
            }
        }
        if (changed) DeviceChanged?.Invoke(this, EventArgs.Empty);
        _serviceDiscovery?.QueryServiceInstances(ProtocolConstants.ServiceType);
    }

    public void Dispose()
    {
        _expiryTimer?.Dispose();
        _expiryTimer = null;
        try
        {
            _serviceDiscovery?.Unadvertise();
        }
        catch (Exception ex)
        {
            AppServices.Log.Warn("discovery.unadvertise.failed", new { error = ex.Message });
        }
        finally
        {
            _serviceDiscovery?.Dispose();
            _mdns?.Dispose();
            _profile = null;
            _serviceDiscovery = null;
            _mdns = null;
        }
    }

    /// <summary>
    /// Rewrites only the TXT <c>name</c> value and re-announces the existing profile.
    /// The instance name, service type, port, <c>id</c> and <c>fp</c> stay identical, so
    /// peers keep the same device entry and no re-pairing is triggered.
    /// </summary>
    public async Task UpdateDeviceNameAsync(string deviceName)
    {
        if (_serviceDiscovery is null || _profile is null || _localDeviceId is null) return;
        var advertisedName = EncodeAdvertisedName(deviceName, _localDeviceId);
        var txtRecords = _profile.Resources.OfType<TXTRecord>().ToArray();
        var txt = txtRecords.FirstOrDefault(record => record.Name.Equals(_profile.FullyQualifiedName))
            ?? txtRecords.FirstOrDefault();
        if (txt is null)
        {
            await AppServices.Log.WarnAsync("mdns.txt.name.update.skipped", new { reason = "no TXT record on the advertised profile" }).ConfigureAwait(false);
            return;
        }

        var replaced = false;
        for (var i = 0; i < txt.Strings.Count; i++)
        {
            if (!txt.Strings[i].StartsWith("name=", StringComparison.Ordinal)) continue;
            txt.Strings[i] = $"name={advertisedName}";
            replaced = true;
        }
        if (!replaced) txt.Strings.Add($"name={advertisedName}");
        _serviceDiscovery.Announce(_profile);
        await AppServices.Log.InfoAsync("mdns.txt.name.updated", new
        {
            advertisedName,
            service = WithTrailingDot(_profile.FullyQualifiedName.ToString())
        }).ConfigureAwait(false);
    }

    private static string EncodeAdvertisedName(string deviceName, string deviceId)
    {
        var encoded = DeviceNameWire.Encode(DeviceNameRules.NormalizeOrFallback(deviceName, string.Empty));
        if (encoded.Length > 0) return encoded;
        var safeId = new string(deviceId.Where(static character => char.IsAsciiLetterOrDigit(character)).Take(8).ToArray());
        return $"QuickSend-{(safeId.Length == 0 ? "Device" : safeId)}";
    }

    private void CleanupAfterFailedStart()
    {
        try { _serviceDiscovery?.Dispose(); }
        catch { }
        try { _mdns?.Dispose(); }
        catch { }
        _profile = null;
        _serviceDiscovery = null;
        _mdns = null;
    }

    private static IEnumerable<NetworkInterface> SelectPreferredInterfaces(IEnumerable<NetworkInterface> candidates)
    {
        var ipv4 = candidates
            .Where(static nic => nic.OperationalStatus == OperationalStatus.Up)
            .Where(static nic => nic.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .Where(static nic => nic.SupportsMulticast)
            .Where(static nic => Ipv4Unicast(nic).Any())
            .ToArray();
        var physical = ipv4.Where(static nic => nic.NetworkInterfaceType is
            NetworkInterfaceType.Ethernet or
            NetworkInterfaceType.Ethernet3Megabit or
            NetworkInterfaceType.FastEthernetFx or
            NetworkInterfaceType.FastEthernetT or
            NetworkInterfaceType.GigabitEthernet or
            NetworkInterfaceType.Wireless80211).ToArray();
        var physicalWithGateway = physical.Where(HasIpv4Gateway).ToArray();
        if (physicalWithGateway.Length > 0) return physicalWithGateway;
        if (physical.Length > 0) return physical;
        var withGateway = ipv4.Where(HasIpv4Gateway).ToArray();
        return withGateway.Length > 0 ? withGateway : ipv4;
    }

    private static IPAddress[] SelectedIpv4Addresses(IEnumerable<NetworkInterface> interfaces) => interfaces
        .SelectMany(Ipv4Unicast)
        .Select(static item => item.Address)
        .Distinct()
        .ToArray();

    private static IEnumerable<UnicastIPAddressInformation> Ipv4Unicast(NetworkInterface nic) => nic
        .GetIPProperties().UnicastAddresses
        .Where(static item => item.Address.AddressFamily == AddressFamily.InterNetwork)
        .Where(static item => !IPAddress.IsLoopback(item.Address))
        .Where(static item => !item.Address.GetAddressBytes().AsSpan(0, 2).SequenceEqual(new byte[] { 169, 254 }));

    private static bool HasIpv4Gateway(NetworkInterface nic) => nic.GetIPProperties().GatewayAddresses
        .Any(static gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any));

    private static InterfaceSnapshot[] DescribeInterfaces(IEnumerable<NetworkInterface> interfaces) => interfaces
        .SelectMany(nic => Ipv4Unicast(nic).Select(address => new InterfaceSnapshot(
            nic.Id,
            nic.Name,
            nic.Description,
            nic.NetworkInterfaceType.ToString(),
            address.Address.ToString(),
            address.PrefixLength,
            HasIpv4Gateway(nic))))
        .ToArray();

    private static object ConnectionProfileSnapshot()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            return new
            {
                name = profile?.ProfileName,
                connectivity = profile?.GetNetworkConnectivityLevel().ToString(),
                transport = profile?.IsWlanConnectionProfile == true ? "WiFi" :
                    profile?.IsWwanConnectionProfile == true ? "Cellular" : "EthernetOrOther"
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.GetType().Name, message = ex.Message };
        }
    }

    private static string WithTrailingDot(string value) => value.EndsWith('.') ? value : value + ".";

    private sealed record InterfaceSnapshot(
        string Id,
        string Name,
        string Description,
        string InterfaceType,
        string Ipv4,
        int PrefixLength,
        bool HasDefaultGateway);
}

public sealed record DiscoveredDevice(
    string DeviceId,
    string Name,
    IPAddress Address,
    int Port,
    string IdentityFingerprint,
    bool IsOnline,
    DateTimeOffset LastSeen);
