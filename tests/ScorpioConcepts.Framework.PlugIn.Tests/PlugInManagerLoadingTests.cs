using System.Collections;
using System.Runtime.Loader;

namespace ScorpioConcepts.Framework.PlugIn.Tests;

// Error handling per assembly and per type (#17), load contexts (#6) and the read-only PlugIns list (#21)
public sealed partial class PlugInManagerTests
{
    private const string HelperSource = """
        public static class Helper
        {{
            public static string Value() => "{0}";
        }}

        public abstract class HelperBase : IGreeterPlugIn
        {{
            public abstract string Name {{ get; }}
            public string Author => "";
            public string Description => "";
            public Version Version => new Version(1, 0);
            public ITestHost Host {{ get; private set; }}
            public void Initialize(ITestHost host) {{ Host = host; }}
            public string Greet(string name) => "";
        }}
        """;

    private const string UsesHelper = """
        public class UsesHelper : IGreeterPlugIn
        {
            public string Name => "UsesHelper";
            public string Author => "";
            public string Description => "";
            public Version Version => new Version(1, 0);
            public ITestHost Host { get; private set; }
            public void Initialize(ITestHost host) { Host = host; }
            public string Greet(string name) => Helper.Value();
        }
        """;

    private string NewDirectory(string name) => Directory.CreateDirectory(Path.Combine(_directory, name)).FullName;

    [Fact]
    public void Constructor_rejects_a_null_host()
    {
        Should.Throw<ArgumentNullException>(() => new PlugInManager<IGreeterPlugIn, ITestHost>(null!));
    }

    [Fact]
    public void Find_rejects_a_null_directory()
    {
        Should.Throw<ArgumentNullException>(() => _manager.Find(null!));
    }

    [Fact]
    public void An_invalid_dll_does_not_stop_the_other_plugins_loading()
    {
        // Sorted by name, the bad file comes first, so a scan that stopped at it would find nothing
        File.WriteAllText(Path.Combine(_directory, "aaa-not-a-dotnet-assembly.dll"), "garbage");
        PlugInCompiler.Compile(_directory, Greeter("StillFound"));

        _manager.Find(_directory);

        FoundNames().ShouldBe(new[] { "StillFound" });
        _host.Received(1).ThrowException(Arg.Is<PlugInLoadException>(ex => ex.InnerException is BadImageFormatException));
    }

    [Fact]
    public void A_plugin_that_fails_does_not_stop_the_others_in_the_same_assembly()
    {
        PlugInCompiler.Compile(_directory, """
            public class AFailsFirst : IGreeterPlugIn
            {
                public AFailsFirst() { throw new InvalidOperationException("boom"); }
                public string Name => "AFailsFirst";
                public string Author => "";
                public string Description => "";
                public Version Version => new Version(1, 0);
                public ITestHost Host => null;
                public void Initialize(ITestHost host) { }
                public string Greet(string name) => "";
            }
            """ + Greeter("Before") + Greeter("After"));

        _manager.Find(_directory);

        FoundNames().ShouldBe(new[] { "After", "Before" });
        _host.Received(1).ThrowException(Arg.Is<PlugInLoadException>(ex => ex.TypeName == "AFailsFirst"));
    }

    [Fact]
    public void Types_that_did_load_are_used_when_a_dependency_is_missing()
    {
        // The helper is compiled somewhere else, so it is missing when the plugin folder is scanned
        string helper = PlugInCompiler.Compile(NewDirectory("elsewhere"), string.Format(HelperSource, "x"), "MissingHelper");
        string plugins = NewDirectory("plugins");
        PlugInCompiler.Compile(plugins, Greeter("Loads") + """
            public class NeedsMissingBase : HelperBase
            {
                public override string Name => "NeedsMissingBase";
            }
            """, null, helper);

        _manager.Find(plugins);

        FoundNames().ShouldBe(new[] { "Loads" });
        _host.Received().ThrowException(Arg.Is<PlugInLoadException>(ex =>
            ex.InnerException is FileNotFoundException && ex.InnerException.Message.Contains("MissingHelper")));
    }

    [Fact]
    public void A_host_that_throws_from_ThrowException_does_not_stop_the_scan()
    {
        _host.When(h => h.ThrowException(Arg.Any<Exception>())).Do(_ => throw new InvalidOperationException("host failed"));
        File.WriteAllText(Path.Combine(_directory, "aaa-not-a-dotnet-assembly.dll"), "garbage");
        PlugInCompiler.Compile(_directory, Greeter("StillFound"));

        Should.NotThrow(() => _manager.Find(_directory));

        FoundNames().ShouldBe(new[] { "StillFound" });
    }

    [Fact]
    public void Skips_classes_that_are_not_public()
    {
        PlugInCompiler.Compile(_directory, Greeter("Public") + """
            internal class Hidden : IGreeterPlugIn
            {
                public string Name => "Hidden";
                public string Author => "";
                public string Description => "";
                public Version Version => new Version(1, 0);
                public ITestHost Host { get; private set; }
                public void Initialize(ITestHost host) { Host = host; }
                public string Greet(string name) => "";
            }
            """);

        _manager.Find(_directory);

        FoundNames().ShouldBe(new[] { "Public" });
    }

    [Fact]
    public void Each_plugin_assembly_gets_its_own_load_context()
    {
        PlugInCompiler.Compile(_directory, Greeter("One"));
        PlugInCompiler.Compile(_directory, Greeter("Two"));

        _manager.Find(_directory);

        var contexts = _manager.PlugIns.Select(p => AssemblyLoadContext.GetLoadContext(p.GetType().Assembly)).ToArray();
        contexts.ShouldAllBe(c => c != AssemblyLoadContext.Default);
        contexts.Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public void Plugins_share_the_hosts_contract_types()
    {
        // A copy of the contract assembly next to the plugin must not be loaded, or IGreeterPlugIn would be a
        // different type for the plugin and the cast to it would fail
        File.Copy(typeof(IGreeterPlugIn).Assembly.Location, Path.Combine(_directory, Path.GetFileName(typeof(IGreeterPlugIn).Assembly.Location)));
        File.Copy(typeof(IPlugInHost).Assembly.Location, Path.Combine(_directory, Path.GetFileName(typeof(IPlugInHost).Assembly.Location)));
        PlugInCompiler.Compile(_directory, Greeter("Shared"));

        _manager.Find(_directory);

        _manager.PlugIns.ShouldHaveSingleItem().Name.ShouldBe("Shared");
        _host.DidNotReceiveWithAnyArgs().ThrowException(default!);
    }

    [Fact]
    public void Plugins_can_ship_different_versions_of_the_same_dependency()
    {
        // Two plugin folders each ship their own "PlugInHelper.dll". In one shared load context, the second
        // plugin would get the first one's helper.
        string first = NewDirectory("first");
        string second = NewDirectory("second");
        string firstHelper = PlugInCompiler.Compile(first, string.Format(HelperSource, "first helper"), "PlugInHelper");
        string secondHelper = PlugInCompiler.Compile(second, string.Format(HelperSource, "second helper"), "PlugInHelper");
        PlugInCompiler.Compile(first, UsesHelper, null, firstHelper);
        PlugInCompiler.Compile(second, UsesHelper, null, secondHelper);
        var secondManager = new PlugInManager<IGreeterPlugIn, ITestHost>(_host);

        _manager.Find(first);
        secondManager.Find(second);

        _manager.PlugIns.ShouldHaveSingleItem().Greet("").ShouldBe("first helper");
        secondManager.PlugIns.ShouldHaveSingleItem().Greet("").ShouldBe("second helper");
        _host.DidNotReceiveWithAnyArgs().ThrowException(default!);
    }

    [Fact]
    public void Find_again_reuses_loaded_assemblies_and_picks_up_new_ones()
    {
        PlugInCompiler.Compile(_directory, Greeter("Existing"));
        _manager.Find(_directory);
        var existingAssembly = _manager.PlugIns.Single().GetType().Assembly;

        PlugInCompiler.Compile(_directory, Greeter("Added"));
        _manager.Find(_directory);

        FoundNames().ShouldBe(new[] { "Added", "Existing" });
        _manager.PlugIns.Single(p => p.Name == "Existing").GetType().Assembly.ShouldBeSameAs(existingAssembly);
    }

    [Fact]
    public void PlugIns_cannot_be_changed_by_callers()
    {
        PlugInCompiler.Compile(_directory, Greeter("ReadOnly"));
        _manager.Find(_directory);

        var list = _manager.PlugIns.ShouldBeAssignableTo<IList>()!;
        list.IsReadOnly.ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => list.Clear());
    }

    [Fact]
    public void A_list_read_before_Find_does_not_change()
    {
        PlugInCompiler.Compile(_directory, Greeter("First"));
        _manager.Find(_directory);
        var before = _manager.PlugIns;

        _manager.Find(Path.Combine(_directory, "does-not-exist"));

        before.Select(p => p.Name).ShouldBe(new[] { "First" });
        _manager.PlugIns.ShouldBeEmpty();
    }
}
