# Database Schema Library

## Overview
Describe the structure of a SQL Server database in a schema file, and let this library make the real database match it.
When the application starts, `CheckDatabaseSchema` compares the database with the file and adds anything that is missing:
tables, columns, primary keys, indexes, foreign keys, and default records for newly created tables.

It only ever adds. It does not drop or change existing tables, columns or indexes, and it is not an ORM.

## Simple Example
```csharp
using Microsoft.Data.SqlClient;
using ScorpioConcepts.Framework.DBSchema;

using var connection = new SqlConnection(connectionString);
connection.Open();
DBSchema.InitializeDB(connection);

// Bring the database up to date with the schema file
DBSchema.CheckDatabaseSchema("schema.json");

// Or create a schema file from an existing database
DBSchema.GenerateSchema("schema.json");
```

`DBSchema.DebugLog` records what was done, which is useful when something does not look right.

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

- `dataType` is written as in `CREATE TABLE`. Add ` identity` for an `IDENTITY(1,1)` column.
- `options` holds the rest of the column definition, such as `NOT NULL` or `NULL DEFAULT (getdate())`.
- Index columns can be plain names, or objects such as `{ "name": "PlacedOn", "descending": true }`.
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

Write one with `DBSchema.GenerateSchema("schema.dbml", new DBDefaultRecords(), DBSchemaFormat.Dbml)`, or convert an existing
schema with `ConvertSchema`. `CheckDatabaseSchema` recognises DBML from its content, like the other formats.

DBML can describe more than DBSchema can create, so only part of it is used:

- **Names DBML leaves optional are generated**: `PK_<table>` for a primary key, `IX_<table>_<columns>` for an index
  (including the unique index made for a `[unique]` column), and `FK_<table>_<columns>` for a foreign key, including
  inline `[ref: > table.column]` settings, which cannot be named. Give an index or `Ref` a name to use your own.
- **Clustered indexes** are named by the table, as in `Table Orders [clustered_index: 'CIX_Orders'] { ... }`. DBML has
  no clustered setting for an index, and other DBML tools reject settings they do not know on an index, while custom
  properties on a table are allowed.
- **Defaults** are plain DBML values (`default: 0`, `default: 'text'`, `default: true`) or `` `expressions` ``
  (`` default: `getdate()` ``). They are created with the parentheses SQL Server uses, so `default: 0` becomes `DEFAULT ((0))`.
- **Records** are default records. As in other DBML tools, `null` or an empty field inserts NULL. Write `` `DEFAULT` `` to
  leave a column out of the inserted row so it gets the column's default; other DBML tools turn it into
  `INSERT ... VALUES (DEFAULT)`, which does the same. `[example]` records are only examples and are not inserted.
- **Primary key and `increment` columns** are `NOT NULL` unless they say `null`.
- **Ignored**, because they only describe a diagram: `Project`, `Note`, `TableGroup`, `DiagramView`, `Metadata`, colours,
  notes and other custom properties.
- **Rejected with an error**, because DBSchema cannot create them: enums, `checks`, `TablePartial`, `use` imports, tables
  outside the `dbo` (or DBML's default `public`) schema, many-to-many (`<>`) refs, `delete`/`update` actions other than
  `no action`, `hash` indexes, indexes on expressions, and expressions other than `` `DEFAULT` `` in records.

## Existing XML schema files
The original XML format is still fully supported. `CheckDatabaseSchema` detects whether a file is JSON, XML or DBML from its content,
so existing XML schema files keep working unchanged, and `GenerateSchemaXML` still writes XML.

To move an XML schema to JSON, convert it once. No database connection is needed:

```csharp
DBSchema.ConvertSchema("schema.xml", "schema.json", DBSchemaFormat.Json);
```

XML stores every default record value as text, so after converting, the values in the JSON file are strings.
SQL Server converts them to the column types when the records are inserted.
