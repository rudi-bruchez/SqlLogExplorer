using Microsoft.Data.SqlClient;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Backends;

/// <summary>Colonnes de fn_dblog / fn_dump_dblog importées (spec03 §3) et leur lecture, communes aux deux backends.</summary>
public static class LogColumns
{
    public const string SelectList = """
        [Current LSN], [Operation], [Context], [Transaction ID], [AllocUnitName],
        [RowLog Contents 0], [RowLog Contents 1],
        [AllocUnitId], [PartitionId], [Page ID], [Slot ID], [Offset in Row], [Modify Size], [Num Elements],
        [Description], [Transaction Name],
        [RowLog Contents 2], [RowLog Contents 3], [RowLog Contents 4], [RowLog Contents 5]
        """;

    /// <summary>Lecture dans l'ordre du SELECT (accès séquentiel). Les types sont ceux que fn_dblog renvoie
    /// (bigint, int, smallint, spec03 §2) : on lit par Convert pour ne pas dépendre de la largeur exacte.</summary>
    public static LogRecord Read(SqlDataReader r)
    {
        static byte[]? Bytes(SqlDataReader r, int i) => r.IsDBNull(i) ? null : (byte[])r.GetValue(i);
        static string? Text(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        static long? Long(SqlDataReader r, int i) => r.IsDBNull(i) ? null : System.Convert.ToInt64(r.GetValue(i));
        static int? Int(SqlDataReader r, int i) => r.IsDBNull(i) ? null : System.Convert.ToInt32(r.GetValue(i));

        return new LogRecord(Lsn.Normalize(r.GetString(0)), r.GetString(1), Text(r, 2), Text(r, 3), Text(r, 4), Bytes(r, 5), Bytes(r, 6))
        {
            AllocUnitId = Long(r, 7), PartitionId = Long(r, 8), PageId = Text(r, 9), SlotId = Int(r, 10),
            OffsetInRow = Int(r, 11), ModifySize = Int(r, 12), NumElements = Int(r, 13),
            Description = Text(r, 14), TransactionName = Text(r, 15),
            RowLogContents2 = Bytes(r, 16), RowLogContents3 = Bytes(r, 17), RowLogContents4 = Bytes(r, 18), RowLogContents5 = Bytes(r, 19),
        };
    }
}
