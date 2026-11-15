using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ScorpioConcepts.Framework.LogServer.Tests;

public sealed class LogServerTests : IDisposable
{
    private readonly int _port = Wait.FreePort();
    private readonly List<LogServer> _started = new();

    public void Dispose()
    {
        foreach (var server in _started)
        {
            server.Stop();
            server.Join();
        }
    }

    private LogServer StartServer(string greeting = "Test Log Server", int logSize = 10, Action<LogServer>? configure = null)
    {
        var server = new LogServer(_port, greeting, logSize);
        configure?.Invoke(server);
        server.Start();
        _started.Add(server);
        Wait.Until(() => server.Active, "the server is running");
        return server;
    }

    [Fact]
    public void LogHistory_keeps_lines_logged_before_Start()
    {
        var server = new LogServer(_port, "greeting", 10);

        server.Log("one");
        server.Log("two");

        server.LogHistory.ShouldBe(new[] { "one", "two" });
        server.Active.ShouldBeFalse();
    }

    [Fact]
    public void LogHistory_keeps_only_the_most_recent_lines()
    {
        var server = new LogServer(_port, "greeting", 3);

        for (int i = 1; i <= 5; i++) server.Log("line " + i);

        server.LogHistory.ShouldBe(new[] { "line 3", "line 4", "line 5" });
    }

    [Fact]
    public void LogHistory_returns_a_copy()
    {
        var server = new LogServer(_port, "greeting", 10);
        server.Log("original");

        server.LogHistory[0] = "changed";

        server.LogHistory.ShouldBe(new[] { "original" });
    }

    [Fact]
    public void Stop_and_Join_shut_the_server_down()
    {
        var server = new LogServer(_port, "greeting", 10);
        server.Start();
        Wait.Until(() => server.Active, "the server is running");

        server.Stop();
        server.Join();

        server.Active.ShouldBeFalse();
        Should.Throw<SocketException>(() => new TcpClient("127.0.0.1", _port));
    }

    [Fact]
    public void New_client_receives_greeting_then_history()
    {
        var server = StartServer(greeting: "Welcome to the log");
        server.Log("first");
        server.Log("second");
        Wait.Until(() => server.LogHistory.Length == 2, "lines are logged");

        using var client = new LogClient(_port);

        client.ReadLines(3).ShouldBe(new[] { "Welcome to the log", "first", "second" });
    }

    [Fact]
    public void New_client_only_receives_the_retained_history()
    {
        var server = StartServer(greeting: "hello", logSize: 2);
        server.Log("a");
        server.Log("b");
        server.Log("c");

        using var client = new LogClient(_port);

        client.ReadLines(3).ShouldBe(new[] { "hello", "b", "c" });
    }

    [Fact]
    public void Lines_logged_while_connected_are_streamed_to_every_client()
    {
        var connected = new ConcurrentQueue<string>();
        var server = StartServer(greeting: "hi", configure: s => s.onClientConnect += connected.Enqueue);
        using var first = new LogClient(_port);
        using var second = new LogClient(_port);
        Wait.Until(() => connected.Count == 2, "both clients are connected");
        first.ReadLines(1);
        second.ReadLines(1);

        server.Log("live line");

        first.ReadLines(2).ShouldBe(new[] { "hi", "live line" });
        second.ReadLines(2).ShouldBe(new[] { "hi", "live line" });
    }

    [Fact]
    public void Input_from_the_client_is_ignored()
    {
        var connected = new ConcurrentQueue<string>();
        var server = StartServer(greeting: "hi", configure: s => s.onClientConnect += connected.Enqueue);
        using var client = new LogClient(_port);
        Wait.Until(() => connected.Count == 1, "the client is connected");

        client.Send("some typing from the user\r\n");
        Thread.Sleep(200);
        server.Log("still working");

        client.ReadLines(2).ShouldBe(new[] { "hi", "still working" });
    }

    [Fact]
    public void Connect_and_disconnect_events_report_the_client_address()
    {
        var connected = new ConcurrentQueue<string>();
        var disconnected = new ConcurrentQueue<string>();
        StartServer(configure: s =>
        {
            s.onClientConnect += connected.Enqueue;
            s.onClientDisconnect += disconnected.Enqueue;
        });

        string address;
        using (var client = new LogClient(_port))
        {
            address = client.LocalAddress;
            Wait.Until(() => connected.Count == 1, "the connect event is raised");
        }

        Wait.Until(() => disconnected.Count == 1, "the disconnect event is raised");
        connected.ShouldBe(new[] { address });
        disconnected.ShouldBe(new[] { address });
    }

    [Fact]
    public void Stopping_the_server_closes_client_connections()
    {
        var connected = new ConcurrentQueue<string>();
        var server = new LogServer(_port, "hi", 10);
        server.onClientConnect += connected.Enqueue;
        server.Start();
        using var client = new LogClient(_port);
        Wait.Until(() => connected.Count == 1, "the client is connected");

        server.Stop();
        server.Join();

        client.WaitForServerClose().ShouldBeTrue();
    }

    [Fact]
    public void Port_in_use_raises_onServerException()
    {
        var blocker = new TcpListener(IPAddress.Any, _port);
        blocker.Start();
        try
        {
            var errors = new ConcurrentQueue<Exception>();
            var server = new LogServer(_port, "greeting", 10);
            server.onServerException += errors.Enqueue;

            server.Start();
            server.Join();

            errors.ShouldHaveSingleItem().ShouldBeOfType<SocketException>();
            server.Active.ShouldBeFalse();
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public void Default_constructor_listens_on_port_446()
    {
        // Port 446 may be taken on a developer machine, so only check the server tries to use it.
        var errors = new ConcurrentQueue<Exception>();
        var server = new LogServer("greeting", 10);
        server.onServerException += errors.Enqueue;
        server.Start();
        try
        {
            Wait.Until(() => server.Active || !errors.IsEmpty, "the server starts or fails");
            if (errors.IsEmpty)
            {
                using var client = new LogClient(446);
                client.ReadLines(1).ShouldBe(new[] { "greeting" });
            }
            else
            {
                errors.Single().ShouldBeOfType<SocketException>().SocketErrorCode.ShouldBe(SocketError.AddressAlreadyInUse);
            }
        }
        finally
        {
            server.Stop();
            server.Join();
        }
    }
}
