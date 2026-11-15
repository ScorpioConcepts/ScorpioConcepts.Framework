using System.Reflection;

namespace ScorpioConcepts.Framework.PlugIn.Tests;

// The contracts an application would define: a host and a plugin interface built on the library's interfaces.
// They are public so the plugin assemblies compiled by the tests can implement them.
public interface ITestHost : IPlugInHost
{
}

public interface IGreeterPlugIn : IPlugIn<ITestHost>
{
    string Greet(string name);
}

public interface IOtherPlugIn : IPlugIn<ITestHost>
{
}

public sealed class PlugInManagerTests : IDisposable
{
    private const string GreeterTemplate = """
        public class {0} : IGreeterPlugIn
        {{
            public string Name => "{0}";
            public string Author => "Test Author";
            public string Description => "Greets people";
            public Version Version => new Version(1, 2, 3);
            public ITestHost Host {{ get; private set; }}
            public void Initialize(ITestHost host) {{ Host = host; }}
            public string Greet(string name) => "{0} says hello to " + name;
        }}
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory("plugins-").FullName;
    private readonly ITestHost _host = Substitute.For<ITestHost>();
    private readonly PlugInManager<IGreeterPlugIn, ITestHost> _manager;

    public PlugInManagerTests()
    {
        _manager = new PlugInManager<IGreeterPlugIn, ITestHost>(_host);
    }

    public void Dispose()
    {
        // Loaded plugin DLLs stay locked until the process exits, so leftovers are expected.
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Greeter(string className) => string.Format(GreeterTemplate, className);

    private string[] FoundNames() => _manager.PlugIns.Select(p => p.Name).OrderBy(n => n).ToArray();

    [Fact]
    public void New_manager_has_host_and_no_plugins()
    {
        _manager.Host.ShouldBeSameAs(_host);
        _manager.PlugIns.ShouldBeEmpty();
    }

    [Fact]
    public void Missing_directory_finds_nothing()
    {
        _manager.Find(Path.Combine(_directory, "does-not-exist"));

        _manager.PlugIns.ShouldBeEmpty();
        _host.DidNotReceiveWithAnyArgs().ThrowException(default!);
    }

    [Fact]
    public void Empty_directory_finds_nothing()
    {
        _manager.Find(_directory);

        _manager.PlugIns.ShouldBeEmpty();
        _host.DidNotReceiveWithAnyArgs().ThrowException(default!);
    }

    [Fact]
    public void Finds_and_initializes_a_plugin()
    {
        PlugInCompiler.Compile(_directory, Greeter("HelloPlugIn"));

        _manager.Find(_directory);

        var plugin = _manager.PlugIns.ShouldHaveSingleItem();
        plugin.Name.ShouldBe("HelloPlugIn");
        plugin.Author.ShouldBe("Test Author");
        plugin.Version.ShouldBe(new Version(1, 2, 3));
        plugin.Host.ShouldBeSameAs(_host);
        plugin.Greet("Sam").ShouldBe("HelloPlugIn says hello to Sam");
        _host.DidNotReceiveWithAnyArgs().ThrowException(default!);
    }

    [Fact]
    public void Finds_plugins_across_several_assemblies()
    {
        PlugInCompiler.Compile(_directory, Greeter("First") + Greeter("Second"));
        PlugInCompiler.Compile(_directory, Greeter("Third"));

        _manager.Find(_directory);

        FoundNames().ShouldBe(new[] { "First", "Second", "Third" });
    }

    [Fact]
    public void Skips_abstract_types_but_finds_their_concrete_subclasses()
    {
        PlugInCompiler.Compile(_directory, """
            public abstract class GreeterBase : IGreeterPlugIn
            {
                public abstract string Name { get; }
                public string Author => "Base";
                public string Description => "Base class";
                public Version Version => new Version(1, 0);
                public ITestHost Host { get; private set; }
                public void Initialize(ITestHost host) { Host = host; }
                public string Greet(string name) => Name + ": hi " + name;
            }

            public class Derived : GreeterBase
            {
                public override string Name => "Derived";
            }
            """);

        _manager.Find(_directory);

        FoundNames().ShouldBe(new[] { "Derived" });
        _host.DidNotReceiveWithAnyArgs().ThrowException(default!);
    }

    [Fact]
    public void Ignores_classes_that_are_not_this_kind_of_plugin()
    {
        PlugInCompiler.Compile(_directory, Greeter("Wanted") + """
            public class NotAPlugIn { }

            public class OtherKind : IOtherPlugIn
            {
                public string Name => "OtherKind";
                public string Author => "";
                public string Description => "";
                public Version Version => new Version(1, 0);
                public ITestHost Host { get; private set; }
                public void Initialize(ITestHost host) { Host = host; }
            }
            """);

        _manager.Find(_directory);

        FoundNames().ShouldBe(new[] { "Wanted" });
    }

    [Fact]
    public void Only_scans_the_top_level_directory()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_directory, "nested")).FullName;
        PlugInCompiler.Compile(_directory, Greeter("TopLevel"));
        PlugInCompiler.Compile(nested, Greeter("Nested"));

        _manager.Find(_directory);

        FoundNames().ShouldBe(new[] { "TopLevel" });
    }

    [Fact]
    public void Find_replaces_previous_results()
    {
        PlugInCompiler.Compile(_directory, Greeter("Once"));

        _manager.Find(_directory);
        _manager.Find(_directory);

        FoundNames().ShouldBe(new[] { "Once" });
    }

    [Fact]
    public void Each_Find_creates_new_instances()
    {
        PlugInCompiler.Compile(_directory, Greeter("Fresh"));

        _manager.Find(_directory);
        var first = _manager.PlugIns.Single();
        _manager.Find(_directory);

        _manager.PlugIns.Single().ShouldNotBeSameAs(first);
    }

    [Fact]
    public void Invalid_dll_is_reported_to_the_host()
    {
        File.WriteAllText(Path.Combine(_directory, "not-a-dotnet-assembly.dll"), "garbage");

        _manager.Find(_directory);

        _manager.PlugIns.ShouldBeEmpty();
        _host.Received(1).ThrowException(Arg.Any<BadImageFormatException>());
    }

    [Fact]
    public void Exception_from_plugin_constructor_is_reported_to_the_host()
    {
        PlugInCompiler.Compile(_directory, """
            public class Exploding : IGreeterPlugIn
            {
                public Exploding() { throw new InvalidOperationException("boom"); }
                public string Name => "Exploding";
                public string Author => "";
                public string Description => "";
                public Version Version => new Version(1, 0);
                public ITestHost Host => null;
                public void Initialize(ITestHost host) { }
                public string Greet(string name) => "";
            }
            """);

        _manager.Find(_directory);

        _host.Received(1).ThrowException(Arg.Is<Exception>(ex =>
            ex is TargetInvocationException && ex.InnerException!.Message == "boom"));
    }

    [Fact]
    public void Exception_from_Initialize_is_reported_to_the_host()
    {
        PlugInCompiler.Compile(_directory, """
            public class FailsToInitialize : IGreeterPlugIn
            {
                public string Name => "FailsToInitialize";
                public string Author => "";
                public string Description => "";
                public Version Version => new Version(1, 0);
                public ITestHost Host => null;
                public void Initialize(ITestHost host) { throw new InvalidOperationException("init failed"); }
                public string Greet(string name) => "";
            }
            """);

        _manager.Find(_directory);

        _manager.PlugIns.ShouldBeEmpty();
        _host.Received(1).ThrowException(Arg.Is<InvalidOperationException>(ex => ex.Message == "init failed"));
    }

    [Fact]
    public void Plugin_without_parameterless_constructor_is_reported_to_the_host()
    {
        PlugInCompiler.Compile(_directory, """
            public class NeedsArgument : IGreeterPlugIn
            {
                public NeedsArgument(string setting) { }
                public string Name => "NeedsArgument";
                public string Author => "";
                public string Description => "";
                public Version Version => new Version(1, 0);
                public ITestHost Host => null;
                public void Initialize(ITestHost host) { }
                public string Greet(string name) => "";
            }
            """);

        _manager.Find(_directory);

        _host.Received(1).ThrowException(Arg.Any<MissingMethodException>());
    }

    [Fact(Skip = "Known bug: Find matches with GetInterface(FullName), which never matches a closed generic interface such as IPlugIn<THost>")]
    public void Can_use_the_base_plugin_interface_directly()
    {
        PlugInCompiler.Compile(_directory, Greeter("ViaBaseInterface"));
        var manager = new PlugInManager<IPlugIn<ITestHost>, ITestHost>(_host);

        manager.Find(_directory);

        manager.PlugIns.ShouldHaveSingleItem().Name.ShouldBe("ViaBaseInterface");
        _host.DidNotReceiveWithAnyArgs().ThrowException(default!);
    }
}
