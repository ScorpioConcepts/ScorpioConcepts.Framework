#if NET
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace ScorpioConcepts.Framework.PlugIn;

/// <summary>
/// The load context for one plugin DLL. The plugin's own dependencies load from its folder, so they can be
/// different versions from the host's. Shared assemblies, and anything not found next to the plugin, come from
/// the default context, so the plugin interfaces are the same types for the host and the plugin.
/// </summary>
internal sealed class PlugInLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver? _resolver;
    private readonly string _directory;
    private readonly HashSet<string> _shared;

    public PlugInLoadContext(string pluginPath, HashSet<string> sharedAssemblyNames)
        : base(Path.GetFileNameWithoutExtension(pluginPath))
    {
        _directory = Path.GetDirectoryName(pluginPath)!;
        _shared = sharedAssemblyNames;
        try
        {
            // Reads the plugin's .deps.json, if it has one
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }
        catch (InvalidOperationException)
        {
            // No host policy available (for example in some single-file apps): use the plugin's folder only
            _resolver = null;
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null || _shared.Contains(assemblyName.Name)) return null;

        string? path = _resolver?.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            string candidate = Path.Combine(_directory, assemblyName.Name + ".dll");
            if (File.Exists(candidate)) path = candidate;
        }

        // null falls back to the default context, which is where framework assemblies come from
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
#endif
