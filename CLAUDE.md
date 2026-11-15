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
- A library with `<Nullable>enable</Nullable>` has nullable-annotated public APIs. netNUTClient has it now; the others get it in their own PRs.

DBSchema uses `Microsoft.Data.SqlClient`, with a different version per target: 7.x for `net10.0` and 5.2.x for `netstandard2.0`, because 6.0 and later no longer ship a .NET Standard build. Keep the conditional `PackageReference` groups in its csproj when updating them.

## Tests

Tests live in `tests/`, one project per library, using xUnit v3 with Shouldly for assertions and NSubstitute for mocks. Package versions are managed centrally in `tests/Directory.Packages.props`, and shared settings live in `tests/Directory.Build.props`. `global.json` opts `dotnet test` into Microsoft.Testing.Platform, so pass the project with `--project` and select tests with the xUnit filter options:

```
dotnet test ScorpioConcepts.Framework.slnx
dotnet test --project tests/ScorpioConcepts.Framework.LogServer.Tests
dotnet test --project tests/ScorpioConcepts.Framework.LogServer.Tests --filter-method "*New_client_receives_greeting_then_history"
```

- **DBSchema.IntegrationTests** start SQL Server in Docker through Testcontainers, using one container per run and a fresh database per test. They run serially (see `xunit.runner.json`) because `DBSchema` holds its connection statically. If Docker isn't running, they are skipped instead of failing.
- **DBSchema.Tests** reach the JSON and XML readers and writers through `InternalsVisibleTo`, so no database is needed. They also run serially, because `DBSchema.DebugLog` is a shared static list.
- **PlugIn.Tests** compile throwaway plugin DLLs with Roslyn (`PlugInCompiler`). Each one gets a unique assembly name, because the default load context can only hold one assembly per name.
- **LogServer.Tests** and **netNUTClient.Tests** use real sockets on loopback. netNUTClient talks to a scripted `FakeUpsdServer`. Every `UPSDClient` call goes through `RunWithTimeout`, so a regression in `ReplyTimeout` handling fails the test instead of hanging the run.
- Every test is expected to run and pass. Don't merge a test that is skipped to work around a bug; fix the bug in the same change.

## CI and publishing

- `.github/workflows/ci.yml` runs on every PR and push to `master`: build, all tests (TRX results uploaded), and pack (packages uploaded as an artifact). Its `Build and test` job is the required check on `master`. It runs on Ubuntu, because the DBSchema integration tests need Docker, and sets `DBSCHEMA_REQUIRE_DOCKER=true` so they fail rather than skip if Docker is missing.
- `.github/workflows/publish.yml` runs when a GitHub Release is published. It builds, tests and packs every library, then pushes to NuGet.org with `--skip-duplicate`, so only libraries whose `<Version>` was bumped get published. To release a library, bump `<Version>` in its csproj, merge, then publish a release.
- Actions are pinned to commit SHAs. Dependabot (`.github/dependabot.yml`) keeps them and the NuGet packages up to date. It skips major versions of Microsoft.Data.SqlClient, because DBSchema deliberately stays on two versions.
- Publishing uses NuGet.org trusted publishing, so there is no API key secret. `NuGet/login` exchanges the job's GitHub OIDC token for a short-lived key. The NuGet.org policy is tied to the workflow file name `publish.yml` and the `nuget` environment, so renaming either breaks publishing until the policy is updated.
- Tests must pass on Linux as well as Windows: privileged ports such as LogServer's default 446 can't be bound without root.

## Architecture

- **LogServer**: `LogServer` runs a `TcpListener` (default port 446) on its own thread. It keeps a bounded queue of recent lines (`logSize`) that new telnet clients get first, then it streams new lines to every connected `LogServer_Client`. Shared state is guarded by a `Mutex`. Errors surface through the `onServerException` event, and connect/disconnect through events too. Callers must `Stop()` and then `Join()`.
- **PlugIn**: generic `PlugInManager<TPlugIn, THost>` where `TPlugIn : IPlugIn<THost>` and `THost : IPlugInHost`. `Find(dir)` loads every top-level `*.dll` with `Assembly.LoadFrom`, makes an instance of each non-abstract type that implements the plugin interface (via `Activator.CreateInstance`, so it needs a parameterless constructor), and calls `Initialize(host)`. Exceptions go to `host.ThrowException` instead of being thrown.
- **netNUTClient**: `UPSDClient` is a client for the NUT `upsd` text protocol (LIST, GET, SET, INSTCMD, LOGIN, STARTTLS and so on).
  - **Sync and async:** every command is async at its core (`SendLockedAsync`), and the synchronous methods call the async ones.
  - **One command at a time:** a `SemaphoreSlim` sends one command at a time, so the client is safe to share between threads.
  - **Dropped connections:** a timeout, a cancellation, the server closing the connection, or an I/O error drops the connection (`Connected` becomes false), so a late reply can never be read as the next command's reply. An `ERR` reply keeps the connection.
  - **`NutProtocol`** holds the protocol rules: `Word` checks names, `Quote` escapes values, `Tokenize` splits reply lines, and `ParseServer` handles host, port and IPv6. Every argument must go through `Word` or `Quote`; that is what stops values from injecting commands.
  - **Errors:** `UPSException.ErrorCode` maps upsd `ERR` replies, and keeps the protocol's names with `-` written as `_`.
- **DBSchema**: a static API on `DBSchema` (`InitializeDB(SqlConnection)`, `GenerateSchema`, `CheckDatabaseSchema`, `ConvertSchema`). It reads the SQL Server schema into `DBTable` objects, compares them with a schema file, and changes the database structure to match. Schema files are JSON by default (`DBSchemaJson`); the original XML format (`DBSchemaXml`) is still supported, and `CheckDatabaseSchema` detects which one a file uses from its first character. `GenerateSchemaXML` is kept for existing XML users. Default rows (`DBDefaultRecords`) can be included too. It handles structure only; it is not an ORM. Activity is recorded in `DBSchema.DebugLog`.
