using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Makaretu.Dns;

const string serviceTypeText = "_eslee-quicksend._tcp.local.";
const ushort expectedPort = 41231;

var records = new ProbeRecords();
using var mdns = new MulticastService(SelectInterfaces)
{
    UseIpv4 = true,
    UseIpv6 = false
};

mdns.AnswerReceived += (_, eventArgs) =>
{
    lock (records)
    {
        foreach (var record in eventArgs.Message.Answers.Concat(eventArgs.Message.AdditionalRecords))
            records.Accept(record);
    }
};

mdns.Start();
try
{
    var serviceType = new DomainName(serviceTypeText);
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (DateTime.UtcNow < deadline)
    {
        mdns.SendQuery(serviceType, DnsClass.IN, DnsType.PTR);
        await Task.Delay(750);
        lock (records)
        {
            if (records.IsComplete(serviceTypeText, expectedPort))
                break;
        }
    }
}
finally
{
    mdns.Stop();
}

lock (records)
{
    records.Print();
    if (!records.IsComplete(serviceTypeText, expectedPort))
    {
        Console.Error.WriteLine("FAIL: QuickSend DNS-SD PTR/SRV/TXT/A contract was not observed within 10 seconds.");
        return 1;
    }
}

Console.WriteLine("PASS: QuickSend DNS-SD advertisement is discoverable from a separate process.");
return 0;

static IEnumerable<NetworkInterface> SelectInterfaces(IEnumerable<NetworkInterface> candidates) =>
    candidates.Where(static candidate =>
    {
        if (candidate.OperationalStatus != OperationalStatus.Up ||
            candidate.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
            !candidate.SupportsMulticast)
            return false;

        try
        {
            return candidate.GetIPProperties().UnicastAddresses.Any(static address =>
                address.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address.Address));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    });

file sealed class ProbeRecords
{
    private readonly Dictionary<string, string> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Target, ushort Port)> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> _text = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress> _addresses = new(StringComparer.OrdinalIgnoreCase);

    public void Accept(ResourceRecord record)
    {
        switch (record)
        {
            case PTRRecord pointer:
                _instances[Normalize(pointer.Name)] = Normalize(pointer.DomainName);
                break;
            case SRVRecord service:
                _services[Normalize(service.Name)] = (Normalize(service.Target), service.Port);
                break;
            case TXTRecord text:
                _text[Normalize(text.Name)] = text.Strings.ToArray();
                break;
            case ARecord address:
                _addresses[Normalize(address.Name)] = address.Address;
                break;
        }
    }

    public bool IsComplete(string serviceType, ushort expectedPort)
    {
        if (!_instances.TryGetValue(Normalize(serviceType), out var instance) ||
            !_services.TryGetValue(instance, out var service) ||
            service.Port != expectedPort ||
            !_text.TryGetValue(instance, out var text) ||
            !_addresses.ContainsKey(service.Target))
            return false;

        return text.Any(static item => item.StartsWith("id=", StringComparison.Ordinal)) &&
               text.Any(static item => item.StartsWith("name=", StringComparison.Ordinal)) &&
               text.Any(static item => item.StartsWith("fp=", StringComparison.Ordinal)) &&
               text.Any(static item => item == "v=1");
    }

    public void Print()
    {
        foreach (var (type, instance) in _instances)
            Console.WriteLine($"PTR type={type} instance={instance}");
        foreach (var (instance, service) in _services)
            Console.WriteLine($"SRV instance={instance} target={service.Target} port={service.Port}");
        foreach (var (instance, text) in _text)
            Console.WriteLine($"TXT instance={instance} values={string.Join(';', text)}");
        foreach (var (host, address) in _addresses)
            Console.WriteLine($"A host={host} address={address}");
    }

    private static string Normalize(DomainName value) => Normalize(value.ToString());
    private static string Normalize(string value) => value.TrimEnd('.');
}
