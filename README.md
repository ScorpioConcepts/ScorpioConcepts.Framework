# Scorpio Concepts Framework Libraries

This is an open source collection of libraries I have built for use in my .NET applications.
Feel free to use them, or to modify them. If you make a contribution and think it's great, feel free to send me a pull request and I'll merge it in

Starting from the simplest:

## 1. ScorpioConcepts.Framework.LogServer
This simple library creates a logging target to send anything you want to log to. It then listens on a TCP port (7446 by default, or pass the address and port to listen on). Simply use your favourite telnet client to connect to the port, and you can watch a live log of what your application is doing. Great for debugging things like Windows Services.

_See the documentation in the folder for more details if you are interested in using it._

## 2. ScorpioConcepts.Framework.PlugIn
A collection of classes and interfaces to simplify having a plugin framework in your application. Implement IPlugInHost in your application, extend IPlugIn to define an interface for your plugins, and use PlugInManager to find, create and initialize every plugin in a folder. On .NET 10 each plugin gets its own load context, and a plugin that fails to load is reported without stopping the others. Every DLL in the plugin folder runs code in your application, so protect that folder like the application itself.

_See the documentation in the folder for more details if you are interested in using it._

## 3. ScorpioConcepts.Framework.netNUTClient
A library for connecting to upsd servers from the [NUT project](http://www.networkupstools.org). Can be used to query or monitor UPS that you have connected to a Linux server.

_See the documentation in the folder for more details if you are interested in using it._

## 4. ScorpioConcepts.Framework.DBSchema
This is a project I have been working on a long time. The tl;dr version is it's a library where you define your database structure in a JSON or DBML file (or, for existing schemas, an XML file), and this library will automatically ensure that your actual database matches it. If you need to add a column, simply define it in the schema file, and when you run the app, this
library will compare the actual database with what the schema file says, and make any structural changes required. Create a `DBSchemaManager` for each database, with an optional `ILogger` to see what it changes. This is NOT a ORM library, or something like Entity Framework. It only deals in the structure of the database, not any data.

_See the documentation in the folder for more details if you are interested in using it._
