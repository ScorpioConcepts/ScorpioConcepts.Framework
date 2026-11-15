# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

A collection of independent, small .NET class libraries, each shipped as its own NuGet package. The projects do not reference each other. Each project folder has its own README.md with usage examples; the root README.md summarizes all four.

## Build and packaging

There is no test project in this repository.

The SDK-style projects (LogServer, PlugIn, netNUTClient) multi-target `net452;netstandard2.0;net5.0`:

```
dotnet build ScorpioConcepts.Framework.LogServer
dotnet pack  ScorpioConcepts.Framework.LogServer -c Release
```

`ScorpioConcepts.Framework.DBSchema` has **not** been migrated: it is still a legacy (non-SDK) csproj targeting .NET Framework 3.5 with `Properties/AssemblyInfo.cs`. It needs Visual Studio / full MSBuild, not `dotnet build`, so building the whole `.sln` with the dotnet CLI may fail on it.

Package metadata lives in each SDK-style csproj (the old `.nuspec` and `AssemblyInfo.cs` files were removed during the .NET 5 migration). When releasing, bump both `<Version>` and `<PackageVersion>` and update `<PackageReleaseNotes>`. Any change must still compile for `net452` and `netstandard2.0`, so avoid newer-only APIs and C# features those targets can't use.

## Architecture

- **LogServer**: `LogServer` runs a `TcpListener` (default port 446) on its own thread. It keeps a bounded queue of recent lines (`logSize`) that new telnet clients get first, then it streams new lines to every connected `LogServer_Client`. Shared state is guarded by a `Mutex`. Errors surface through the `onServerException` event, and connect/disconnect through events too. Callers must `Stop()` and then `Join()`.
- **PlugIn**: generic `PlugInManager<TPlugIn, THost>` where `TPlugIn : IPlugIn<THost>` and `THost : IPlugInHost`. `Find(dir)` loads every top-level `*.dll` with `Assembly.LoadFrom`, makes an instance of each non-abstract type that implements the plugin interface (via `Activator.CreateInstance`, so it needs a parameterless constructor), and calls `Initialize(host)`. Exceptions go to `host.ThrowException` instead of being thrown.
- **netNUTClient**: `UPSDClient` is a synchronous TCP client for the NUT `upsd` text protocol (LIST/GET/SET/INSTCMD/LOGIN and so on). `UPSException.ErrorCode` maps upsd `ERR` responses. `UPS` parses `LIST UPS` lines.
- **DBSchema**: a static API on `DBSchema` (`InitializeDB(SqlConnection)`, `GenerateSchemaXML`, `CheckDatabaseSchema`). It reads the SQL Server schema into `DBTable` objects, compares them with an XML schema file, and changes the database structure to match. Default rows (`DBDefaultRecords`) can be included too. It handles structure only; it is not an ORM. Activity is recorded in `DBSchema.DebugLog`.
