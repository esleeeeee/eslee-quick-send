namespace Eslee.QuickSend.Core.Protocol;

public sealed class ProtocolException : IOException
{
    public ProtocolException(string message) : base(message) { }
}

