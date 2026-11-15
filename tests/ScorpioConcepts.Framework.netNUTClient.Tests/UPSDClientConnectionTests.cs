using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ScorpioConcepts.Framework.netNUTClient.Tests;

/// <summary>
/// Connecting, TLS, the async API and use from several threads.
/// </summary>
public sealed class UPSDClientConnectionTests : IDisposable
{
    private static readonly X509Certificate2 Certificate = FakeUpsdServer.CreateCertificate();

    private readonly FakeUpsdServer _server = new();
    private readonly List<UPSDClient> _clients = new();

    public void Dispose()
    {
        foreach (var client in _clients) client.Dispose();
        _server.Dispose();
    }

    private UPSDClient Client(string? server = null)
    {
        var client = server is null ? new UPSDClient("127.0.0.1", (ushort)_server.Port) : new UPSDClient(server);
        client.ReplyTimeout = TimeSpan.FromSeconds(5);
        _clients.Add(client);
        return client;
    }

    private static bool AcceptTestCertificate(object sender, X509Certificate? certificate, X509Chain? chain, System.Net.Security.SslPolicyErrors errors)
        => certificate is not null && certificate.GetCertHashString() == Certificate.GetCertHashString();

    [Fact]
    public void Connects_to_an_IPv6_address()
    {
        if (!Socket.OSSupportsIPv6) Assert.Skip("This machine has no IPv6");
        using var server = new FakeUpsdServer(IPAddress.IPv6Loopback);
        server.Reply("GET VAR rack ups.status", "VAR rack ups.status \"OL\"\n");
        var client = Client($"[::1]:{server.Port}");

        client.Connect();

        client.GetUPSVar("rack", "ups.status").ShouldBe("OL");
    }

    [Fact]
    public void Connect_gives_up_after_ConnectTimeout()
    {
        // 10.255.255.1 is never routed, so the connection attempt neither succeeds nor is refused
        var client = new UPSDClient("10.255.255.1", 3493) { ConnectTimeout = TimeSpan.FromMilliseconds(300) };
        _clients.Add(client);
        var timer = Stopwatch.StartNew();

        var ex = Should.Throw<Exception>(() => client.Connect());

        // Some networks reject the address at once instead; either way Connect must not hang
        (ex is TimeoutException or SocketException).ShouldBeTrue(ex.ToString());
        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        client.Connected.ShouldBeFalse();
    }

    [Fact]
    public void ConnectTimeout_and_ReplyTimeout_default_to_ten_seconds()
    {
        using var client = new UPSDClient("localhost");

        client.ConnectTimeout.ShouldBe(TimeSpan.FromSeconds(10));
        client.ReplyTimeout.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Async_commands_work()
    {
        _server
            .Reply("LIST UPS", "BEGIN LIST UPS\nUPS rack \"Server rack UPS\"\nEND LIST UPS\n")
            .Reply("GET VAR rack battery.charge", "VAR rack battery.charge \"97\"\n")
            .Reply("LOGIN rack", "OK\n");
        var client = Client();
        var ct = TestContext.Current.CancellationToken;

        await client.ConnectAsync(ct);
        (await client.ListUPSAsync(ct)).ShouldHaveSingleItem().Name.ShouldBe("rack");
        (await client.GetUPSVarAsync("rack", "battery.charge", ct)).ShouldBe("97");
        (await client.LoginAsync("rack", ct)).ShouldBeTrue();
        await client.DisconnectAsync(ct);

        client.Connected.ShouldBeFalse();
        _server.ReceivedCommands.Last().ShouldBe("LOGOUT");
    }

    [Fact]
    public async Task Canceling_a_command_closes_the_connection()
    {
        _server.Reply("GET VAR rack ups.status", ""); // never answers
        var client = Client();
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(() => client.GetUPSVarAsync("rack", "ups.status", cancel.Token));

        client.Connected.ShouldBeFalse();
    }

    [Fact]
    public async Task Commands_from_several_threads_each_get_their_own_reply()
    {
        const int variables = 40;
        for (int i = 0; i < variables; i++)
        {
            _server.Reply($"GET VAR rack test.var{i}", $"VAR rack test.var{i} \"value {i}\"\n");
        }
        _server.FragmentSize = 7; // slow replies make overlapping commands likely
        var client = Client();
        client.Connect();

        var results = await Task.WhenAll(Enumerable.Range(0, variables)
            .Select(i => Task.Run(() => (i, value: client.GetUPSVar("rack", $"test.var{i}")), TestContext.Current.CancellationToken)));

        results.ShouldAllBe(r => r.value == $"value {r.i}");
    }

    [Fact]
    public void Large_lists_are_read_quickly()
    {
        const int count = 5000;
        var reply = new System.Text.StringBuilder("BEGIN LIST VAR rack\n");
        for (int i = 0; i < count; i++) reply.Append($"VAR rack test.var{i} \"{i}\"\n");
        reply.Append("END LIST VAR rack\n");
        _server.Reply("LIST VAR rack", reply.ToString());
        var client = Client();
        client.Connect();
        var timer = Stopwatch.StartNew();

        var vars = client.ListUPSVar("rack");

        vars.Count.ShouldBe(count);
        vars["test.var4999"].ShouldBe("4999");
        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void StartTls_encrypts_the_connection()
    {
        _server.TlsCertificate = Certificate;
        _server.Reply("LIST UPS", "BEGIN LIST UPS\nUPS rack \"Server rack UPS\"\nEND LIST UPS\n");
        var client = Client();
        client.CertificateValidationCallback = AcceptTestCertificate;
        client.Connect();
        client.IsEncrypted.ShouldBeFalse();

        client.StartTls("localhost");

        client.IsEncrypted.ShouldBeTrue();
        _server.ClientUsedTls.ShouldBeTrue();
        client.ListUPS().ShouldHaveSingleItem().Name.ShouldBe("rack");
    }

    [Fact]
    public async Task StartTlsAsync_encrypts_the_connection()
    {
        _server.TlsCertificate = Certificate;
        _server.Reply("GET VAR rack ups.status", "VAR rack ups.status \"OL\"\n");
        var client = Client();
        client.CertificateValidationCallback = AcceptTestCertificate;
        var ct = TestContext.Current.CancellationToken;
        await client.ConnectAsync(ct);

        await client.StartTlsAsync("localhost", ct);

        client.IsEncrypted.ShouldBeTrue();
        (await client.GetUPSVarAsync("rack", "ups.status", ct)).ShouldBe("OL");
    }

    [Fact]
    public void Untrusted_certificate_is_rejected_by_default_and_closes_the_connection()
    {
        _server.TlsCertificate = Certificate;
        var client = Client();
        client.Connect();

        Should.Throw<AuthenticationException>(() => client.StartTls("localhost"));

        client.Connected.ShouldBeFalse();
        client.IsEncrypted.ShouldBeFalse();
    }

    [Fact]
    public void Server_without_TLS_refuses_and_the_connection_stays_usable()
    {
        _server
            .Reply("STARTTLS", "ERR FEATURE-NOT-CONFIGURED\n")
            .Reply("GET VAR rack ups.status", "VAR rack ups.status \"OL\"\n");
        var client = Client();
        client.Connect();

        Should.Throw<UPSException>(() => client.StartTls())
            .Code.ShouldBe(UPSException.ErrorCode.FEATURE_NOT_CONFIGURED);

        client.Connected.ShouldBeTrue();
        client.IsEncrypted.ShouldBeFalse();
        client.GetUPSVar("rack", "ups.status").ShouldBe("OL");
    }

    [Fact]
    public void StartTls_twice_is_rejected()
    {
        _server.TlsCertificate = Certificate;
        var client = Client();
        client.CertificateValidationCallback = AcceptTestCertificate;
        client.Connect();
        client.StartTls("localhost");

        Should.Throw<InvalidOperationException>(() => client.StartTls("localhost"));
    }

    [Fact]
    public void Disposing_a_client_that_never_connected_does_nothing()
    {
        Should.NotThrow(() => new UPSDClient("localhost").Dispose());
    }
}
