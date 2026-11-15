using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ScorpioConcepts.Framework.LogServer.Tests;

public sealed class LogServerTests : IDisposable
{
    private readonly List<LogServer> _servers = new();

    public void Dispose()
    {
        foreach (var server in _servers) server.Dispose();
    }

    /// <summary>A server on loopback and any free port; its real port is in <see cref="LogServer.LocalEndpoint"/>.</summary>
    private LogServer NewServer(string greeting = "Test Log Server", int logSize = 10)
    {
        var server = new LogServer(IPAddress.Loopback, 0, greeting, logSize);
        _servers.Add(server);
        return server;
    }

    private LogServer StartServer(string greeting = "Test Log Server", int logSize = 10, Action<LogServer>? configure = null)
    {
        var server = NewServer(greeting, logSize);
        configure?.Invoke(server);
        server.Start();
        server.Active.ShouldBeTrue();
        return server;
    }

    private static int PortOf(LogServer server) => server.LocalEndpoint!.Port;

    // ---- History ----

    [Fact]
    public void LogHistory_keeps_lines_logged_before_Start()
    {
        var server = NewServer();

        server.Log("one");
        server.Log("two");

        server.LogHistory.ShouldBe(new[] { "one", "two" });
        server.Active.ShouldBeFalse();
    }

    [Fact]
    public void LogHistory_keeps_only_the_most_recent_lines()
    {
        var server = NewServer(logSize: 3);

        for (int i = 1; i <= 5; i++) server.Log("line " + i);

        server.LogHistory.ShouldBe(new[] { "line 3", "line 4", "line 5" });
    }

    [Fact]
    public void LogHistory_returns_a_snapshot()
    {
        var server = NewServer();
        server.Log("original");
        var history = server.LogHistory;

        server.Log("later");

        history.ShouldBe(new[] { "original" });
        server.LogHistory.ShouldBe(new[] { "original", "later" });
    }

    [Fact]
    public void Zero_log_size_keeps_no_history()
    {
        var server = NewServer(logSize: 0);

        server.Log("gone");

        server.LogHistory.ShouldBeEmpty();
    }

    // ---- Construction ----

    [Fact]
    public void Default_port_is_7446()
    {
        LogServer.DefaultPort.ShouldBe(7446);
    }

    [Fact]
    public void Default_constructor_listens_on_every_interface_and_the_default_port()
    {
        // 7446 may already be in use on a developer machine, so accept that outcome too
        var errors = new ConcurrentQueue<Exception>();
        var server = new LogServer("greeting", 10);
        _servers.Add(server);
        server.ServerError += (_, e) => errors.Enqueue(e.Exception);

        server.Start();

        if (errors.IsEmpty)
        {
            server.LocalEndpoint.ShouldBe(new IPEndPoint(IPAddress.Any, 7446));
            using var client = new LogClient(7446);
            client.ReadLines(1).ShouldBe(new[] { "greeting" });
        }
        else
        {
            errors.Single().ShouldBeOfType<SocketException>().SocketErrorCode.ShouldBe(SocketError.AddressAlreadyInUse);
        }
    }

    [Fact]
    public void Can_listen_on_loopback_only()
    {
        var server = StartServer();

        server.LocalEndpoint!.Address.ShouldBe(IPAddress.Loopback);
        server.LocalEndpoint.Port.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Port_constructor_listens_on_every_interface()
    {
        var server = new LogServer(0, "greeting", 10);
        _servers.Add(server);

        server.Start();

        server.LocalEndpoint!.Address.ShouldBe(IPAddress.Any);
    }

    [Fact]
    public void Constructor_rejects_invalid_arguments()
    {
        Should.Throw<ArgumentNullException>(() => new LogServer(null!, 0, "g", 10)).ParamName.ShouldBe("address");
        Should.Throw<ArgumentOutOfRangeException>(() => new LogServer(IPAddress.Loopback, 70000, "g", 10)).ParamName.ShouldBe("port");
        Should.Throw<ArgumentOutOfRangeException>(() => new LogServer(IPAddress.Loopback, -1, "g", 10)).ParamName.ShouldBe("port");
        Should.Throw<ArgumentNullException>(() => new LogServer(IPAddress.Loopback, 0, null!, 10)).ParamName.ShouldBe("greetingMessage");
        Should.Throw<ArgumentOutOfRangeException>(() => new LogServer(IPAddress.Loopback, 0, "g", -1)).ParamName.ShouldBe("logSize");
    }

    [Fact]
    public void Limits_reject_values_below_one()
    {
        var server = NewServer();

        Should.Throw<ArgumentOutOfRangeException>(() => server.MaxClients = 0);
        Should.Throw<ArgumentOutOfRangeException>(() => server.MaxQueuedLines = 0);
        server.MaxClients.ShouldBe(LogServer.DefaultMaxClients);
        server.MaxQueuedLines.ShouldBe(LogServer.DefaultMaxQueuedLines);
    }

    [Fact]
    public void Log_rejects_null()
    {
        Should.Throw<ArgumentNullException>(() => NewServer().Log(null!));
    }

    // ---- Starting and stopping ----

    [Fact]
    public void Stop_and_Join_shut_the_server_down()
    {
        var server = StartServer();
        int port = PortOf(server);

        server.Stop();
        server.Join();

        server.Active.ShouldBeFalse();
        Should.Throw<SocketException>(() => new TcpClient("127.0.0.1", port));
    }

    [Fact]
    public void Dispose_stops_the_server()
    {
        var server = StartServer();
        int port = PortOf(server);

        server.Dispose();

        server.Active.ShouldBeFalse();
        Should.Throw<SocketException>(() => new TcpClient("127.0.0.1", port));
    }

    [Fact]
    public async Task Stop_straight_after_Start_still_shuts_down()
    {
        for (int i = 0; i < 20; i++)
        {
            var server = NewServer();

            server.Start();
            server.Stop();

            // WaitAsync throws TimeoutException if Join never returns
            await Task.Run(server.Join, TestContext.Current.CancellationToken).WaitAsync(Wait.Timeout, TestContext.Current.CancellationToken);
            server.Active.ShouldBeFalse();
        }
    }

    [Fact]
    public void Start_twice_is_rejected()
    {
        var server = StartServer();

        Should.Throw<InvalidOperationException>(server.Start);
    }

    [Fact]
    public void Join_without_Start_returns_immediately()
    {
        Should.NotThrow(NewServer().Join);
    }

    [Fact]
    public void Port_in_use_raises_ServerError_from_Start()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        try
        {
            var errors = new ConcurrentQueue<Exception>();
            var server = new LogServer(IPAddress.Loopback, ((IPEndPoint)blocker.LocalEndpoint).Port, "greeting", 10);
            _servers.Add(server);
            server.ServerError += (sender, e) =>
            {
                sender.ShouldBeSameAs(server);
                errors.Enqueue(e.Exception);
            };

            server.Start();

            // Reported before Start() returns, and the server never becomes active
            errors.ShouldHaveSingleItem().ShouldBeOfType<SocketException>().SocketErrorCode.ShouldBe(SocketError.AddressAlreadyInUse);
            server.Active.ShouldBeFalse();
            server.LocalEndpoint.ShouldBeNull();
            Should.NotThrow(server.Join);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public void Clients_can_connect_as_soon_as_Start_returns()
    {
        for (int i = 0; i < 20; i++)
        {
            var server = StartServer(greeting: "ready");
            using var client = new LogClient(PortOf(server));
            client.ReadLines(1).ShouldBe(new[] { "ready" });
            server.Dispose();
        }
    }

    [Fact]
    public void Stopping_the_server_closes_client_connections()
    {
        var disconnected = new ConcurrentQueue<string>();
        var server = StartServer(greeting: "hi", configure: s => s.ClientDisconnected += (_, e) => disconnected.Enqueue(e.Address));
        using var client = new LogClient(PortOf(server));
        client.ReadLines(1);

        server.Stop();
        server.Join();

        client.WaitForServerClose().ShouldBeTrue();
        disconnected.Count.ShouldBe(1, "stopping counts as a disconnect");
    }

    // ---- Streaming ----

    [Fact]
    public void New_client_receives_greeting_then_history()
    {
        var server = StartServer(greeting: "Welcome to the log");
        server.Log("first");
        server.Log("second");

        using var client = new LogClient(PortOf(server));

        client.ReadLines(3).ShouldBe(new[] { "Welcome to the log", "first", "second" });
    }

    [Fact]
    public void New_client_only_receives_the_retained_history()
    {
        var server = StartServer(greeting: "hello", logSize: 2);
        server.Log("a");
        server.Log("b");
        server.Log("c");

        using var client = new LogClient(PortOf(server));

        client.ReadLines(3).ShouldBe(new[] { "hello", "b", "c" });
    }

    [Fact]
    public void Lines_logged_while_connected_are_streamed_to_every_client()
    {
        var server = StartServer(greeting: "hi");
        using var first = new LogClient(PortOf(server));
        using var second = new LogClient(PortOf(server));
        first.ReadLines(1);
        second.ReadLines(1);

        server.Log("live line");

        first.ReadLines(2).ShouldBe(new[] { "hi", "live line" });
        second.ReadLines(2).ShouldBe(new[] { "hi", "live line" });
    }

    [Fact]
    public void Lines_are_sent_as_UTF8()
    {
        var server = StartServer(greeting: "Grüße");
        using var client = new LogClient(PortOf(server));
        client.ReadLines(1);

        server.Log("Café ☕ naïve 日本語");

        client.ReadLines(2).ShouldBe(new[] { "Grüße", "Café ☕ naïve 日本語" });
    }

    [Fact]
    public void Input_from_the_client_is_ignored()
    {
        var server = StartServer(greeting: "hi");
        using var client = new LogClient(PortOf(server));
        client.ReadLines(1);

        client.Send("some typing from the user\r\n");
        Thread.Sleep(200);
        server.Log("still working");

        client.ReadLines(2).ShouldBe(new[] { "hi", "still working" });
    }

    [Fact]
    public async Task Clients_see_every_line_exactly_once_while_others_connect()
    {
        const int lineCount = 2000;
        var errors = new ConcurrentQueue<Exception>();
        var server = StartServer(greeting: "hi", logSize: lineCount, configure: s => s.ServerError += (_, e) => errors.Enqueue(e.Exception));
        int port = PortOf(server);

        // Log steadily while clients join part way through. Each client must end up with the
        // full log once, however its connection lined up with the lines being logged.
        var logger = Task.Run(() =>
        {
            for (int i = 0; i < lineCount; i++)
            {
                server.Log("line " + i);
                if (i % 20 == 0) Thread.Sleep(1);
            }
        }, TestContext.Current.CancellationToken);

        var clients = new List<LogClient>();
        try
        {
            while (!logger.IsCompleted && clients.Count < 8)
            {
                clients.Add(new LogClient(port));
                Thread.Sleep(50);
            }
            await logger.WaitAsync(Wait.Timeout, TestContext.Current.CancellationToken);

            var expected = new[] { "hi" }.Concat(Enumerable.Range(0, lineCount).Select(i => "line " + i)).ToArray();
            clients.Count.ShouldBeGreaterThan(1);
            foreach (var client in clients)
            {
                client.ReadLines(expected.Length).ShouldBe(expected);
            }
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
        }

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Survives_many_clients_connecting_and_disconnecting()
    {
        const int clientCount = 40;
        var errors = new ConcurrentQueue<Exception>();
        var disconnected = 0;
        var server = StartServer(greeting: "hi", configure: s =>
        {
            s.ServerError += (_, e) => errors.Enqueue(e.Exception);
            s.ClientDisconnected += (_, _) => Interlocked.Increment(ref disconnected);
        });
        int port = PortOf(server);

        // Busy logging, in bursts like a real application, while clients come and go. Dedicated threads
        // keep the logger and the blocking test clients off the thread pool the server runs on: a tight
        // logging loop and synchronous reads on pool threads starved it on a loaded CI runner.
        using var stop = new ManualResetEventSlim();
        var logger = new Thread(() =>
        {
            while (!stop.IsSet)
            {
                for (int i = 0; i < 50; i++) server.Log("busy");
                Thread.Sleep(1);
            }
        });
        logger.Start();

        var clientErrors = new ConcurrentQueue<Exception>();
        var clients = Enumerable.Range(0, 8).Select(t => new Thread(() =>
        {
            try
            {
                for (int i = t; i < clientCount; i += 8)
                {
                    using var client = new LogClient(port);
                    client.ReadLines(1);
                }
            }
            catch (Exception ex)
            {
                clientErrors.Enqueue(ex);
            }
        })).ToList();
        clients.ForEach(c => c.Start());
        clients.ForEach(c => c.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue("each client thread should finish"));
        clientErrors.ShouldBeEmpty();

        Wait.Until(() => Volatile.Read(ref disconnected) == clientCount, "every client has disconnected");
        stop.Set();
        logger.Join(Wait.Timeout).ShouldBeTrue("the logger should stop");

        errors.ShouldBeEmpty();
        server.Active.ShouldBeTrue();
        server.ConnectedClients.ShouldBe(0);
        using var last = new LogClient(port);
        last.ReadLines(1)[0].ShouldBe("hi");
    }

    // ---- Events ----

    [Fact]
    public void Connect_and_disconnect_events_report_the_client_address()
    {
        var connected = new ConcurrentQueue<string>();
        var disconnected = new ConcurrentQueue<string>();
        var server = StartServer(configure: s =>
        {
            s.ClientConnected += (_, e) => connected.Enqueue(e.Address);
            s.ClientDisconnected += (_, e) => disconnected.Enqueue(e.Address);
        });

        string address;
        using (var client = new LogClient(PortOf(server)))
        {
            address = client.LocalAddress;
            Wait.Until(() => connected.Count == 1, "the connect event is raised");
            server.ConnectedClients.ShouldBe(1);
        }

        Wait.Until(() => disconnected.Count == 1, "the disconnect event is raised");
        connected.ShouldBe(new[] { address });
        disconnected.ShouldBe(new[] { address });
        server.ConnectedClients.ShouldBe(0);
    }

    // ---- Limits ----

    [Fact]
    public void Clients_beyond_MaxClients_are_told_and_disconnected()
    {
        var server = StartServer(greeting: "hi", configure: s => s.MaxClients = 2);
        int port = PortOf(server);
        using var first = new LogClient(port);
        using var second = new LogClient(port);
        first.ReadLines(1);
        second.ReadLines(1);

        using (var third = new LogClient(port))
        {
            third.ReadLines(1).ShouldBe(new[] { "Too many clients are connected to this log server. Try again later." });
            third.WaitForServerClose().ShouldBeTrue();
        }
        server.ConnectedClients.ShouldBe(2);

        // Once a place is free, a new client gets in
        first.Dispose();
        Wait.Until(() => server.ConnectedClients == 1, "the first client has gone");
        using var fourth = new LogClient(port);
        fourth.ReadLines(1).ShouldBe(new[] { "hi" });
    }

    [Fact]
    public void A_client_that_stops_reading_cannot_grow_the_queue_past_MaxQueuedLines()
    {
        const int lines = 100_000;
        var server = StartServer(greeting: "hi", logSize: 0, configure: s => s.MaxQueuedLines = 100);
        using var stalled = new LogClient(PortOf(server));
        Wait.Until(() => server.ConnectedClients == 1, "the client is connected");

        // The client reads nothing while a lot is logged with long lines, so the socket buffers fill up
        string padding = new('x', 200);
        for (int i = 0; i < lines; i++) server.Log($"line {i} {padding}");

        server.QueuedLinesPerClient().ShouldAllBe(count => count <= 100);

        // When it starts reading, it learns that lines were skipped and still gets the latest one
        var received = stalled.ReadUntil(line => line.StartsWith($"line {lines - 1} ", StringComparison.Ordinal));
        received.ShouldContain(line => line.StartsWith("*** ", StringComparison.Ordinal) && line.Contains("lines skipped"));
        received.Length.ShouldBeLessThan(lines);
    }
}
