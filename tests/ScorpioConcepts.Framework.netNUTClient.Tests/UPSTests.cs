namespace ScorpioConcepts.Framework.netNUTClient.Tests;

public class UPSTests
{
    [Fact]
    public void Name_only_defaults_host_to_localhost()
    {
        var ups = new UPS("rack");

        ups.Name.ShouldBe("rack");
        ups.Host.ShouldBe("localhost");
        ups.ToString().ShouldBe("rack@localhost");
    }

    [Fact]
    public void Name_and_host_are_split_on_at_sign()
    {
        var ups = new UPS("rack@nas.local");

        ups.Name.ShouldBe("rack");
        ups.Host.ShouldBe("nas.local");
    }

    [Fact]
    public void Port_stays_part_of_the_host()
    {
        var ups = new UPS("rack@nas.local:3493");

        ups.Name.ShouldBe("rack");
        ups.Host.ShouldBe("nas.local:3493");
        ups.ToString().ShouldBe("rack@nas.local:3493");
    }

    [Fact]
    public void Description_is_settable()
    {
        var ups = new UPS("rack") { Description = "Server rack UPS" };

        ups.Description.ShouldBe("Server rack UPS");
    }

    [Fact]
    public void VariableDescription_holds_values_and_formats_name_and_value()
    {
        var variable = new UPS.VariableDescription("ups.id", "UPS system identifier", "STRING:20", "Rack 1");

        variable.Name.ShouldBe("ups.id");
        variable.Description.ShouldBe("UPS system identifier");
        variable.Type.ShouldBe("STRING:20");
        variable.Value.ShouldBe("Rack 1");
        variable.ToString().ShouldBe("ups.id: Rack 1");
    }
}

public class UPSExceptionTests
{
    [Fact]
    public void Holds_code_and_message()
    {
        var ex = new UPSException(UPSException.ErrorCode.DATA_STALE, "stale");

        ex.Code.ShouldBe(UPSException.ErrorCode.DATA_STALE);
        ex.Message.ShouldBe("stale");
    }

    [Theory]
    [InlineData(UPSException.ErrorCode.ACCESS_DENIED, "Access Denied")]
    [InlineData(UPSException.ErrorCode.UNKNOWN_UPS, "Unknown UPS")]
    [InlineData(UPSException.ErrorCode.VAR_NOT_SUPPORTED, "Variable not supported by UPS")]
    [InlineData(UPSException.ErrorCode.CMD_NOT_SUPPORTED, "Instant Command not supported by UPS")]
    public void Description_has_friendly_text_for_common_codes(UPSException.ErrorCode code, string expected)
    {
        new UPSException(code, "").Description.ShouldBe(expected);
    }

    [Fact]
    public void Every_code_has_its_own_readable_description()
    {
        var descriptions = Enum.GetValues<UPSException.ErrorCode>()
            .ToDictionary(code => code, code => new UPSException(code, "").Description);

        descriptions.Values.ShouldAllBe(d => d.Length > 0 && !d.Contains('_'));
        descriptions.Values.Distinct().Count().ShouldBe(descriptions.Count);
        descriptions[UPSException.ErrorCode.DATA_STALE].ShouldBe("UPS data is stale");
        descriptions[UPSException.ErrorCode.UNKNOWN_ERROR].ShouldBe("Unknown error");
    }

    [Fact]
    public void Standard_constructors_use_UNKNOWN_ERROR()
    {
        new UPSException().Code.ShouldBe(UPSException.ErrorCode.UNKNOWN_ERROR);
        new UPSException("message").Message.ShouldBe("message");

        var inner = new InvalidOperationException("inner");
        var ex = new UPSException("outer", inner);
        ex.Code.ShouldBe(UPSException.ErrorCode.UNKNOWN_ERROR);
        ex.InnerException.ShouldBeSameAs(inner);
    }
}
