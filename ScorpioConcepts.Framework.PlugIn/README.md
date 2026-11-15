# Simple Plug In Library

## Overview
A small library for adding plugins to a .NET application. The application defines what a plugin looks like
with an interface, and `PlugInManager` finds every class in a folder of DLLs that implements it, creates it and
hands it the host application.

> [!WARNING]
> **Every DLL in the plugin folder is loaded, and loading a DLL runs code from it.** Anyone who can put a file
> in that folder can run code inside your application, with all of its permissions. Protect the folder as
> carefully as the application's own install folder: don't point `Find()` at a folder that ordinary users,
> uploads or downloads can write to.

## Interfaces
### IPlugInHost
`IPlugInHost` is what the host application implements. Its two methods let plugins report problems without
knowing how the application handles errors:
- `PlugInError(string message)` reports an error message.
- `ThrowException(Exception ex)` reports an exception. `PlugInManager` also uses it for every plugin that fails
  to load (see [Errors](#errors)).

Extend it with your own interface to give plugins access to more of the application.

### IPlugIn&lt;THost&gt;
`IPlugIn<THost>` is the base interface for plugins, where `THost` is the host type the plugins get. It has
`Name`, `Author`, `Description` and `Version` properties, a `Host` property, and `Initialize(THost host)`, which
the manager calls once straight after creating the plugin. Define your own plugin interface on top of it with
the methods your application calls.

### PlugInManager&lt;TPlugIn, THost&gt;
`PlugInManager<TPlugIn, THost>` does the finding. Use it as it is, or subclass it to add helpers such as calling a
method on every plugin. `Find(directory)` scans the `*.dll` files in that folder (not its subfolders). For each
public, non-abstract class that is a `TPlugIn` and has a public parameterless constructor, it creates an instance,
calls `Initialize(host)` and adds it to `PlugIns`.

`PlugIns` is a read-only list. Each call to `Find()` replaces it with a new list when it finishes, so a list you
read earlier never changes underneath you.

## Errors
A DLL or a plugin that fails to load doesn't stop the others. `Find()` passes each failure to
`host.ThrowException` as a `PlugInLoadException`, and carries on:
- `FilePath` is the DLL that failed.
- `TypeName` is the plugin class that failed, or null when the whole DLL failed (for example, it isn't a .NET
  assembly).
- `InnerException` is the original error, such as the exception the plugin's constructor or `Initialize()`
  threw.

If some classes in a DLL need a dependency that is missing, the classes that don't need it still load, and the
missing dependency is reported.

## Dependencies and load contexts
On .NET 10, each plugin DLL is loaded into its own `AssemblyLoadContext`. A plugin's dependencies load from the
plugin's folder (using its `.deps.json` if it has one), so two plugins can ship different versions of the same
library, and neither has to match the host's version.

The assemblies that define the plugin contract are always shared with the host, even if a plugin ships a copy:
this library, the assemblies that define `TPlugIn` and `THost`, and the assemblies those reference. Otherwise
the plugin's `IPlugIn` would be a different type from the host's. Anything a plugin doesn't ship, including the
.NET framework itself, comes from the host.

A DLL is loaded once per manager. Calling `Find()` again creates new plugin instances, and picks up DLLs added
since the last call, but a DLL that has changed is only seen after the application restarts.

On .NET Standard 2.0 (for example .NET Framework hosts), plugins are loaded with `Assembly.LoadFrom` into the
application's own context, so they share its dependencies.

## Simple Example
### ISamplePlugIn.cs
```csharp
using ScorpioConcepts.Framework.PlugIn;

namespace PlugInHost;

public interface ISamplePlugIn : IPlugIn<IPlugInHost>
{
    void SayHello();
}
```
The plugin contract: plugins are hosted by an `IPlugInHost`, and each one has a `SayHello()` method. Put it in an
assembly that both the application and the plugins reference.

### Program.cs
```csharp
using PlugInHost;
using ScorpioConcepts.Framework.PlugIn;

var host = new ConsoleHost();
var manager = new PlugInManager<ISamplePlugIn, IPlugInHost>(host);

string folder = Path.Combine(AppContext.BaseDirectory, "plugins");
manager.Find(folder);
Console.WriteLine($"Found {manager.PlugIns.Count} plugins in {folder}");

foreach (ISamplePlugIn plugin in manager.PlugIns)
{
    plugin.SayHello();
}

class ConsoleHost : IPlugInHost
{
    public void PlugInError(string message) => Console.WriteLine("Plugin error: " + message);

    public void ThrowException(Exception ex) => Console.WriteLine("Plugin exception: " + ex);
}
```
The application finds the plugins in a `plugins` folder next to it and asks each one to say hello.

### SamplePlugIn1.cs
```csharp
using PlugInHost;
using ScorpioConcepts.Framework.PlugIn;

namespace SamplePlugIn1;

public class SamplePlugIn1 : ISamplePlugIn
{
    public string Name => "SamplePlugIn1";
    public string Author => "Scorpio Concepts";
    public string Description => "A sample plugin for ScorpioConcepts.Framework.PlugIn";
    public Version Version => new(1, 0);
    public IPlugInHost Host { get; private set; } = null!;

    public void Initialize(IPlugInHost host) => Host = host;

    public void SayHello() => Console.WriteLine("Sample Plug In 1 saying 'Hello!'");
}
```
A plugin is a class library that references the contract. `Initialize()` is where it gets ready to be used; this
one just keeps the host. Build it and copy its DLL into the `plugins` folder.
