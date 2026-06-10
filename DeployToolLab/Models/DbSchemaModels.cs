namespace DeployTool.Models;

public enum DbObjectType
{
    Table,
    View,
    StoredProcedure
}

public class DbColumn
{
    public string ColumnName { get; init; } = string.Empty;
    public string DataType { get; init; } = string.Empty;
    public bool IsNullable { get; init; }
    public string? ColumnDefault { get; init; }
    public bool IsPrimaryKey { get; init; }
    public bool IsForeignKey { get; init; }
    public string? ReferencedTable { get; init; }
    public int OrdinalPosition { get; init; }
}

public class DbIndex
{
    public string IndexName { get; init; } = string.Empty;
    public bool IsUnique { get; init; }
    public bool IsPrimaryKey { get; init; }
    public List<string> Columns { get; init; } = [];
}

public class DbObject
{
    public string Name { get; init; } = string.Empty;
    public DbObjectType ObjectType { get; init; }
    public List<DbColumn> Columns { get; set; } = [];
    public List<DbIndex> Indexes { get; set; } = [];
    public string? Definition { get; set; }

    public string Icon => ObjectType switch
    {
        DbObjectType.Table => "📋",
        DbObjectType.View => "👁",
        DbObjectType.StoredProcedure => "⚙",
        _ => "📄"
    };
}

public class DbSchemaInfo
{
    public string DatabaseName { get; set; } = string.Empty;
    public List<DbObject> Tables { get; set; } = [];
    public List<DbObject> Views { get; set; } = [];
    public List<DbObject> StoredProcedures { get; set; } = [];
}

public class PreflightCheckResult
{
    public string CheckName { get; init; } = string.Empty;
    public bool IsPassed { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool IsWarning { get; set; }
}
