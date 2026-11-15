using System.Text;

namespace ScorpioConcepts.Framework.netNUTClient.Tests;

/// <summary>
/// End-to-end checks that values reach upsd as one argument each, and can't add a command of their own.
/// </summary>
public sealed class UPSDClientSecurityTests : IDisposable
{
    private readonly FakeUpsdServer _server = new();
    private readonly UPSDClient _client;

    public UPSDClientSecurityTests()
    {
        _client = new UPSDClient("127.0.0.1", (ushort)_server.Port) { ReplyTimeout = TimeSpan.FromSeconds(5) };
        _client.Connect();
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    [Fact]
    public void A_value_cannot_inject_a_second_command()
    {
        // This exact value used to make the server run INSTCMD rack load.off, cutting power to the load
        Should.Throw<ArgumentException>(() => _client.SetUPSVariable("rack", "ups.id", "x\"\nINSTCMD rack load.off\n\""))
            .ParamName.ShouldBe("newValue");

        Thread.Sleep(200);
        _server.ReceivedCommands.ShouldBeEmpty();
        _client.Connected.ShouldBeTrue();
    }

    [Fact]
    public void Quotes_and_backslashes_in_a_value_are_escaped()
    {
        _server.Reply("SET VAR rack ups.id \"say \\\"hi\\\" \\\\ bye\"", "OK\n");

        _client.SetUPSVariable("rack", "ups.id", "say \"hi\" \\ bye").ShouldBeTrue();
    }

    [Fact]
    public void Instant_command_parameter_is_escaped()
    {
        _server.Reply("INSTCMD rack load.off.delay \"12\\\"0\"", "OK\n");

        _client.InstantCommand("rack", "load.off.delay", "12\"0").ShouldBeTrue();
    }

    [Fact]
    public void Password_with_spaces_is_sent_as_one_quoted_word()
    {
        _server.Reply("PASSWORD \"correct horse\"", "OK\n");

        _client.SetPassword("correct horse").ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(CommandsWithABadName))]
    public void Names_that_are_not_single_words_are_rejected(string paramName, Action<UPSDClient> command)
    {
        Should.Throw<ArgumentException>(() => command(_client)).ParamName.ShouldBe(paramName);

        Thread.Sleep(100);
        _server.ReceivedCommands.ShouldBeEmpty();
    }

    public static TheoryData<string, Action<UPSDClient>> CommandsWithABadName() => new()
    {
        { "upsName", c => c.GetUPSVar("rack\nLOGOUT", "ups.status") },
        { "varName", c => c.GetUPSVar("rack", "ups.status rack") },
        { "upsName", c => c.ListUPSVar("a b") },
        { "upsName", c => c.ListUPSCommands("rack\"") },
        { "cmdName", c => c.GetUPSCmdDescription("rack", "") },
        { "command", c => c.InstantCommand("rack", "beeper.off\nINSTCMD rack load.off") },
        { "authUser", c => c.SetUsername("admin root") },
        { "upsName", c => c.Login("rack\r\nLOGOUT") },
        { "varName", c => c.SetUPSVariable("rack", "ups.id\n", "x") },
    };

    [Fact]
    public void Escaped_quotes_in_replies_are_unescaped()
    {
        _server
            .Reply("GET VAR rack ups.id", "VAR rack ups.id \"say \\\"hi\\\" \\\\ bye\"\n")
            .Reply("LIST UPS", "BEGIN LIST UPS\nUPS rack \"The \\\"main\\\" rack\"\nEND LIST UPS\n");

        _client.GetUPSVar("rack", "ups.id").ShouldBe("say \"hi\" \\ bye");
        _client.ListUPS().ShouldHaveSingleItem().Description.ShouldBe("The \"main\" rack");
    }

    [Fact]
    public void Spaces_inside_values_are_kept()
    {
        _server.Reply("GET UPSDESC rack", "UPSDESC rack \"  padded  \"\n");

        _client.GetUPSDescription("rack").ShouldBe("  padded  ");
    }

    [Fact]
    public void UTF8_replies_are_decoded_even_when_a_character_is_split_across_packets()
    {
        const string reply = "UPSDESC rack \"Café ☕ rack\"\n";
        // Byte offsets inside é (2 bytes in UTF-8) and inside ☕ (3 bytes), so both arrive in two pieces
        int e = Encoding.UTF8.GetByteCount(reply[..reply.IndexOf('é')]);
        int cup = Encoding.UTF8.GetByteCount(reply[..reply.IndexOf('☕')]);
        _server.ReplyInPieces("GET UPSDESC rack", reply, e + 1, cup + 1, cup + 2);

        _client.GetUPSDescription("rack").ShouldBe("Café ☕ rack");
    }
}
