using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ScorpioConcepts.Framework.LogServer.Tests;

internal static class Wait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static void Until(Func<bool> condition, string because)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > Timeout) throw new TimeoutException("Timed out waiting until " + because);
            Thread.Sleep(10);
        }
    }
}

/// <summary>
/// A minimal telnet-style client that collects the CRLF-terminated lines the log server sends.
/// </summary>
internal sealed class LogClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly StringBuilder _received = new();
    // A decoder keeps a multi-byte UTF-8 character that is split across reads in one piece
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly byte[] _buffer = new byte[16 * 1024];
    private readonly char[] _chars = new char[Encoding.UTF8.GetMaxCharCount(16 * 1024)];

    public LogClient(int port)
    {
        _tcp = new TcpClient(AddressFamily.InterNetwork);
        _tcp.Connect(IPAddress.Loopback, port);
        _stream = _tcp.GetStream();
        _stream.ReadTimeout = (int)Wait.Timeout.TotalMilliseconds;
    }

    public string LocalAddress => _tcp.Client.LocalEndPoint!.ToString()!;

    /// <summary>Read until at least <paramref name="count"/> complete lines have arrived, and return all lines so far.</summary>
    public string[] ReadLines(int count)
    {
        while (Lines().Length < count && ReadMore())
        {
        }
        return Lines();
    }

    /// <summary>Read until a complete line matches <paramref name="match"/>, and return all lines so far.</summary>
    public string[] ReadUntil(Func<string, bool> match)
    {
        while (!Lines().Any(match) && ReadMore())
        {
        }
        return Lines();
    }

    /// <summary>True once the server has closed the connection.</summary>
    public bool WaitForServerClose()
    {
        try
        {
            while (ReadMore())
            {
            }
            return true;
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
        {
            return true;
        }
    }

    public void Send(string text) => _stream.Write(Encoding.UTF8.GetBytes(text));

    private bool ReadMore()
    {
        int read = _stream.Read(_buffer, 0, _buffer.Length);
        if (read == 0) return false;
        _received.Append(_chars, 0, _decoder.GetChars(_buffer, 0, read, _chars, 0));
        return true;
    }

    private string[] Lines()
    {
        string text = _received.ToString();
        int last = text.LastIndexOf("\r\n", StringComparison.Ordinal);
        return last < 0 ? [] : text[..last].Split("\r\n");
    }

    public void Dispose() => _tcp.Dispose();
}
