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

## Existing XML schema files
The original XML format is still fully supported. `CheckDatabaseSchema` detects whether a file is JSON or XML from its content,
so existing XML schema files keep working unchanged, and `GenerateSchemaXML` still writes XML.

To move an XML schema to JSON, convert it once. No database connection is needed:

```csharp
DBSchema.ConvertSchema("schema.xml", "schema.json", DBSchemaFormat.Json);
```

XML stores every default record value as text, so after converting, the values in the JSON file are strings.
SQL Server converts them to the column types when the records are inserted.
