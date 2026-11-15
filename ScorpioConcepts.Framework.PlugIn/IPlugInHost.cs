using System;

namespace ScorpioConcepts.Framework.PlugIn;

/// <summary>
/// The base interface for an application that hosts plugins. Extend it to give plugins access to more of the
/// application.
/// </summary>
public interface IPlugInHost
{
    /// <summary>Reports an error message from a plugin to the host.</summary>
    /// <param name="message">The error message.</param>
    void PlugInError(string message);

    /// <summary>
    /// Reports an exception to the host. <see cref="PlugInManager{TPlugIn, THost}.Find"/> uses it for every plugin
    /// that fails to load, passing a <see cref="PlugInLoadException"/>, and plugins can use it for their own errors.
    /// </summary>
    /// <param name="ex">The exception.</param>
    void ThrowException(Exception ex);
}
