# .NET NUT Client

## Overview
A client for `upsd`, the network server of the [Network UPS Tools (NUT)](https://networkupstools.org) project.
Use it to list the UPS units a server knows about, read their variables (battery charge, load, status and so on),
change writable variables and run instant commands such as a battery test.

## Simple Example
```csharp
using ScorpioConcepts.Framework.netNUTClient;

using var client = new UPSDClient("nas.local");   // or "nas.local:3493", or new UPSDClient("nas.local", 3493)
client.Connect();

foreach (UPS ups in client.ListUPS())
{
    Console.WriteLine($"{ups.Name}: {ups.Description}");
    Console.WriteLine($"  Status: {client.GetUPSVar(ups.Name, "ups.status")}");
    Console.WriteLine($"  Charge: {client.GetUPSVar(ups.Name, "battery.charge")}%");
}
```

Commands that change something need a user from the server's `upsd.users` file:

```csharp
client.SetUsername("admin");
client.SetPassword(password);
client.InstantCommand("rack", "test.battery.start");
```

The server address can be `"host"`, `"host:port"`, an IPv6 address such as `"::1"`, or `"[::1]:3493"`.

## Encrypting the connection
Without TLS, everything is sent as plain text, including the password. If the server has a certificate
configured, call `StartTls()` straight after `Connect()` and before `SetPassword()`:

```csharp
client.Connect();
client.StartTls();                // the certificate must be trusted and match the host name
client.SetUsername("admin");
client.SetPassword(password);
```

For a self-signed certificate, check it yourself with `CertificateValidationCallback`, for example by comparing
its thumbprint. A server without TLS answers with a `UPSException` (`FEATURE-NOT-CONFIGURED`), and the connection
stays usable.

## Async and threads
Every command has an async version that takes a `CancellationToken`, such as `ConnectAsync`, `ListUPSAsync` and
`GetUPSVarAsync`. One client can be shared between threads: commands are sent one at a time, so each caller gets
its own reply.

## Values are escaped
UPS, variable and command names must be single words. A name with a space, a quote, a backslash or a line break
throws an `ArgumentException` before anything is sent. Values such as the new value in `SetUPSVariable` can hold
any text except line breaks: quotes and backslashes are escaped, so a value can never add a command of its own.

## Errors and timeouts
- An error reply from the server, such as `ERR ACCESS-DENIED`, is thrown as a `UPSException`. Its `Code`
  says which error it was, and the connection stays usable.
- A reply that takes longer than `ReplyTimeout` (10 seconds by default) throws a `TimeoutException` and closes
  the connection, because a late reply would otherwise be read as the answer to the next command.
  Call `Connect()` again to carry on. Canceling an async command closes the connection for the same reason.
- `Connect()` gives up with a `TimeoutException` after `ConnectTimeout` (10 seconds by default).
- If the server closes the connection, `Connected` becomes false.

## Logging in to a UPS
`Login(upsName)` tells the server that this machine is powered by that UPS, which matters for coordinated
shutdowns. `Disconnect()` (or disposing the client) logs out again before closing the connection.
