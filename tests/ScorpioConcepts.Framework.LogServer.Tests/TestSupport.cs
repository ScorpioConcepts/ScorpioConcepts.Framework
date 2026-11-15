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

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
        var buffer = new byte[4096];
        while (Lines().Length < count)
        {
            int read = _stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            _received.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        return Lines();
    }

    /// <summary>True once the server has closed the connection.</summary>
    public bool WaitForServerClose()
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                int read = _stream.Read(buffer, 0, buffer.Length);
                if (read == 0) return true;
                _received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
        {
            return true;
        }
    }

    public void Send(string text) => _stream.Write(Encoding.ASCII.GetBytes(text));

    private string[] Lines()
    {
        string text = _received.ToString();
        int last = text.LastIndexOf("\r\n", StringComparison.Ordinal);
        return last < 0 ? [] : text[..last].Split("\r\n");
    }

    public void Dispose() => _tcp.Dispose();
}
