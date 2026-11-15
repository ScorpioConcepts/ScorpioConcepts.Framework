namespace ScorpioConcepts.Framework.netNUTClient.Tests;

public class NutProtocolTests
{
    [Theory]
    [InlineData("rack")]
    [InlineData("ups.status")]
    [InlineData("test.battery.start")]
    [InlineData("my-ups_2")]
    public void Word_accepts_plain_names(string name)
    {
        NutProtocol.Word(name, "name").ShouldBe(name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("rack\nINSTCMD rack load.off")]
    [InlineData("rack\r")]
    [InlineData("quote\"d")]
    [InlineData("back\\slash")]
    [InlineData("tab\there")]
    public void Word_rejects_anything_that_could_change_the_command(string name)
    {
        Should.Throw<ArgumentException>(() => NutProtocol.Word(name, "upsName")).ParamName.ShouldBe("upsName");
    }

    [Fact]
    public void Word_rejects_null()
    {
        Should.Throw<ArgumentNullException>(() => NutProtocol.Word(null!, "upsName")).ParamName.ShouldBe("upsName");
    }

    [Theory]
    [InlineData("Rack 1", "\"Rack 1\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("C:\\ups", "\"C:\\\\ups\"")]
    [InlineData("Café ☕", "\"Café ☕\"")]
    public void Quote_escapes_quotes_and_backslashes(string value, string expected)
    {
        NutProtocol.Quote(value, "value").ShouldBe(expected);
    }

    [Theory]
    [InlineData("x\"\nINSTCMD rack load.off\n\"")]
    [InlineData("line\r\nbreak")]
    [InlineData("nul\0char")]
    public void Quote_rejects_control_characters(string value)
    {
        Should.Throw<ArgumentException>(() => NutProtocol.Quote(value, "newValue")).ParamName.ShouldBe("newValue");
    }

    [Theory]
    [InlineData("secret", "secret")]
    [InlineData("pass word", "\"pass word\"")]
    [InlineData("", "\"\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    public void QuoteIfNeeded_only_quotes_when_it_has_to(string value, string expected)
    {
        NutProtocol.QuoteIfNeeded(value, "authPass").ShouldBe(expected);
    }

    [Theory]
    [InlineData("VAR rack ups.status \"OL\"\n", new[] { "VAR", "rack", "ups.status", "OL" })]
    [InlineData("UPS rack \"Server rack UPS\"", new[] { "UPS", "rack", "Server rack UPS" })]
    [InlineData("VAR rack ups.id \"say \\\"hi\\\" \\\\ bye\"", new[] { "VAR", "rack", "ups.id", "say \"hi\" \\ bye" })]
    [InlineData("UPSDESC rack \"\"", new[] { "UPSDESC", "rack", "" })]
    [InlineData("TYPE rack ups.id RW STRING:20\r\n", new[] { "TYPE", "rack", "ups.id", "RW", "STRING:20" })]
    [InlineData("  spaced   out  ", new[] { "spaced", "out" })]
    public void Tokenize_splits_words_and_removes_quoting(string line, string[] expected)
    {
        NutProtocol.Tokenize(line).ShouldBe(expected);
    }

    [Fact]
    public void Quote_and_Tokenize_round_trip()
    {
        string value = "He said \"don't\" \\ and left";

        NutProtocol.Tokenize("VAR rack ups.id " + NutProtocol.Quote(value, "v")).Last().ShouldBe(value);
    }

    [Theory]
    [InlineData("nas.local", "nas.local", 3493)]
    [InlineData("nas.local:4000", "nas.local", 4000)]
    [InlineData("192.168.1.10:3493", "192.168.1.10", 3493)]
    [InlineData("::1", "::1", 3493)]
    [InlineData("fe80::1:2", "fe80::1:2", 3493)]
    [InlineData("[::1]", "::1", 3493)]
    [InlineData("[::1]:4000", "::1", 4000)]
    [InlineData("  nas.local  ", "nas.local", 3493)]
    public void ParseServer_handles_hosts_ports_and_IPv6(string server, string host, int port)
    {
        NutProtocol.ParseServer(server, "upsdServer", out string parsedHost, out ushort parsedPort);

        parsedHost.ShouldBe(host);
        parsedPort.ShouldBe((ushort)port);
    }

    [Theory]
    [InlineData("nas.local:notaport")]
    [InlineData("nas.local:0")]
    [InlineData("nas.local:70000")]
    [InlineData("[::1]:x")]
    public void ParseServer_rejects_invalid_ports(string server)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => NutProtocol.ParseServer(server, "upsdServer", out _, out _))
            .ParamName.ShouldBe("upsdServer");
    }

    [Theory]
    [InlineData("")]
    [InlineData(":3493")]
    [InlineData("[::1")]
    [InlineData("[::1]3493")]
    public void ParseServer_rejects_malformed_addresses(string server)
    {
        Should.Throw<ArgumentException>(() => NutProtocol.ParseServer(server, "upsdServer", out _, out _))
            .ShouldNotBeOfType<ArgumentOutOfRangeException>();
    }
}
