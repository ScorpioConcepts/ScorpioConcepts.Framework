using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace ScorpioConcepts.Framework.PlugIn;

/// <summary>
/// Finds, creates and initializes the plugins in a folder. Use it as it is, or subclass it to add helpers such as
/// calling a method on every plugin.
/// </summary>
/// <typeparam name="TPlugIn">The plugin interface or base class to look for.</typeparam>
/// <typeparam name="THost">The type of the host that is passed to each plugin.</typeparam>
public class PlugInManager<TPlugIn, THost>
    where TPlugIn : IPlugIn<THost>
    where THost : IPlugInHost
{
    private readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _findLock = new();
    private IReadOnlyList<TPlugIn> _plugIns = Array.Empty<TPlugIn>();

    /// <summary>
    /// The plugins found by the last call to <see cref="Find"/>. <see cref="Find"/> replaces the whole list when
    /// it finishes, so a list read earlier never changes.
    /// </summary>
    public IReadOnlyList<TPlugIn> PlugIns => Volatile.Read(ref _plugIns);

    /// <summary>The host that is passed to each plugin.</summary>
    public THost Host { get; }

    /// <summary>Creates a plugin manager for the given host.</summary>
    /// <param name="host">The host that is passed to each plugin.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
    public PlugInManager(THost host)
    {
        if (host is null) throw new ArgumentNullException(nameof(host));
        Host = host;
    }

    /// <summary>
    /// Loads every <c>*.dll</c> in <paramref name="directory"/> (not its subfolders), creates an instance of each
    /// public, non-abstract class that is a <typeparamref name="TPlugIn"/>, and calls its
    /// <see cref="IPlugIn{THost}.Initialize"/>. The plugins found replace <see cref="PlugIns"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Loading a DLL runs code from it, so <paramref name="directory"/> must be protected as well as the
    /// application itself: anyone who can write a DLL there can run code in the application.
    /// </para>
    /// <para>
    /// A DLL or a plugin that fails to load does not stop the others. Each failure is passed to
    /// <see cref="IPlugInHost.ThrowException"/> as a <see cref="PlugInLoadException"/>.
    /// </para>
    /// <para>
    /// On .NET 10, each DLL is loaded into its own <c>AssemblyLoadContext</c>, so the dependencies it ships with
    /// can be different versions from the host's. A DLL is loaded only once per manager: calling Find again
    /// creates new plugin instances from the same assemblies and picks up DLLs added since the last call, but a
    /// DLL that has changed is only seen after the application restarts.
    /// </para>
    /// </remarks>
    /// <param name="directory">The full path of the folder to scan. A folder that doesn't exist has no plugins.</param>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> is null.</exception>
    public void Find(string directory)
    {
        if (directory is null) throw new ArgumentNullException(nameof(directory));

        lock (_findLock)
        {
            var found = new List<TPlugIn>();
            if (Directory.Exists(directory))
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Report(new PlugInLoadException(directory, null, ex));
                    files = Array.Empty<string>();
                }

                // A stable order, so plugins come out the same way on every platform
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    FindInAssembly(Path.GetFullPath(file), found);
                }
            }

            Volatile.Write(ref _plugIns, found.AsReadOnly());
        }
    }

    private void FindInAssembly(string file, List<TPlugIn> found)
    {
        Type[] types;
        try
        {
            types = LoadAssembly(file).GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types need a dependency that is missing. Report why, and use the types that did load.
            foreach (Exception loaderException in ex.LoaderExceptions.OfType<Exception>())
            {
                Report(new PlugInLoadException(file, null, loaderException));
            }
            types = ex.Types.OfType<Type>().ToArray();
        }
        catch (Exception ex)
        {
            Report(new PlugInLoadException(file, null, ex));
            return;
        }

        Type pluginType = typeof(TPlugIn);
        foreach (Type type in types)
        {
            // Interfaces, abstract classes and open generic types cannot be created, and private classes are not plugins
            if (type.IsAbstract || type.ContainsGenericParameters || !(type.IsPublic || type.IsNestedPublic)) continue;
            if (!pluginType.IsAssignableFrom(type)) continue;

            try
            {
                var plugin = (TPlugIn)Activator.CreateInstance(type)!;
                plugin.Initialize(Host);
                found.Add(plugin);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                // The constructor threw: report its exception rather than the reflection wrapper
                Report(new PlugInLoadException(file, type.FullName, ex.InnerException));
            }
            catch (Exception ex)
            {
                Report(new PlugInLoadException(file, type.FullName, ex));
            }
        }
    }

    private Assembly LoadAssembly(string file)
    {
        if (_loaded.TryGetValue(file, out Assembly? assembly)) return assembly;

#if NET
        assembly = new PlugInLoadContext(file, SharedAssemblyNames()).LoadFromAssemblyPath(file);
#else
        assembly = Assembly.LoadFrom(file);
#endif
        _loaded[file] = assembly;
        return assembly;
    }

#if NET
    private HashSet<string>? _sharedAssemblyNames;

    /// <summary>
    /// The assemblies a plugin always shares with the host, so that the plugin interfaces are the same types on
    /// both sides: this library, the assemblies that define <typeparamref name="TPlugIn"/> and
    /// <typeparamref name="THost"/>, and the assemblies those reference.
    /// </summary>
    private HashSet<string> SharedAssemblyNames()
    {
        if (_sharedAssemblyNames is not null) return _sharedAssemblyNames;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Assembly contract in new[] { typeof(IPlugInHost).Assembly, typeof(TPlugIn).Assembly, typeof(THost).Assembly })
        {
            if (contract.GetName().Name is string name) names.Add(name);
            foreach (AssemblyName reference in contract.GetReferencedAssemblies())
            {
                if (reference.Name is not null) names.Add(reference.Name);
            }
        }
        return _sharedAssemblyNames = names;
    }
#endif

    private void Report(PlugInLoadException ex)
    {
#pragma warning disable CA1031 // A host whose error handler throws must not stop the scan
        try
        {
            Host.ThrowException(ex);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }
}
