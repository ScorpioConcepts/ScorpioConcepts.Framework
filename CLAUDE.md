# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

A collection of independent, small .NET class libraries, each shipped as its own NuGet package. The projects do not reference each other. Each project folder has its own README.md with usage examples; the root README.md summarizes all four.

## Build and packaging

There is no test project in this repository.

All four projects are SDK-style and multi-target `net10.0;netstandard2.0` (.NET Framework is not supported). The solution uses the XML `.slnx` format, which needs the .NET 10 SDK or Visual Studio 2022 17.13+.

```
dotnet build ScorpioConcepts.Framework.slnx
dotnet build ScorpioConcepts.Framework.LogServer
dotnet pack  ScorpioConcepts.Framework.slnx -c Release
```

Package metadata lives in each csproj (there are no `.nuspec` or `AssemblyInfo.cs` files). When releasing, bump both `<Version>` and `<PackageVersion>` and update `<PackageReleaseNotes>`. Every change must still compile for `netstandard2.0`, so avoid APIs that only exist in newer .NET, and C# features that need runtime support netstandard2.0 lacks.

DBSchema uses `Microsoft.Data.SqlClient`, with a different version per target: 7.x for `net10.0` and 5.2.x for `netstandard2.0`, because 6.0 and later no longer ship a .NET Standard build. Keep the conditional `PackageReference` groups in its csproj when updating them.

## Architecture

- **LogServer**: `LogServer` runs a `TcpListener` (default port 446) on its own thread. It keeps a bounded queue of recent lines (`logSize`) that new telnet clients get first, then it streams new lines to every connected `LogServer_Client`. Shared state is guarded by a `Mutex`. Errors surface through the `onServerException` event, and connect/disconnect through events too. Callers must `Stop()` and then `Join()`.
- **PlugIn**: generic `PlugInManager<TPlugIn, THost>` where `TPlugIn : IPlugIn<THost>` and `THost : IPlugInHost`. `Find(dir)` loads every top-level `*.dll` with `Assembly.LoadFrom`, makes an instance of each non-abstract type that implements the plugin interface (via `Activator.CreateInstance`, so it needs a parameterless constructor), and calls `Initialize(host)`. Exceptions go to `host.ThrowException` instead of being thrown.
- **netNUTClient**: `UPSDClient` is a synchronous TCP client for the NUT `upsd` text protocol (LIST/GET/SET/INSTCMD/LOGIN and so on). `UPSException.ErrorCode` maps upsd `ERR` responses. `UPS` parses `LIST UPS` lines.
- **DBSchema**: a static API on `DBSchema` (`InitializeDB(SqlConnection)`, `GenerateSchemaXML`, `CheckDatabaseSchema`). It reads the SQL Server schema into `DBTable` objects, compares them with an XML schema file, and changes the database structure to match. Default rows (`DBDefaultRecords`) can be included too. It handles structure only; it is not an ORM. Activity is recorded in `DBSchema.DebugLog`.
