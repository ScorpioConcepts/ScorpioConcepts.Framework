# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

A collection of independent, small .NET class libraries, each shipped as its own NuGet package. The projects do not reference each other. Each project folder has its own README.md with usage examples; the root README.md summarizes all four.

## Build and packaging

All four projects are SDK-style and multi-target `net10.0;netstandard2.0` (.NET Framework is not supported). The solution uses the XML `.slnx` format, which needs the .NET 10 SDK or Visual Studio 2022 17.13+.

```
dotnet build ScorpioConcepts.Framework.slnx
dotnet build ScorpioConcepts.Framework.LogServer
dotnet pack  ScorpioConcepts.Framework.slnx -c Release
```

Package metadata lives in each csproj (there are no `.nuspec` or `AssemblyInfo.cs` files). When releasing, bump both `<Version>` and `<PackageVersion>` and update `<PackageReleaseNotes>`. Every change must still compile for `netstandard2.0`, so avoid APIs that only exist in newer .NET, and C# features that need runtime support netstandard2.0 lacks.

DBSchema uses `Microsoft.Data.SqlClient`, with a different version per target: 7.x for `net10.0` and 5.2.x for `netstandard2.0`, because 6.0 and later no longer ship a .NET Standard build. Keep the conditional `PackageReference` groups in its csproj when updating them.

## Tests

Tests live in `tests/`, one project per library, using xUnit v3 with Shouldly for assertions and NSubstitute for mocks. Package versions are managed centrally in `tests/Directory.Packages.props`, and shared settings live in `tests/Directory.Build.props`. `global.json` opts `dotnet test` into Microsoft.Testing.Platform, so pass the project with `--project` and select tests with the xUnit filter options:

```
dotnet test ScorpioConcepts.Framework.slnx
dotnet test --project tests/ScorpioConcepts.Framework.LogServer.Tests
dotnet test --project tests/ScorpioConcepts.Framework.LogServer.Tests --filter-method "*New_client_receives_greeting_then_history"
```

- **DBSchema.IntegrationTests** start SQL Server in Docker through Testcontainers, using one container per run and a fresh database per test. They run serially (see `xunit.runner.json`) because `DBSchema` holds its connection statically. If Docker isn't running, they are skipped instead of failing.
- **DBSchema.Tests** reach the JSON, XML and DBML readers and writers through `InternalsVisibleTo`, so no database is needed. They also run serially, because `DBSchema.DebugLog` is a shared static list.
- **PlugIn.Tests** compile throwaway plugin DLLs with Roslyn (`PlugInCompiler`). Each one gets a unique assembly name, because the default load context can only hold one assembly per name.
- **LogServer.Tests** and **netNUTClient.Tests** use real sockets on loopback. netNUTClient talks to a scripted `FakeUpsdServer`. Every `UPSDClient` call goes through `RunWithTimeout`, so a regression in `ReplyTimeout` handling fails the test instead of hanging the run.
- Every test is expected to run and pass. Don't merge a test that is skipped to work around a bug; fix the bug in the same change.

## Architecture

- **LogServer**: `LogServer` runs a `TcpListener` (default port 446) on its own thread. It keeps a bounded queue of recent lines (`logSize`) that new telnet clients get first, then it streams new lines to every connected `LogServer_Client`. Shared state is guarded by a `Mutex`. Errors surface through the `onServerException` event, and connect/disconnect through events too. Callers must `Stop()` and then `Join()`.
- **PlugIn**: generic `PlugInManager<TPlugIn, THost>` where `TPlugIn : IPlugIn<THost>` and `THost : IPlugInHost`. `Find(dir)` loads every top-level `*.dll` with `Assembly.LoadFrom`, makes an instance of each non-abstract type that implements the plugin interface (via `Activator.CreateInstance`, so it needs a parameterless constructor), and calls `Initialize(host)`. Exceptions go to `host.ThrowException` instead of being thrown.
- **netNUTClient**: `UPSDClient` is a synchronous TCP client for the NUT `upsd` text protocol (LIST/GET/SET/INSTCMD/LOGIN and so on). `UPSException.ErrorCode` maps upsd `ERR` responses. `UPS` parses `LIST UPS` lines.
- **DBSchema**: a static API on `DBSchema` (`InitializeDB(SqlConnection)`, `GenerateSchema`, `CheckDatabaseSchema`, `ConvertSchema`). It reads the SQL Server schema into `DBTable` objects, compares them with a schema file, and changes the database structure to match. Schema files are JSON by default (`DBSchemaJson`); the original XML format (`DBSchemaXml`) is still supported, and DBML (`DBSchemaDbml`, a hand-written `DbmlLexer` and `DbmlReader` covering the subset of DBML that `DBTable` can hold) is supported too. `CheckDatabaseSchema` detects which one a file uses from how it starts, after any comments: `{`, `<`, or a DBML keyword. `GenerateSchemaXML` is kept for existing XML users. Default rows (`DBDefaultRecords`) can be included too. It handles structure only; it is not an ORM. Activity is recorded in `DBSchema.DebugLog`.
