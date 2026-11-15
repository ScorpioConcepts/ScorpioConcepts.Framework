using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ScorpioConcepts.Framework.netNUTClient.Tests;

/// <summary>
/// A scripted stand-in for a NUT upsd server. Accepts one client and answers each
/// newline-terminated command with the reply registered for it.
/// </summary>
internal sealed class FakeUpsdServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentDictionary<string, string> _replies = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _serverTask;
    private TcpClient? _client;
    private readonly TaskCompletionSource _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<string> ReceivedCommands { get; } = new();
    public int Port { get; }

    /// <summary>When set, replies are sent in pieces of this many bytes with a short pause between them.</summary>
    public int? FragmentSize { get; set; }

    public FakeUpsdServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Reply("LOGOUT", "OK Goodbye\n");
        _serverTask = Task.Run(ServeAsync);
    }

    public FakeUpsdServer Reply(string command, string reply)
    {
        _replies[command] = reply;
        return this;
    }

    /// <summary>Wait for the client to connect, then close its connection from the server side.</summary>
    public void DropClient()
    {
        _accepted.Task.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue("client never connected");
        _client!.Close();
    }

    private async Task ServeAsync()
    {
        try
        {
            _client = await _listener.AcceptTcpClientAsync(_cts.Token);
            _accepted.TrySetResult();
            using var stream = _client.GetStream();
            var pending = new StringBuilder();
            var buffer = new byte[1024];

            while (!_cts.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, _cts.Token);
                if (read == 0) return;
                pending.Append(Encoding.ASCII.GetString(buffer, 0, read));

                string text = pending.ToString();
                int newline;
                while ((newline = text.IndexOf('\n')) >= 0)
                {
                    string command = text[..newline];
                    text = text[(newline + 1)..];
                    ReceivedCommands.Enqueue(command);

                    string reply = _replies.TryGetValue(command, out var r) ? r : "ERR UNKNOWN-COMMAND\n";
                    await SendAsync(stream, reply);

                    if (command == "LOGOUT")
                    {
                        _client.Close();
                        return;
                    }
                }
                pending.Clear().Append(text);
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task SendAsync(NetworkStream stream, string reply)
    {
        byte[] data = Encoding.ASCII.GetBytes(reply);
        if (data.Length == 0) return; // an empty reply means "stay silent"
        if (FragmentSize is not int size)
        {
            await stream.WriteAsync(data, _cts.Token);
            return;
        }

        for (int offset = 0; offset < data.Length; offset += size)
        {
            await stream.WriteAsync(data.AsMemory(offset, Math.Min(size, data.Length - offset)), _cts.Token);
            await Task.Delay(20, _cts.Token);
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _client?.Close();
        _listener.Stop();
        try { _serverTask.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
