namespace ScorpioConcepts.Framework.DBSchema.Tests;

/// <summary>
/// FormatDataType turns the raw sys.columns values into a type that can be used in CREATE TABLE.
/// </summary>
public class FormatDataTypeTests
{
    [Theory]
    [InlineData("int", 4, 10, 0, "int")]
    [InlineData("bit", 1, 1, 0, "bit")]
    [InlineData("datetime", 8, 23, 3, "datetime")]
    [InlineData("uniqueidentifier", 16, 0, 0, "uniqueidentifier")]
    [InlineData("varchar", 50, 0, 0, "varchar(50)")]
    [InlineData("char", 10, 0, 0, "char(10)")]
    [InlineData("varbinary", 16, 0, 0, "varbinary(16)")]
    [InlineData("binary", 32, 0, 0, "binary(32)")]
    [InlineData("varchar", -1, 0, 0, "varchar(max)")]
    [InlineData("varbinary", -1, 0, 0, "varbinary(max)")]
    [InlineData("nvarchar", 100, 0, 0, "nvarchar(50)")]
    [InlineData("nchar", 2, 0, 0, "nchar(1)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("decimal", 9, 10, 2, "decimal(10,2)")]
    [InlineData("numeric", 5, 8, 3, "numeric(8,3)")]
    [InlineData("datetime2", 7, 23, 3, "datetime2(3)")]
    [InlineData("time", 3, 8, 0, "time(0)")]
    [InlineData("datetimeoffset", 10, 34, 7, "datetimeoffset(7)")]
    [InlineData("float", 8, 53, 0, "float")]
    [InlineData("NVARCHAR", 20, 0, 0, "NVARCHAR(10)")]
    public void Adds_the_size_for_types_that_have_one(string type, short maxLength, byte precision, byte scale, string expected)
    {
        DBSchemaManager.FormatDataType(type, maxLength, precision, scale, isIdentity: false).ShouldBe(expected);
    }

    [Theory]
    [InlineData("int", 4, 10, 0, "int identity")]
    [InlineData("bigint", 8, 19, 0, "bigint identity")]
    [InlineData("decimal", 9, 18, 0, "decimal(18,0) identity")]
    public void Marks_identity_columns(string type, short maxLength, byte precision, byte scale, string expected)
    {
        DBSchemaManager.FormatDataType(type, maxLength, precision, scale, isIdentity: true).ShouldBe(expected);
    }
}
