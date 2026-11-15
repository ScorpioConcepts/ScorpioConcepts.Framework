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

Shared settings for the four libraries live in the root `Directory.Build.props`:
- target frameworks and package metadata
- symbol packages (`.snupkg`) and package validation
- the analyzer level
- each folder's `README.md` as the package README
- in CI only, deterministic builds and warnings as errors

Each library csproj holds only `PackageId`, `Version`, `Title`, `Description`, `PackageReleaseNotes` and its dependencies. When releasing, bump `<Version>` and update `<PackageReleaseNotes>`. A library csproj that still sets `<AnalysisLevel>latest</AnalysisLevel>` hasn't had its analyzer warnings fixed yet; remove that line when you clean it up.

Every change must still compile for `netstandard2.0`.
- `LangVersion` is `latest` everywhere, so modern syntax works on both targets. A feature that needs runtime support fails to compile for `netstandard2.0`.
- APIs that only exist in newer .NET need `#if NET`.
- The analyzer rules that only suggest such APIs (CA1510, CA1835, CA1865 and so on) are turned off in `Directory.Build.props`.
- A library with `<Nullable>enable</Nullable>` has nullable-annotated public APIs. All four libraries have it.

DBSchema uses `Microsoft.Data.SqlClient`, with a different version per target: 7.x for `net10.0` and 5.2.x for `netstandard2.0`, because 6.0 and later no longer ship a .NET Standard build. Keep the conditional `PackageReference` groups in its csproj when updating them.

## Tests

Tests live in `tests/`, one project per library, using xUnit v3 with Shouldly for assertions and NSubstitute for mocks. Package versions are managed centrally in `tests/Directory.Packages.props`, and shared settings live in `tests/Directory.Build.props`. `global.json` opts `dotnet test` into Microsoft.Testing.Platform, so pass the project with `--project` and select tests with the xUnit filter options:

```
dotnet test ScorpioConcepts.Framework.slnx
dotnet test --project tests/ScorpioConcepts.Framework.LogServer.Tests
dotnet test --project tests/ScorpioConcepts.Framework.LogServer.Tests --filter-method "*New_client_receives_greeting_then_history"
```

- **DBSchema.IntegrationTests** start SQL Server in Docker through Testcontainers, using one container per run and a fresh database per test, so test classes run in parallel. Each test gets a `DBSchemaManager` for its database and a `TestLogger` that keeps what it logged. If Docker isn't running, they are skipped instead of failing.
- **DBSchema.Tests** reach the JSON, XML and DBML readers and writers, and the internal model constructors, through `InternalsVisibleTo`, so no database is needed.
- **PlugIn.Tests** compile throwaway plugin DLLs with Roslyn (`PlugInCompiler`). Each one gets a unique assembly name unless a test passes one, such as two folders that each ship a `PlugInHelper.dll` to check that plugins get their own copy.
- **LogServer.Tests** and **netNUTClient.Tests** use real sockets on loopback. netNUTClient talks to a scripted `FakeUpsdServer`. Every `UPSDClient` call goes through `RunWithTimeout`, so a regression in `ReplyTimeout` handling fails the test instead of hanging the run.
- Every test is expected to run and pass. Don't merge a test that is skipped to work around a bug; fix the bug in the same change.

## CI and publishing

- `.github/workflows/ci.yml` runs on every PR and push to `master`: build, all tests (TRX results uploaded), and pack (packages uploaded as an artifact). Its `Build and test` job is the required check on `master`. It runs on Ubuntu, because the DBSchema integration tests need Docker, and sets `DBSCHEMA_REQUIRE_DOCKER=true` so they fail rather than skip if Docker is missing.
- `.github/workflows/publish.yml` runs when a GitHub Release is published. It builds, tests and packs every library, then pushes to NuGet.org with `--skip-duplicate`, so only libraries whose `<Version>` was bumped get published. To release a library, bump `<Version>` in its csproj, merge, then publish a release.
- Actions are pinned to commit SHAs. Dependabot (`.github/dependabot.yml`) keeps them and the NuGet packages up to date. It skips major versions of Microsoft.Data.SqlClient, because DBSchema deliberately stays on two versions.
- Publishing uses NuGet.org trusted publishing, so there is no API key secret. `NuGet/login` exchanges the job's GitHub OIDC token for a short-lived key. The NuGet.org policy is tied to the workflow file name `publish.yml` and the `nuget` environment, so renaming either breaks publishing until the policy is updated.
- Tests must pass on Linux as well as Windows. For example, ports below 1024 can't be bound without root, and the CI runner isn't root. Socket tests should listen on loopback with port 0 and read the real port back (as LogServer's `LocalEndpoint` and the fake upsd server do), rather than picking a fixed port.

## Architecture

- **LogServer**: `LogServer` (sealed, `IDisposable`) opens its `TcpListener` in `Start()` and runs an async accept loop. The default is `IPAddress.Any` on port 7446; that default is deliberate and documented as not for production.
  - **History:** it keeps the last `logSize` lines, which a new client gets after the greeting.
  - **Clients:** each `LogServerClient` has an async writer that waits for queued lines (no polling) and an async reader that only detects the disconnect.
  - **One lock:** `logLock` covers both `Log()` and adding a client with its history, so a client gets every line exactly once.
  - **Limits:** `MaxClients` refuses extra connections. `MaxQueuedLines` drops a slow client's oldest lines and tells it how many it missed.
  - **Events and shutdown:** errors are raised through `ServerError`, and connects and disconnects through `ClientConnected` and `ClientDisconnected`, all `EventHandler<T>`. Shut down with `Dispose()`, or `Stop()` then `Join()`.
- **PlugIn**: generic `PlugInManager<TPlugIn, THost>` where `TPlugIn : IPlugIn<THost>` and `THost : IPlugInHost`. `Find(dir)` loads every top-level `*.dll`, makes an instance of each public, non-abstract type that is a `TPlugIn` (via `Activator.CreateInstance`, so it needs a parameterless constructor), calls `Initialize(host)`, and swaps in a new read-only `PlugIns` list.
  - **Loading:** on .NET 10 each DLL gets its own `PlugInLoadContext` (`#if NET`), which loads the plugin's dependencies from its folder but always shares the contract assemblies (this library, the assemblies of `TPlugIn` and `THost`, and their references) so the interface types match. netstandard2.0 uses `Assembly.LoadFrom`. A manager loads each DLL once and reuses it on the next `Find`.
  - **Errors:** nothing is thrown. Each failing DLL, missing dependency (`ReflectionTypeLoadException`, using the types that did load) or failing type goes to `host.ThrowException` as a `PlugInLoadException` with `FilePath`, `TypeName` and the original exception inside, and the scan carries on.
- **netNUTClient**: `UPSDClient` is a client for the NUT `upsd` text protocol (LIST, GET, SET, INSTCMD, LOGIN, STARTTLS and so on).
  - **Sync and async:** every command is async at its core (`SendLockedAsync`), and the synchronous methods call the async ones.
  - **One command at a time:** a `SemaphoreSlim` sends one command at a time, so the client is safe to share between threads.
  - **Dropped connections:** a timeout, a cancellation, the server closing the connection, or an I/O error drops the connection (`Connected` becomes false), so a late reply can never be read as the next command's reply. An `ERR` reply keeps the connection.
  - **`NutProtocol`** holds the protocol rules: `Word` checks names, `Quote` escapes values, `Tokenize` splits reply lines, and `ParseServer` handles host, port and IPv6. Every argument must go through `Word` or `Quote`; that is what stops values from injecting commands.
  - **Errors:** `UPSException.ErrorCode` maps upsd `ERR` replies, and keeps the protocol's names with `-` written as `_`.
- **DBSchema**: `DBSchemaManager` takes a `SqlConnection` (opened per call if it is closed, never disposed) and an optional `ILogger`. Its log messages use `[LoggerMessage]` source generation.
  - **Model:** `DBSchemaDefinition` holds the tables (`DBTable` with `DBColumn`, `DBPrimaryKey`, `DBIndex`/`DBIndexColumn`, `DBForeignKey`, `DBDefaultRecord`). Collections are exposed through real read-only wrappers, and only the library adds to them (`internal` constructors and `Add*` methods).
  - **Files:** `DBSchemaDefinition.Load` reads JSON (`DBSchemaJson`, the default), the original XML (`DBSchemaXml`) or DBML (`DBSchemaDbml`), and `Save` writes any of them, including each table's default records. The format is detected from how the file starts, after any comments: `{`, `<`, or a DBML keyword.
  - **DBML:** a hand-written `DbmlLexer` and `DbmlReader` cover the subset of DBML that the model can hold, and reject anything else that would change the structure. DBML has no syntax for some model details, so they use custom properties that other DBML tools accept: `clustered_index` on a table (an index name, or `false` for none), and `identity_seed`/`identity_increment`, `default_name` and `persisted: true` on a column. Output was checked against the real `@dbml/core` parser, which rejects unknown index settings and custom settings without a string, number, colour or boolean value (so no bare flags and no `null`).
  - **Reading:** `ReadDatabaseSchema` reads the whole `dbo` schema with one query per catalog view (tables, columns, indexes including the primary key, foreign keys), not several per table.
  - **Checking:** `CheckDatabaseSchema` compares the database with the file inside one transaction (`_transaction`, set on every command), and adds missing tables, then columns and indexes, then foreign keys. A failure rolls everything back. It handles structure only; it is not an ORM.
  - **SQL safety:** every identifier goes through `SqlName.Quote` (doubling `]`) and tables through `SqlName.Table` (always `[dbo].`), and default record values are positional parameters (`@p0`). A column's `DataType` and `Options` are raw SQL on purpose, which is why the README says a schema file is code.
  - **Names** compare with `SqlName.Comparer` (ordinal, ignoring case), like SQL Server's default collations.
  - **Model details:** a computed column's `DataType` is its `AS (...)` expression, an identity seed shows as `identity(100,5)`, a user-named default is kept as `CONSTRAINT [name] DEFAULT ...` in `Options`, `DBPrimaryKey.Clustered` defaults to true, and `DBForeignKey` has matching `Columns` and `ReferencedColumns` lists. Single-column keys keep the original JSON and XML shape, so existing files don't change.
