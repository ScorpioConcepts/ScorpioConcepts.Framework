namespace ScorpioConcepts.Framework.netNUTClient.Tests;

public sealed class UPSDClientTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly FakeUpsdServer _server = new();
    private readonly UPSDClient _client;

    public UPSDClientTests()
    {
        _client = new UPSDClient("127.0.0.1", (ushort)_server.Port);
    }

    public void Dispose()
    {
        // Disconnect explicitly so the client's finalizer has nothing left to do.
        try { if (_client.Connected) RunWithTimeout(_client.Disconnect); } catch { }
        _server.Dispose();
    }

    /// <summary>
    /// Several client calls spin forever if the reply never matches, so every call
    /// goes through here to turn a hang into a test failure.
    /// </summary>
    private static T RunWithTimeout<T>(Func<T> call)
    {
        var task = Task.Run(call);
        if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(Timeout)) throw new TimeoutException("UPSDClient call did not return");
        return task.GetAwaiter().GetResult();
    }

    private static void RunWithTimeout(Action call) => RunWithTimeout(() => { call(); return 0; });

    private UPSDClient Connected()
    {
        _client.Connect();
        return _client;
    }

    [Fact]
    public void Server_string_with_invalid_port_is_rejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new UPSDClient("localhost:notaport"))
            .ParamName.ShouldBe("upsdServer");
    }

    [Fact]
    public void Server_string_with_port_connects_to_that_port()
    {
        var client = new UPSDClient("127.0.0.1:" + _server.Port);

        client.Connect();

        client.Connected.ShouldBeTrue();
        RunWithTimeout(client.Disconnect);
    }

    [Fact]
    public void Commands_require_a_connection()
    {
        var client = new UPSDClient("127.0.0.1", (ushort)_server.Port);

        client.Connected.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => client.ListUPS()).Message.ShouldContain("Connect()");
        Should.Throw<InvalidOperationException>(() => client.GetUPSVar("ups", "ups.status")).Message.ShouldContain("Connect()");
        Should.Throw<InvalidOperationException>(() => client.InstantCommand("ups", "beeper.off")).Message.ShouldContain("Connect()");
        Should.Throw<InvalidOperationException>(() => client.Logout()).Message.ShouldContain("Connect()");
    }

    [Fact]
    public void Disconnect_when_not_connected_does_nothing()
    {
        Should.NotThrow(() => _client.Disconnect());
        _client.Connected.ShouldBeFalse();
    }

    [Fact]
    public void Disconnect_without_login_closes_without_logging_out()
    {
        _client.Connect();
        _client.Connected.ShouldBeTrue();

        RunWithTimeout(_client.Disconnect);

        _client.Connected.ShouldBeFalse();
        _server.ReceivedCommands.ShouldNotContain("LOGOUT");
    }

    [Fact]
    public void Disconnect_after_login_logs_out_first()
    {
        _server.Reply("LOGIN rack", "OK\n");
        var client = Connected();
        RunWithTimeout(() => client.Login("rack"));

        RunWithTimeout(client.Disconnect);

        client.Connected.ShouldBeFalse();
        client.LoggedIn.ShouldBeFalse();
        _server.ReceivedCommands.ShouldBe(new[] { "LOGIN rack", "LOGOUT" });
    }

    [Fact]
    public void Disconnect_after_login_still_closes_when_the_server_has_gone()
    {
        _server.Reply("LOGIN rack", "OK\n");
        var client = Connected();
        RunWithTimeout(() => client.Login("rack"));
        _server.DropClient();

        Should.NotThrow(() => RunWithTimeout(client.Disconnect));

        client.Connected.ShouldBeFalse();
        client.LoggedIn.ShouldBeFalse();
    }

    [Fact]
    public void Dispose_disconnects()
    {
        _server.Reply("LOGIN rack", "OK\n");
        var client = Connected();
        RunWithTimeout(() => client.Login("rack"));

        RunWithTimeout(client.Dispose);

        client.Connected.ShouldBeFalse();
        _server.ReceivedCommands.ShouldContain("LOGOUT");
    }

    [Fact]
    public void Connect_twice_keeps_the_existing_connection()
    {
        _server.Reply("GET VAR ups ups.status", "VAR ups ups.status \"OL\"\n");
        _client.Connect();

        _client.Connect();

        RunWithTimeout(() => _client.GetUPSVar("ups", "ups.status")).ShouldBe("OL");
    }

    [Fact]
    public void ListUPS_parses_ups_names_and_descriptions()
    {
        _server.Reply("LIST UPS",
            "BEGIN LIST UPS\n" +
            "UPS rack \"Server rack UPS\"\n" +
            "UPS desk \"Desk UPS\"\n" +
            "END LIST UPS\n");

        var list = RunWithTimeout(Connected().ListUPS);

        list.Select(u => (u.Name, u.Description, u.Host)).ShouldBe(new[]
        {
            ("rack", "Server rack UPS", "localhost"),
            ("desk", "Desk UPS", "localhost"),
        });
    }

    [Fact]
    public void ListUPS_handles_an_empty_list()
    {
        _server.Reply("LIST UPS", "BEGIN LIST UPS\nEND LIST UPS\n");

        RunWithTimeout(Connected().ListUPS).ShouldBeEmpty();
    }

    [Fact]
    public void ListUPS_handles_replies_split_across_packets()
    {
        _server.FragmentSize = 5;
        _server.Reply("LIST UPS", "BEGIN LIST UPS\nUPS rack \"Server rack UPS\"\nEND LIST UPS\n");

        RunWithTimeout(Connected().ListUPS).ShouldHaveSingleItem().Name.ShouldBe("rack");
    }

    [Fact]
    public void ListUPSVar_parses_variables_and_strips_quotes()
    {
        _server.Reply("LIST VAR rack",
            "BEGIN LIST VAR rack\n" +
            "VAR rack battery.charge \"100\"\n" +
            "VAR rack ups.status \"OL CHRG\"\n" +
            "END LIST VAR rack\n");

        var vars = RunWithTimeout(() => Connected().ListUPSVar("rack"));

        vars.ShouldBe(new Dictionary<string, string> { ["battery.charge"] = "100", ["ups.status"] = "OL CHRG" });
    }

    [Fact]
    public void ListUPSReadWrite_looks_up_description_and_type_of_each_variable()
    {
        _server
            .Reply("LIST RW rack", "BEGIN LIST RW rack\nRW rack ups.id \"Rack 1\"\nEND LIST RW rack\n")
            .Reply("GET DESC rack ups.id", "DESC rack ups.id \"UPS system identifier\"\n")
            .Reply("GET TYPE rack ups.id", "TYPE rack ups.id RW STRING:20\n");

        var vars = RunWithTimeout(() => Connected().ListUPSReadWrite("rack"));

        var variable = vars.ShouldHaveSingleItem();
        variable.Name.ShouldBe("ups.id");
        variable.Description.ShouldBe("UPS system identifier");
        variable.Type.ShouldBe("STRING:20");
        variable.Value.ShouldBe("Rack 1");
    }

    [Fact]
    public void ListUPSCommands_looks_up_description_of_each_command()
    {
        _server
            .Reply("LIST CMD rack", "BEGIN LIST CMD rack\nCMD rack beeper.off\nCMD rack test.battery.start\nEND LIST CMD rack\n")
            .Reply("GET CMDDESC rack beeper.off", "CMDDESC rack beeper.off \"Disable the UPS beeper\"\n")
            .Reply("GET CMDDESC rack test.battery.start", "CMDDESC rack test.battery.start \"Start a battery test\"\n");

        var commands = RunWithTimeout(() => Connected().ListUPSCommands("rack"));

        commands.ShouldBe(new Dictionary<string, string>
        {
            ["beeper.off"] = "Disable the UPS beeper",
            ["test.battery.start"] = "Start a battery test",
        });
    }

    [Fact]
    public void ListUPSClient_returns_client_addresses()
    {
        _server.Reply("LIST CLIENT rack", "BEGIN LIST CLIENT rack\nCLIENT rack 192.168.1.10\nCLIENT rack ::1\nEND LIST CLIENT rack\n");

        RunWithTimeout(() => Connected().ListUPSClient("rack")).ShouldBe(new[] { "192.168.1.10", "::1" });
    }

    [Fact]
    public void GetUPSVar_returns_unquoted_value()
    {
        _server.Reply("GET VAR rack ups.status", "VAR rack ups.status \"OL\"\n");

        RunWithTimeout(() => Connected().GetUPSVar("rack", "ups.status")).ShouldBe("OL");
    }

    [Fact]
    public void GetUPSVarType_returns_type()
    {
        _server.Reply("GET TYPE rack ups.id", "TYPE rack ups.id RW STRING:20\n");

        RunWithTimeout(() => Connected().GetUPSVarType("rack", "ups.id")).ShouldBe("RW STRING:20");
    }

    [Fact]
    public void GetUPSDescription_returns_unquoted_description()
    {
        _server.Reply("GET UPSDESC rack", "UPSDESC rack \"Server rack UPS\"\n");

        RunWithTimeout(() => Connected().GetUPSDescription("rack")).ShouldBe("Server rack UPS");
    }

    [Fact]
    public void GetUPSVarDescription_returns_unquoted_description()
    {
        _server.Reply("GET DESC rack battery.charge", "DESC rack battery.charge \"Battery charge (percent)\"\n");

        RunWithTimeout(() => Connected().GetUPSVarDescription("rack", "battery.charge")).ShouldBe("Battery charge (percent)");
    }

    [Fact]
    public void GetUPSCmdDescription_returns_unquoted_description()
    {
        _server.Reply("GET CMDDESC rack beeper.off", "CMDDESC rack beeper.off \"Disable the UPS beeper\"\n");

        RunWithTimeout(() => Connected().GetUPSCmdDescription("rack", "beeper.off")).ShouldBe("Disable the UPS beeper");
    }

    [Fact]
    public void SetUsername_and_SetPassword_send_credentials()
    {
        _server.Reply("USERNAME admin", "OK\n").Reply("PASSWORD secret", "OK\n");
        var client = Connected();

        RunWithTimeout(() => client.SetUsername("admin")).ShouldBeTrue();
        RunWithTimeout(() => client.SetPassword("secret")).ShouldBeTrue();

        _server.ReceivedCommands.ShouldBe(new[] { "USERNAME admin", "PASSWORD secret" });
    }

    [Fact]
    public void SetUPSVariable_quotes_the_new_value()
    {
        _server.Reply("SET VAR rack ups.id \"Rack 2\"", "OK\n");

        RunWithTimeout(() => Connected().SetUPSVariable("rack", "ups.id", "Rack 2")).ShouldBeTrue();
    }

    [Fact]
    public void LoggedIn_reflects_login_state()
    {
        _server.Reply("LOGIN rack", "OK\n");
        var client = Connected();
        client.LoggedIn.ShouldBeFalse();

        RunWithTimeout(() => client.Login("rack"));
        client.LoggedIn.ShouldBeTrue();

        RunWithTimeout(client.Logout);
        client.LoggedIn.ShouldBeFalse();
    }

    [Fact]
    public void Failed_login_does_not_set_LoggedIn()
    {
        _server.Reply("LOGIN rack", "ERR ACCESS-DENIED\n");
        var client = Connected();

        Should.Throw<UPSException>(() => RunWithTimeout(() => client.Login("rack")));

        client.LoggedIn.ShouldBeFalse();
    }

    [Fact]
    public void Login_sends_login_for_ups()
    {
        _server.Reply("LOGIN rack", "OK\n");

        RunWithTimeout(() => Connected().Login("rack")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("OK Goodbye\n")]
    [InlineData("Goodbye...\n")]
    public void Logout_accepts_both_goodbye_replies(string reply)
    {
        _server.Reply("LOGOUT", reply);

        RunWithTimeout(Connected().Logout).ShouldBeTrue();
    }

    [Fact]
    public void InstantCommand_without_parameter()
    {
        _server.Reply("INSTCMD rack beeper.off", "OK\n");

        RunWithTimeout(() => Connected().InstantCommand("rack", "beeper.off")).ShouldBeTrue();
    }

    [Fact]
    public void InstantCommand_with_parameter_quotes_it()
    {
        _server.Reply("INSTCMD rack load.off.delay \"120\"", "OK\n");

        RunWithTimeout(() => Connected().InstantCommand("rack", "load.off.delay", "120")).ShouldBeTrue();
    }

    [Fact]
    public void Command_does_not_hang_when_server_drops_the_connection()
    {
        var client = Connected();
        _server.DropClient();

        // Depending on timing the write either fails, or succeeds and the read sees the closed socket.
        try
        {
            RunWithTimeout(() => client.Login("rack")).ShouldBeFalse();
        }
        catch (IOException)
        {
        }

        // Either way the broken connection is dropped, so a reconnect is needed
        client.Connected.ShouldBeFalse();
    }

    [Fact]
    public void Command_times_out_when_server_never_replies()
    {
        _server.Reply("LOGIN rack", "");
        _client.ReplyTimeout = TimeSpan.FromMilliseconds(300);

        var ex = Should.Throw<TimeoutException>(() => RunWithTimeout(() => Connected().Login("rack")));

        ex.Message.ShouldContain("LOGIN");
    }

    [Fact]
    public void List_command_times_out_when_the_list_never_ends()
    {
        _server.Reply("LIST UPS", "BEGIN LIST UPS\nUPS rack \"Rack\"\n");
        _client.ReplyTimeout = TimeSpan.FromMilliseconds(300);

        Should.Throw<TimeoutException>(() => RunWithTimeout(Connected().ListUPS));
    }

    [Fact]
    public void Timeout_message_does_not_include_command_arguments()
    {
        _server.Reply("PASSWORD hunter2", "");
        _client.ReplyTimeout = TimeSpan.FromMilliseconds(300);

        var ex = Should.Throw<TimeoutException>(() => RunWithTimeout(() => Connected().SetPassword("hunter2")));

        ex.Message.ShouldContain("PASSWORD");
        ex.Message.ShouldNotContain("hunter2");
    }

    [Fact]
    public void Timeout_closes_the_connection()
    {
        _server.ReplyAfter("GET VAR rack ups.status", "VAR rack ups.status \"OL\"\n", TimeSpan.FromMilliseconds(500));
        _client.ReplyTimeout = TimeSpan.FromMilliseconds(200);
        var client = Connected();

        Should.Throw<TimeoutException>(() => RunWithTimeout(() => client.GetUPSVar("rack", "ups.status")))
            .Message.ShouldContain("connection has been closed");

        client.Connected.ShouldBeFalse();
        client.LoggedIn.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => client.GetUPSVar("rack", "battery.charge"));
    }

    [Fact]
    public void Late_reply_is_never_read_as_the_reply_to_the_next_command()
    {
        _server
            .ReplyAfter("GET VAR rack ups.status", "VAR rack ups.status \"OL\"\n", TimeSpan.FromMilliseconds(400))
            .Reply("GET VAR rack battery.charge", "VAR rack battery.charge \"100\"\n");
        _client.ReplyTimeout = TimeSpan.FromMilliseconds(150);
        var client = Connected();
        Should.Throw<TimeoutException>(() => RunWithTimeout(() => client.GetUPSVar("rack", "ups.status")));
        Thread.Sleep(400); // the late reply has now been sent

        client.ReplyTimeout = TimeSpan.FromSeconds(5);
        client.Connect();

        RunWithTimeout(() => client.GetUPSVar("rack", "battery.charge")).ShouldBe("100");
        _server.Connections.ShouldBe(2);
    }

    [Fact]
    public void Error_reply_keeps_the_connection_usable()
    {
        _server
            .Reply("GET VAR rack missing.var", "ERR VAR-NOT-SUPPORTED\n")
            .Reply("GET VAR rack ups.status", "VAR rack ups.status \"OL\"\n");
        var client = Connected();

        Should.Throw<UPSException>(() => RunWithTimeout(() => client.GetUPSVar("rack", "missing.var")));

        client.Connected.ShouldBeTrue();
        RunWithTimeout(() => client.GetUPSVar("rack", "ups.status")).ShouldBe("OL");
        _server.Connections.ShouldBe(1);
    }

    [Fact]
    public void ReplyTimeout_defaults_to_ten_seconds()
    {
        _client.ReplyTimeout.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Get_returns_empty_when_reply_is_for_something_else()
    {
        _server.Reply("GET VAR rack ups.status", "VAR rack battery.charge \"100\"\n");

        RunWithTimeout(() => Connected().GetUPSVar("rack", "ups.status")).ShouldBe("");
    }

    [Fact]
    public void List_returns_empty_when_reply_is_not_a_list()
    {
        _server.Reply("LIST UPS", "OK\n");

        RunWithTimeout(Connected().ListUPS).ShouldBeEmpty();
    }

    [Fact]
    public void Ok_command_returns_false_for_unexpected_reply()
    {
        _server.Reply("INSTCMD rack beeper.off", "OK but not quite\n");

        RunWithTimeout(() => Connected().InstantCommand("rack", "beeper.off")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("ACCESS-DENIED", UPSException.ErrorCode.ACCESS_DENIED)]
    [InlineData("UNKNOWN-UPS", UPSException.ErrorCode.UNKNOWN_UPS)]
    [InlineData("VAR-NOT-SUPPORTED", UPSException.ErrorCode.VAR_NOT_SUPPORTED)]
    [InlineData("CMD-NOT-SUPPORTED", UPSException.ErrorCode.CMD_NOT_SUPPORTED)]
    [InlineData("DATA-STALE", UPSException.ErrorCode.DATA_STALE)]
    [InlineData("USERNAME-REQUIRED", UPSException.ErrorCode.USERNAME_REQUIRED)]
    public void Error_replies_become_UPSException_with_matching_code(string error, UPSException.ErrorCode expected)
    {
        _server.Reply("GET VAR rack ups.status", $"ERR {error}\n");

        var ex = Should.Throw<UPSException>(() => RunWithTimeout(() => Connected().GetUPSVar("rack", "ups.status")));

        ex.Code.ShouldBe(expected);
    }

    [Fact]
    public void Error_replies_are_raised_for_list_commands()
    {
        _server.Reply("LIST VAR missing", "ERR UNKNOWN-UPS\n");

        Should.Throw<UPSException>(() => RunWithTimeout(() => Connected().ListUPSVar("missing")))
            .Code.ShouldBe(UPSException.ErrorCode.UNKNOWN_UPS);
    }

    [Fact]
    public void Unrecognised_error_becomes_UNKNOWN_ERROR()
    {
        _server.Reply("LOGIN rack", "ERR SOMETHING-NEW\n");

        var ex = Should.Throw<UPSException>(() => RunWithTimeout(() => Connected().Login("rack")));

        ex.Code.ShouldBe(UPSException.ErrorCode.UNKNOWN_ERROR);
        ex.Message.ShouldBe("SOMETHING-NEW is not a recognized UPSD Error Code");
    }

    [Fact]
    public void Error_without_extra_text_has_empty_message()
    {
        _server.Reply("LOGIN rack", "ERR ACCESS-DENIED\n");

        Should.Throw<UPSException>(() => RunWithTimeout(() => Connected().Login("rack")))
            .Message.ShouldBe("");
    }

    [Fact]
    public void Error_extra_text_is_kept_without_the_line_ending()
    {
        _server.Reply("SET VAR rack ups.id \"x\"", "ERR INVALID-ARGUMENT value too long\r\n");

        var ex = Should.Throw<UPSException>(() => RunWithTimeout(() => Connected().SetUPSVariable("rack", "ups.id", "x")));

        ex.Code.ShouldBe(UPSException.ErrorCode.INVALID_ARGUMENT);
        ex.Message.ShouldBe("value too long");
    }
}
