# Database Schema Library

## Overview
Describe the structure of a SQL Server database in a schema file, and let this library make the real database match it.
When the application starts, `CheckDatabaseSchema` compares the database with the file and adds anything that is missing:
tables, columns, primary keys, indexes, foreign keys, and default records for newly created tables.

It only ever adds. It does not drop or change existing tables, columns or indexes, and it is not an ORM.
Each `CheckDatabaseSchema` call runs in a single transaction, so if anything fails part way through, the database is
left exactly as it was and the exception is thrown. It works on the `dbo` schema only, and, like SQL Server, compares
table, column, index and key names without regard to case.

## Simple Example
```csharp
using Microsoft.Data.SqlClient;
using ScorpioConcepts.Framework.DBSchema;

using var connection = new SqlConnection(connectionString);
var schema = new DBSchemaManager(connection, logger);   // the logger is optional

// Bring the database up to date with the schema file
schema.CheckDatabaseSchema("schema.json");

// Or create a schema file from an existing database
schema.GenerateSchema("schema.json");
```

Each `DBSchemaManager` works on the database its connection points to, so an application can keep several
databases up to date side by side. The connection can be open or closed: a closed one is opened for each call and
closed again afterwards. The manager never disposes the connection.

## Logging
Pass any `Microsoft.Extensions.Logging.ILogger` to the constructor to see what the manager does. Changes to the
database (tables, columns, indexes, foreign keys and default records being created) are logged at `Information`,
and reading the database's structure at `Debug`. Without a logger, nothing is logged.

```csharp
var schema = new DBSchemaManager(connection, loggerFactory.CreateLogger<DBSchemaManager>());
```

## Working with a schema in code
`DBSchemaDefinition` is the structure of a database: its `Tables`, each with read-only lists of `Columns`,
`Indexes`, `ForeignKeys` and `DefaultRecords`, and a `PrimaryKey`.

```csharp
DBSchemaDefinition fromFile = DBSchemaDefinition.Load("schema.json");   // JSON, XML or DBML, detected from the content
DBSchemaDefinition fromDatabase = schema.ReadDatabaseSchema();

foreach (DBTable table in fromFile.TablesInOrder)
{
    if (!fromDatabase.Tables.ContainsKey(table.Name)) Console.WriteLine(table.Name + " will be created");
}

schema.CheckDatabaseSchema(fromFile);
fromDatabase.Save("current.json");
```

To include default records when generating a schema file, build them with `DBDefaultRecords`:

```csharp
var defaults = new DBDefaultRecords();
defaults.AddRecord("Customers").AddValue("Name", "Walk-in customer").AddValue("Email", null);
schema.GenerateSchema("schema.json", defaults);
```

## Schema files
New schema files are written as JSON. Only a table's `name` and its `columns` are required:

```json
{
  "tables": [
    {
      "name": "Customers",
      "columns": [
        { "name": "Id", "dataType": "int identity", "options": "NOT NULL" },
        { "name": "Name", "dataType": "nvarchar(50)", "options": "NOT NULL DEFAULT ('')" },
        { "name": "Email", "dataType": "varchar(100)", "options": "NULL" }
      ],
      "primaryKey": { "name": "PK_Customers", "columns": [ "Id" ] },
      "indexes": [
        { "name": "IX_Customers_Email", "clustered": false, "unique": true, "columns": [ "Email" ] }
      ],
      "defaultRecords": [
        { "Name": "Walk-in customer", "Email": null }
      ]
    },
    {
      "name": "Orders",
      "columns": [
        { "name": "Id", "dataType": "int identity", "options": "NOT NULL" },
        { "name": "CustomerId", "dataType": "int", "options": "NOT NULL" }
      ],
      "primaryKey": { "name": "PK_Orders", "columns": [ "Id" ] },
      "foreignKeys": [
        { "name": "FK_Orders_Customers", "column": "CustomerId", "referencedTable": "Customers", "referencedColumn": "Id" }
      ]
    }
  ]
}
```

- `dataType` is written as in `CREATE TABLE`. Add ` identity` for an `IDENTITY(1,1)` column, or give the seed and
  increment, as in `int identity(1000,1)`.
- `options` holds the rest of the column definition, such as `NOT NULL`, `NULL DEFAULT (getdate())` or, to name the
  default constraint, `NOT NULL CONSTRAINT [DF_Orders_PlacedOn] DEFAULT (getdate())`.
- A computed column has its expression as the `dataType` and, if it is stored, `PERSISTED` as the `options`:
  `{ "name": "Total", "dataType": "AS ([Qty]*[Price])", "options": "PERSISTED" }`.
- A primary key is clustered unless it says `"clustered": false`, which leaves room for a clustered index on other columns.
- Index columns can be plain names, or objects such as `{ "name": "PlacedOn", "descending": true }`.
- A foreign key on several columns lists them in order, matched one for one:
  `{ "name": "FK_Deliveries_OrderLines", "columns": [ "OrderId", "LineNo" ], "referencedTable": "OrderLines", "referencedColumns": [ "OrderId", "LineNo" ] }`.
- Default records are inserted only when their table is created. Their values keep their JSON types: strings, numbers, `true`/`false` and `null`.
- Property names are not case sensitive. Comments, trailing commas and unknown properties are allowed.

## DBML schema files
Schemas can also be written in [DBML](https://dbml.dbdiagram.io), the language used by [dbdiagram.io](https://dbdiagram.io),
so the same file can draw a diagram of the database and keep the database in sync. This is the JSON example above in DBML:

```dbml
Table Customers {
  Id int [pk, increment, not null]
  Name nvarchar(50) [not null, default: '']
  Email varchar(100)

  indexes {
    (Email) [name: 'IX_Customers_Email', unique]
  }

  records (Name, Email) {
    'Walk-in customer', null
  }
}

Table Orders {
  Id int [pk, increment, not null]
  CustomerId int [not null]
}

Ref FK_Orders_Customers: Orders.CustomerId > Customers.Id
```

Write one with `schema.GenerateSchema("schema.dbml", format: DBSchemaFormat.Dbml)`, or convert an existing schema with
`ConvertSchema`. `CheckDatabaseSchema` recognises DBML from its content, like the other formats. Files written by this
library are valid DBML for other DBML tools too.

DBML can describe more than DBSchema can create, and it has no syntax for a few things DBSchema needs, so:

- **Names DBML leaves optional are generated**: `PK_<table>` for a primary key, `IX_<table>_<columns>` for an index
  (including the unique index made for a `[unique]` column), and `FK_<table>_<columns>` for a foreign key, including
  inline `[ref: > table.column]` settings, which cannot be named. Give an index or `Ref` a name to use your own.
- **Foreign keys on several columns** are written as `Ref: Deliveries.(OrderId, LineNo) > OrderLines.(OrderId, LineNo)`.
- **Clustered indexes** are named by the table, because DBML has no clustered setting for an index:
  `Table Orders [clustered_index: 'CIX_Orders'] { ... }`. The primary key is clustered unless `clustered_index` names
  another index, or is `false` to cluster nothing.
- **Defaults** are plain DBML values (`default: 0`, `default: 'text'`, `default: true`) or `` `expressions` ``
  (`` default: `getdate()` ``). They are created with the parentheses SQL Server uses, so `default: 0` becomes
  `DEFAULT ((0))`. Name the default constraint with `default_name: 'DF_Orders_PlacedOn'`.
- **Identity columns** are `[increment]`. Give a seed and step other than 1 with `identity_seed: 1000, identity_increment: 5`.
- **Computed columns** have their expression as a quoted type, with `persisted: true` if it is stored:
  `Total "AS ([Qty]*[Price])" [persisted: true]`.
- **Records** are default records. As in other DBML tools, `null` or an empty field inserts NULL. Write `` `DEFAULT` `` to
  leave a column out of the inserted row so it gets the column's default; other DBML tools turn it into
  `INSERT ... VALUES (DEFAULT)`, which does the same. `[example]` records are only examples and are not inserted.
- **Primary key and `increment` columns** are `NOT NULL` unless they say `null`.
- **Ignored**, because they only describe a diagram: `Project`, `Note`, `TableGroup`, `DiagramView`, `Metadata`, colours,
  notes and other custom properties.
- **Rejected with an error**, because DBSchema cannot create them: enums, `checks`, `TablePartial`, `use` imports, tables
  outside the `dbo` (or DBML's default `public`) schema, many-to-many (`<>`) refs, `delete`/`update` actions other than
  `no action`, `hash` indexes, indexes on expressions, and expressions other than `` `DEFAULT` `` in records.

## A schema file is code
Treat a schema file like source code, and only load files you trust. Every name in the file (tables, columns,
indexes and keys) is quoted before it goes into SQL, so a name can't run SQL of its own, and default record values are
always sent as parameters. But `dataType` and `options` are SQL by design (types, defaults, computed column expressions),
so whoever writes the file decides what SQL runs, with the permissions of the connection. Don't build a schema file
from user input.

## Existing XML schema files
The original XML format is still fully supported. `CheckDatabaseSchema` detects whether a file is JSON, XML or DBML from its content,
so existing XML schema files keep working unchanged, and `GenerateSchema` still writes XML when asked:

```csharp
schema.GenerateSchema("schema.xml", format: DBSchemaFormat.Xml);
```

The XML format has the same features as JSON. A nonclustered primary key is
`<primary_key name="PK_Events" clustered="False">`, and a foreign key on several columns lists them as children:

```xml
<foreign_key name="FK_Deliveries_OrderLines" pk_table="OrderLines">
  <column name="OrderId" pk_column="OrderId" />
  <column name="LineNo" pk_column="LineNo" />
</foreign_key>
```

To move an XML schema to JSON, convert it once. No database connection is needed:

```csharp
DBSchemaManager.ConvertSchema("schema.xml", "schema.json", DBSchemaFormat.Json);
```

XML stores every default record value as text, so after converting, the values in the JSON file are strings.
SQL Server converts them to the column types when the records are inserted.

## .NET 10 and .NET Standard 2.0
The package has two builds:
- **.NET 10** uses Microsoft.Data.SqlClient 7.x, the current line. Use it wherever you can.
- **.NET Standard 2.0** is there so applications on .NET 8 or older, and .NET Framework, can still use the
  library. It has to use Microsoft.Data.SqlClient 5.2.x, because later versions no longer support .NET Standard.

> [!WARNING]
> The 5.2 line of Microsoft.Data.SqlClient is older than the one the .NET 10 build uses, and may not get future
> security fixes. Treat the .NET Standard build as a way to keep running while you move to .NET 10, not as a
> long-term choice.

## Upgrading from the static API
Version 2.0 replaces the static `DBSchema` class with `DBSchemaManager`:

| Before | Now |
| --- | --- |
| `DBSchema.InitializeDB(connection)` | `var schema = new DBSchemaManager(connection, logger)` |
| `DBSchema.CheckDatabaseSchema(file)` | `schema.CheckDatabaseSchema(file)` |
| `DBSchema.GenerateSchema(file, defaults)` | `schema.GenerateSchema(file, defaults)` |
| `DBSchema.GenerateSchemaXML(file, defaults)` | `schema.GenerateSchema(file, defaults, DBSchemaFormat.Xml)` |
| `DBSchema.ConvertSchema(...)` | `DBSchemaManager.ConvertSchema(...)` |
| `DBSchema.DebugLog` | an `ILogger` passed to the constructor |
| `DBTable.Column`, `DBTable.PKey`, `DBTable.Index`, `DBTable.ForeignKey` | `DBColumn`, `DBPrimaryKey`, `DBIndex`, `DBForeignKey` |
| `PKey.KeyColumns`, `Index.Columns` (a dictionary) | `DBPrimaryKey.Columns`, `DBIndex.Columns` (a list of `DBIndexColumn`) |
| `ForeignKey.Column`, `ForeignKey.PKeyTable`, `ForeignKey.PKeyColumn` | `DBForeignKey.Columns`, `DBForeignKey.ReferencedTable`, `DBForeignKey.ReferencedColumns` (lists, for keys on several columns) |

Schema files don't change: files written by earlier versions read exactly as before.
