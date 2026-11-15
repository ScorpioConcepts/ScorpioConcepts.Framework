using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ScorpioConcepts.Framework.PlugIn.Tests;

/// <summary>
/// Compiles C# source into plugin DLLs on disk, so PlugInManager can discover them like real plugins.
/// </summary>
internal static class PlugInCompiler
{
    private static readonly Lazy<MetadataReference[]> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray());

    private const string Usings = """
        using System;
        using ScorpioConcepts.Framework.PlugIn;
        using ScorpioConcepts.Framework.PlugIn.Tests;

        """;

    /// <summary>Compile <paramref name="source"/> into a uniquely named DLL in <paramref name="directory"/>.</summary>
    public static string Compile(string directory, string source)
    {
        // Every assembly gets a unique name: the default load context can only hold one assembly per name.
        string name = "SamplePlugIns_" + Guid.NewGuid().ToString("N");
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(Usings + source)],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));

        string path = Path.Combine(directory, name + ".dll");
        var result = compilation.Emit(path);
        if (!result.Success)
        {
            File.Delete(path);
            var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
            throw new InvalidOperationException("Plugin source failed to compile:\n" + string.Join("\n", errors));
        }

        return path;
    }
}
