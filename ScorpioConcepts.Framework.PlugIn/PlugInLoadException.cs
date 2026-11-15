using System;

namespace ScorpioConcepts.Framework.PlugIn;

/// <summary>
/// A plugin assembly or plugin type that could not be loaded. <see cref="PlugInManager{TPlugIn, THost}.Find"/>
/// passes one to <see cref="IPlugInHost.ThrowException"/> for each failure and carries on with the other plugins.
/// The original exception is the <see cref="Exception.InnerException"/>.
/// </summary>
public class PlugInLoadException : Exception
{
    /// <summary>The full path of the DLL (or folder) that failed, if known.</summary>
    public string? FilePath { get; }

    /// <summary>The full name of the plugin type that failed, or null when the whole assembly failed.</summary>
    public string? TypeName { get; }

    /// <summary>Creates an exception with a default message.</summary>
    public PlugInLoadException() : base("A plugin could not be loaded.")
    {
    }

    /// <summary>Creates an exception with the given message.</summary>
    public PlugInLoadException(string message) : base(message)
    {
    }

    /// <summary>Creates an exception with the given message and inner exception.</summary>
    public PlugInLoadException(string message, Exception? innerException) : base(message, innerException)
    {
    }

    /// <summary>Creates an exception for a failure in <paramref name="filePath"/>, optionally in one type.</summary>
    public PlugInLoadException(string filePath, string? typeName, Exception? innerException)
        : base(CreateMessage(filePath, typeName, innerException), innerException)
    {
        FilePath = filePath;
        TypeName = typeName;
    }

    private static string CreateMessage(string filePath, string? typeName, Exception? innerException)
    {
        string what = typeName is null ? "Plugin assembly " + filePath : "Plugin " + typeName + " in " + filePath;
        return innerException is null
            ? what + " could not be loaded."
            : what + " could not be loaded: " + innerException.Message;
    }
}
