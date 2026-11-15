# Log Server Library

## Overview
A small TCP log server for watching what an application is doing while it runs. Connect with any telnet client
and you see the most recent lines, then every new line as it is logged. It is handy for debugging things that have
no console, such as Windows services.

> [!WARNING]
> **Never use the default settings in production.** By default the server listens on every network interface
> (`IPAddress.Any`) and has no authentication or encryption, so anyone who can reach the machine can read
> everything you log, and logs often contain connection strings, tokens or personal data.
> In production, listen on `IPAddress.Loopback` so only the same machine can connect, or on an internal address
> behind a firewall, and be careful what you log.

## Simple Example
```csharp
using System.Net;
using ScorpioConcepts.Framework.LogServer;

// Only this machine can connect. Port 7446 is the default.
using var server = new LogServer(IPAddress.Loopback, LogServer.DefaultPort, "Log Server Example", 100);
server.ClientConnected += (_, e) => Console.WriteLine("Log viewer connected: " + e.Address);
server.ClientDisconnected += (_, e) => Console.WriteLine("Log viewer disconnected: " + e.Address);
server.ServerError += (_, e) => Console.WriteLine("Log server error: " + e.Exception.Message);

server.Start();
server.Log("Simple Test Log");
server.Log("Retains a history of logged lines for later reading");

Console.WriteLine("Connect with: telnet 127.0.0.1 7446");
Console.WriteLine("Press the enter key to exit...");
Console.ReadLine();
```

Create a `LogServer`, call `Start()`, and call `Log(...)` for each line. Disposing the server, or calling `Stop()`
and then `Join()`, disconnects every client and closes the port.

- `Start()` returns once the port is open, so clients can connect straight away.
- If the port can't be opened, for example because it is in use, `ServerError` is raised before `Start()` returns
  and `Active` stays false.
- Lines are sent as UTF-8, one per line.
- New clients first get the greeting, then up to `logSize` recent lines (also available from `LogHistory`).

## Choosing the address and port
| Constructor | Listens on |
| --- | --- |
| `new LogServer(greeting, logSize)` | every interface, port 7446 |
| `new LogServer(port, greeting, logSize)` | every interface, the given port |
| `new LogServer(address, port, greeting, logSize)` | the given address and port |

The default port, 7446, is unassigned in the IANA registry and above 1023, so it needs no root on Linux. Pass port
`0` to use any free port, then read the one chosen from `LocalEndpoint`.

## Limits
| Property | Default | What it does |
| --- | --- | --- |
| `MaxClients` | 32 | More connections than this are told the server is full and closed. |
| `MaxQueuedLines` | 10,000 | Lines waiting for one client. A client that isn't keeping up loses its oldest lines and gets a line saying how many it missed, so it can't make the server's memory grow without bound. |
