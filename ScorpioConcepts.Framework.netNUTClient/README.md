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

## Errors and timeouts
- An error reply from the server, such as `ERR ACCESS-DENIED`, is thrown as a `UPSException`. Its `Code`
  says which error it was, and the connection stays usable.
- A reply that takes longer than `ReplyTimeout` (10 seconds by default) throws a `TimeoutException` and closes
  the connection, because a late reply would otherwise be read as the answer to the next command.
  Call `Connect()` again to carry on.
- If the server closes the connection, `Connected` becomes false.

## Logging in to a UPS
`Login(upsName)` tells the server that this machine is powered by that UPS, which matters for coordinated
shutdowns. `Disconnect()` (or disposing the client) logs out again before closing the connection.
