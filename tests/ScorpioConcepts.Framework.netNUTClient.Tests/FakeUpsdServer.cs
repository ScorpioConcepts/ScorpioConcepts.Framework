using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ScorpioConcepts.Framework.netNUTClient.Tests;

/// <summary>
/// A scripted stand-in for a NUT upsd server. Serves one client at a time, accepting the next
/// when it disconnects, and answers each newline-terminated command with the reply registered for it.
/// With a <see cref="TlsCertificate"/>, it answers STARTTLS and switches the connection to TLS.
/// </summary>
internal sealed class FakeUpsdServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<string, string> _replies = new();
    private readonly ConcurrentDictionary<string, TimeSpan> _delays = new();
    private readonly ConcurrentDictionary<string, int[]> _splits = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _serverTask;
    private TcpClient? _client;
    private readonly TaskCompletionSource _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _connections;

    public ConcurrentQueue<string> ReceivedCommands { get; } = new();
    public int Port { get; }

    /// <summary>How many client connections the server has accepted so far.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>When set, replies are sent in pieces of this many bytes with a short pause between them.</summary>
    public int? FragmentSize { get; set; }

    /// <summary>When set, STARTTLS is accepted and the connection switches to TLS with this certificate.</summary>
    public X509Certificate2? TlsCertificate { get; set; }

    /// <summary>True once a client has completed a TLS handshake.</summary>
    public bool ClientUsedTls { get; private set; }

    public FakeUpsdServer() : this(IPAddress.Loopback)
    {
    }

    public FakeUpsdServer(IPAddress address)
    {
        _listener = new TcpListener(address, 0);
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

    /// <summary>
    /// Answer <paramref name="command"/> in separate writes, split at the given byte offsets of the UTF-8 reply,
    /// with a short pause between them. Splitting at a few chosen points tests reassembly without the cost of
    /// sending every byte separately, which is slow enough on a busy CI runner to hit the client's reply timeout.
    /// </summary>
    public FakeUpsdServer ReplyInPieces(string command, string reply, params int[] splitAtBytes)
    {
        _splits[command] = splitAtBytes;
        return Reply(command, reply);
    }

    /// <summary>Answer <paramref name="command"/> only after <paramref name="delay"/>.</summary>
    public FakeUpsdServer ReplyAfter(string command, string reply, TimeSpan delay)
    {
        _delays[command] = delay;
        return Reply(command, reply);
    }

    /// <summary>Wait for the first client to connect, then close its connection from the server side.</summary>
    public void DropClient()
    {
        _accepted.Task.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue("client never connected");
        _client!.Close();
    }

    /// <summary>A self-signed certificate for localhost, usable as a TLS server certificate on every platform.</summary>
    public static X509Certificate2 CreateCertificate(string subject = "localhost")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(subject);
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // An exported and reloaded certificate keeps its private key usable by SslStream on Windows too
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                _client = await _listener.AcceptTcpClientAsync(_cts.Token);
                Interlocked.Increment(ref _connections);
                _accepted.TrySetResult();
                try
                {
                    await ServeClientAsync(_client);
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (System.Security.Authentication.AuthenticationException) { }
                finally
                {
                    _client.Close();
                }
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task ServeClientAsync(TcpClient client)
    {
        Stream stream = client.GetStream();
        try
        {
            var pending = new StringBuilder();
            var buffer = new byte[1024];
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

            while (!_cts.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, _cts.Token);
                if (read == 0) return;
                pending.Append(chars, 0, decoder.GetChars(buffer, 0, read, chars, 0));

                string text = pending.ToString();
                int newline;
                while ((newline = text.IndexOf('\n')) >= 0)
                {
                    string command = text[..newline];
                    text = text[(newline + 1)..];
                    ReceivedCommands.Enqueue(command);

                    if (command == "STARTTLS" && TlsCertificate is not null && !_replies.ContainsKey("STARTTLS"))
                    {
                        await SendAsync(stream, "OK STARTTLS\n");
                        var ssl = new SslStream(stream, false);
                        await ssl.AuthenticateAsServerAsync(TlsCertificate);
                        ClientUsedTls = true;
                        stream = ssl;
                        continue;
                    }

                    if (_delays.TryGetValue(command, out var delay)) await Task.Delay(delay, _cts.Token);
                    string reply = _replies.TryGetValue(command, out var r) ? r : "ERR UNKNOWN-COMMAND\n";
                    if (_splits.TryGetValue(command, out var splitAt)) await SendInPiecesAsync(stream, reply, splitAt);
                    else await SendAsync(stream, reply);

                    if (command == "LOGOUT") return;
                }
                pending.Clear().Append(text);
            }
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    private async Task SendAsync(Stream stream, string reply)
    {
        byte[] data = Encoding.UTF8.GetBytes(reply);
        if (data.Length == 0) return; // an empty reply means "stay silent"
        if (FragmentSize is not int size)
        {
            await stream.WriteAsync(data, _cts.Token);
            return;
        }

        for (int offset = 0; offset < data.Length; offset += size)
        {
            await stream.WriteAsync(data.AsMemory(offset, Math.Min(size, data.Length - offset)), _cts.Token);
            await stream.FlushAsync(_cts.Token);
            await Task.Delay(20, _cts.Token);
        }
    }

    private async Task SendInPiecesAsync(Stream stream, string reply, int[] splitAtBytes)
    {
        byte[] data = Encoding.UTF8.GetBytes(reply);
        int start = 0;
        foreach (int split in splitAtBytes.Where(s => s > 0 && s < data.Length).Order().Append(data.Length))
        {
            await stream.WriteAsync(data.AsMemory(start, split - start), _cts.Token);
            await stream.FlushAsync(_cts.Token);
            await Task.Delay(20, _cts.Token);
            start = split;
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
