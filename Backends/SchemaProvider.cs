using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SqlLogExplorer.Decoding;

namespace SqlLogExplorer.Backends;

/// <summary>AllocUnitId vers TableSchema, lu dans le catalogue de la base connectée (spec03 §4). Cache par unité d'allocation.</summary>
public sealed class SchemaProvider
{
    /// <summary>Texte SQL embarqué, aussi utilisé pour lire tout le catalogue d'un coup.</summary>
    public static readonly string Query = LoadQuery();

    private readonly string _connectionString;
    private readonly Dictionary<long, TableSchema?> _cache = new();

    public SchemaProvider(string connectionString) => _connectionString = connectionString;

    private static string LoadQuery()
    {
        using var stream = typeof(SchemaProvider).Assembly.GetManifestResourceStream("SqlLogExplorer.Backends.Sql.TableSchema.sql")
            ?? throw new InvalidOperationException("Ressource TableSchema.sql introuvable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Schéma de l'unité d'allocation, ou null si elle est inconnue de la base connectée.</summary>
    public async Task<TableSchema?> GetAsync(long allocUnitId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(allocUnitId, out var cached)) return cached;

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = Query;
        cmd.Parameters.AddWithValue("@AllocUnitId", allocUnitId);
        await using var r = await cmd.ExecuteReaderAsync(ct);

        string schemaName = "", tableName = "";
        int indexId = 0, auType = 0;
        var columns = new List<SchemaColumn>();
        while (await r.ReadAsync(ct))
        {
            schemaName = (string)r["SchemaName"];
            tableName = (string)r["TableName"];
            indexId = Int(r, "IndexId");
            auType = Int(r, "AllocUnitType");
            var id = Int(r, "PartitionColumnId");
            var dropped = Convert.ToBoolean(r["IsDropped"]);
            var uniqueifier = Convert.ToBoolean(r["IsUniqueifier"]);
            var name = r["ColumnName"] is string s ? s
                : uniqueifier ? "uniqueifier"
                : dropped ? $"dropped#{id}"
                : $"column#{id}";
            columns.Add(new SchemaColumn(id, name, Int(r, "LeafOffset"), Int(r, "LeafNullBit"), Int(r, "LeafBitPosition"),
                Int(r, "MaxInrowLength"), Int(r, "SystemTypeId"), Int(r, "Precision"), Int(r, "Scale"), NullableInt(r, "CodePage"),
                Convert.ToBoolean(r["IsNullable"]), dropped, uniqueifier, Convert.ToBoolean(r["HasDefault"]),
                r["DefaultValue"] is string d ? d : null, NullableInt(r, "KeyOrdinal")));
        }

        var schema = columns.Count == 0 ? null : new TableSchema(allocUnitId, schemaName, tableName, indexId, auType, columns);
        _cache[allocUnitId] = schema;
        return schema;
    }

    /// <summary>sysadmin ou db_owner : sans l'un des deux, le catalogue est filtré sans erreur (spec03 §4).</summary>
    public async Task<bool> IsPrivilegedAsync(CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT CONVERT(bit, ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0) | ISNULL(IS_ROLEMEMBER('db_owner'), 0));";
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }

    // Plusieurs colonnes sont tinyint ou smallint : GetInt32 lèverait une InvalidCastException.
    private static int Int(SqlDataReader r, string name) => Convert.ToInt32(r[name]);
    private static int? NullableInt(SqlDataReader r, string name) => r[name] is DBNull ? null : Convert.ToInt32(r[name]);
}
