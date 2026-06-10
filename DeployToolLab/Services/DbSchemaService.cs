using DeployTool.Models;
using Microsoft.Data.SqlClient;

namespace DeployTool.Services;

public class DbSchemaService(ILogger<DbSchemaService> logger)
{
    public async Task<bool> TestConnectionAsync(string connectionString)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DB 연결 테스트 실패");
            return false;
        }
    }

    public async Task<DbSchemaInfo> LoadSchemaAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var dbName = conn.Database;
        var tables = await LoadTablesAndViewsAsync(conn);
        var sps = await LoadStoredProceduresAsync(conn);

        return new DbSchemaInfo
        {
            DatabaseName = dbName,
            Tables = tables.Where(t => t.ObjectType == DbObjectType.Table).ToList(),
            Views = tables.Where(t => t.ObjectType == DbObjectType.View).ToList(),
            StoredProcedures = sps
        };
    }

    public async Task<List<DbColumn>> LoadColumnsAsync(string connectionString, string tableName)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var pkColumns = await GetPrimaryKeyColumnsAsync(conn, tableName);
        var fkMap = await GetForeignKeyMapAsync(conn, tableName);
        var columns = new List<DbColumn>();

        const string sql = """
            SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT, ORDINAL_POSITION
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = @tableName
            ORDER BY ORDINAL_POSITION
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@tableName", tableName);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var colName = reader.GetString(0);
            columns.Add(new DbColumn
            {
                ColumnName = colName,
                DataType = reader.GetString(1),
                IsNullable = reader.GetString(2) == "YES",
                ColumnDefault = reader.IsDBNull(3) ? null : reader.GetString(3),
                OrdinalPosition = reader.GetInt32(4),
                IsPrimaryKey = pkColumns.Contains(colName),
                IsForeignKey = fkMap.ContainsKey(colName),
                ReferencedTable = fkMap.TryGetValue(colName, out var refTable) ? refTable : null
            });
        }

        return columns;
    }

    public async Task<List<DbIndex>> LoadIndexesAsync(string connectionString, string tableName)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        const string sql = """
            SELECT i.name, i.is_unique, i.is_primary_key, c.name AS column_name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            JOIN sys.tables t ON i.object_id = t.object_id
            WHERE t.name = @tableName AND i.name IS NOT NULL
            ORDER BY i.name, ic.key_ordinal
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@tableName", tableName);

        var indexMap = new Dictionary<string, DbIndex>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var indexName = reader.GetString(0);
            if (!indexMap.TryGetValue(indexName, out var index))
            {
                index = new DbIndex
                {
                    IndexName = indexName,
                    IsUnique = reader.GetBoolean(1),
                    IsPrimaryKey = reader.GetBoolean(2)
                };
                indexMap[indexName] = index;
            }
            index.Columns.Add(reader.GetString(3));
        }

        return indexMap.Values.ToList();
    }

    public async Task<string?> LoadObjectDefinitionAsync(string connectionString, string objectName)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        const string sql = "SELECT OBJECT_DEFINITION(OBJECT_ID(@name))";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", objectName);

        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull or null ? null : result.ToString();
    }

    private static async Task<List<DbObject>> LoadTablesAndViewsAsync(SqlConnection conn)
    {
        const string sql = """
            SELECT TABLE_NAME, TABLE_TYPE
            FROM INFORMATION_SCHEMA.TABLES
            ORDER BY TABLE_TYPE, TABLE_NAME
            """;

        await using var cmd = new SqlCommand(sql, conn);
        var objects = new List<DbObject>();

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var tableType = reader.GetString(1) == "VIEW" ? DbObjectType.View : DbObjectType.Table;
            objects.Add(new DbObject
            {
                Name = reader.GetString(0),
                ObjectType = tableType
            });
        }

        return objects;
    }

    private static async Task<List<DbObject>> LoadStoredProceduresAsync(SqlConnection conn)
    {
        const string sql = """
            SELECT ROUTINE_NAME
            FROM INFORMATION_SCHEMA.ROUTINES
            WHERE ROUTINE_TYPE = 'PROCEDURE'
            ORDER BY ROUTINE_NAME
            """;

        await using var cmd = new SqlCommand(sql, conn);
        var sps = new List<DbObject>();

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sps.Add(new DbObject
            {
                Name = reader.GetString(0),
                ObjectType = DbObjectType.StoredProcedure
            });
        }

        return sps;
    }

    private static async Task<HashSet<string>> GetPrimaryKeyColumnsAsync(SqlConnection conn, string tableName)
    {
        const string sql = """
            SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
            WHERE TABLE_NAME = @tableName
              AND CONSTRAINT_NAME IN (
                SELECT CONSTRAINT_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
                WHERE TABLE_NAME = @tableName AND CONSTRAINT_TYPE = 'PRIMARY KEY')
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@tableName", tableName);

        var pkCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            pkCols.Add(reader.GetString(0));

        return pkCols;
    }

    private static async Task<Dictionary<string, string>> GetForeignKeyMapAsync(
        SqlConnection conn, string tableName)
    {
        const string sql = """
            SELECT kcu.COLUMN_NAME, ccu.TABLE_NAME AS REFERENCED_TABLE
            FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
              ON rc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
            JOIN INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE ccu
              ON rc.UNIQUE_CONSTRAINT_NAME = ccu.CONSTRAINT_NAME
            WHERE kcu.TABLE_NAME = @tableName
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@tableName", tableName);

        var fkMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            fkMap[reader.GetString(0)] = reader.GetString(1);

        return fkMap;
    }
}
