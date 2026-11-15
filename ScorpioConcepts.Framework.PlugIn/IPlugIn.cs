using System;

namespace ScorpioConcepts.Framework.PlugIn;

/// <summary>
/// The base interface for plugins. An application usually defines its own plugin interface that extends this one.
/// </summary>
/// <typeparam name="THost">The type of the host that the plugin is given when it is initialized.</typeparam>
public interface IPlugIn<THost> where THost : IPlugInHost
{
    /// <summary>The name of the plugin.</summary>
    string Name { get; }

    /// <summary>The author of the plugin.</summary>
    string Author { get; }

    /// <summary>A description of what the plugin does.</summary>
    string Description { get; }

    /// <summary>The version of the plugin.</summary>
    Version Version { get; }

    /// <summary>The host that was passed to <see cref="Initialize"/>.</summary>
    THost Host { get; }

    /// <summary>
    /// Prepares the plugin for use. <see cref="PlugInManager{TPlugIn, THost}.Find"/> calls it once, straight after
    /// creating the plugin.
    /// </summary>
    /// <param name="host">The host for this plugin.</param>
    void Initialize(THost host);
}
