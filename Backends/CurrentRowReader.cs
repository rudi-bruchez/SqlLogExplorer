using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SqlLogExplorer.Decoding;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Backends;

/// <summary>Row : la ligne courante, lue dans la page pendant qu'elle était tenue ; Unavailable : pourquoi l'ancre 3 ne s'applique pas.</summary>
public sealed record CurrentRowResult(byte[]? Row, string? Unavailable);

/// <summary>Ancre 3 (spec03 §7.2) : la ligne telle qu'elle est maintenant, lue sous verrou avec la suite du journal.</summary>
public sealed partial class CurrentRowReader
{
    public const string Locked = "row locked by an open transaction";
    public const string NotThisRow = "current row is not this row";
    public const string Truncated = "log truncated since the import, re-import to use the current row";
    public const string Deleted = "row deleted since: the DELETE anchor applies";
    public const string NotSendable = "key value cannot be sent in the database code page";
    public const string Rebuilt = "table rebuilt since this record, the current row is not comparable";

    private readonly string _connectionString;
    private int? _databaseCodePage;

    public CurrentRowReader(string connectionString) => _connectionString = connectionString;   // chaîne vers la base analysée

    /// <summary>La lecture se fait hors pool : SERIALIZABLE resterait sur la session rendue au pool (sp_reset_connection ne remet pas
    /// le niveau d'isolation, mesuré sur 2017 et 2022), et toute connexion suivante de l'application sur la base en hériterait.</summary>
    private string ReadConnectionString => new SqlConnectionStringBuilder(_connectionString) { Pooling = false }.ConnectionString;

    /// <summary>false quand la base a l'ADR : la page peut porter des octets annulés (spec03 §2). La colonne n'existe pas avant 2019.</summary>
    public async Task<bool> IsAvailableForDatabaseAsync(CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            IF COL_LENGTH('sys.databases', 'is_accelerated_database_recovery_on') IS NULL SELECT CONVERT(bit, 0);
            ELSE EXEC (N'SELECT is_accelerated_database_recovery_on FROM sys.databases WHERE database_id = DB_ID();');
            """;
        return !Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>acceptSuffix reçoit les enregistrements lus depuis le dernier LSN du cache, pendant que la ligne est tenue ;
    /// il les ajoute au cache, les indexe, et rend false si la ligne a été supprimée après l'enregistrement choisi.</summary>
    public async Task<CurrentRowResult> ReadAsync(TableSchema schema, IReadOnlyList<(SchemaColumn Column, object? Value)> key,
        string lastCachedLsn, Func<IReadOnlyList<LogRecord>, CancellationToken, Task<bool>> acceptSuffix, CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(ReadConnectionString);
        await cn.OpenAsync(ct);
        await using (var set = cn.CreateCommand())
        {
            set.CommandText = "SET LOCK_TIMEOUT 5000;";
            await set.ExecuteNonQueryAsync(ct);
        }

        // Un paramètre varchar part dans la page de code par défaut de la base : une clé qui n'y passe pas deviendrait « ??? »
        // et viserait une autre ligne. Rien n'est cherché dans ce cas.
        if (key.Any(k => k.Column.SystemTypeId is 175 or 167 && k.Value is string))
        {
            _databaseCodePage ??= await DatabaseCodePageAsync(cn, ct);
            if (key.Any(k => k.Column.SystemTypeId is 175 or 167 && k.Value is string v && !Sendable(v, k.Column.CodePage, _databaseCodePage.Value)))
                return new CurrentRowResult(null, NotSendable);
        }
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        // 1. Chercher la ligne et la tenir jusqu'au commit : aucun écrivain ne peut plus la changer.
        var addresses = new List<(int File, int Page, int Slot)>();
        try
        {
            await using var seek = cn.CreateCommand();
            seek.Transaction = tx;
            seek.CommandText = SeekText(schema, key, seek.Parameters);
            await using var r = await seek.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) addresses.Add(ParseAddress(r.GetString(0)));
        }
        catch (SqlException e) when (e.Number == 1222) { return new CurrentRowResult(null, Locked); }
        catch (SqlException e) { throw StepFailed("seek", e); }

        // 2. La suite du journal depuis le dernier enregistrement du cache ; il doit en être le premier, sinon le journal a été tronqué.
        var suffix = new List<LogRecord>();
        try
        {
            await using var log = cn.CreateCommand();
            log.Transaction = tx;
            log.CommandText = $"SELECT {LogColumns.SelectList} FROM sys.fn_dblog(@start, NULL);";
            log.Parameters.Add(new SqlParameter("@start", SqlDbType.NVarChar, 30) { Value = Lsn.ToFnDblogArgument(lastCachedLsn) });
            log.CommandTimeout = 0;
            await using var r = await log.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            while (await r.ReadAsync(ct)) suffix.Add(LogColumns.Read(r));
        }
        catch (SqlException e) { throw StepFailed("log suffix", e); }
        if (suffix.Count == 0 || suffix[0].Lsn != Lsn.Normalize(lastCachedLsn)) return new CurrentRowResult(null, Truncated);

        // 3. Le cache reçoit la suite ; une suppression de la ligne après l'enregistrement choisi renvoie à l'ancre 2.
        // Sans adresse, la suite est lue quand même : une ligne supprimée depuis l'import n'est plus là pour la recherche.
        if (!await acceptSuffix(suffix, ct)) return new CurrentRowResult(null, Deleted);
        if (addresses.Count == 0) return new CurrentRowResult(null, NotThisRow);

        // 4. La page de chaque adresse, ligne toujours tenue.
        var dumps = new List<(int Slot, long? AllocUnitId, List<(string ParentObject, string Value)> Lines)>();
        foreach (var (file, page, slot) in addresses)
        {
            try
            {
                await using var dbcc = cn.CreateCommand();
                dbcc.Transaction = tx;
                // DBCC PAGE refuse un appel de fonction en argument (DB_NAME() : erreur de syntaxe, mesuré sur 2022) ; une variable passe.
                dbcc.CommandText = $"DECLARE @db sysname = DB_NAME(); DBCC PAGE (@db, {file}, {page}, 1) WITH TABLERESULTS;";
                var lines = new List<(string, string)>();
                long? allocUnitId = null;
                await using var r = await dbcc.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var value = r["VALUE"] as string ?? "";
                    // En-tête de page : l'unité d'allocation qui possède la page (mesuré sur 2022).
                    if (r["Field"] as string == "Metadata: AllocUnitId" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var au))
                        allocUnitId = au;
                    lines.Add((r["ParentObject"] as string ?? "", value));
                }
                dumps.Add((slot, allocUnitId, lines));
            }
            catch (SqlException e) { throw StepFailed("page read", e); }
        }

        // 5. Commit, puis garder l'enregistrement primaire dont la clé est celle de la ligne. Une page d'une autre unité d'allocation
        // (index reconstruit, table reconstruite ou recréée) : la chaîne rejouée, prise sur l'ancienne, ne contient pas ses modifications.
        await tx.CommitAsync(ct);
        if (dumps.Any(d => d.AllocUnitId != schema.AllocUnitId)) return new CurrentRowResult(null, Rebuilt);
        foreach (var (slot, _, lines) in dumps)
        {
            var row = ParseSlotDump(lines, slot);
            if (row is null || row.Length == 0 || (row[0] >> 1 & 7) != 0) continue;
            if (KeyMatches(row, schema, key)) return new CurrentRowResult(row, null);
        }
        return new CurrentRowResult(null, NotThisRow);
    }

    private static async Task<int> DatabaseCodePageAsync(SqlConnection cn, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT CONVERT(int, COLLATIONPROPERTY(CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation')), 'CodePage'));";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>Même page de code que la colonne : la valeur en vient. Sinon, la valeur doit faire l'aller-retour par la page de la base.</summary>
    internal static bool Sendable(string value, int? columnCodePage, int databaseCodePage)
    {
        if (columnCodePage == databaseCodePage) return true;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding;
        try { encoding = Encoding.GetEncoding(databaseCodePage); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return false; }
        return encoding.GetString(encoding.GetBytes(value)) == value;
    }

    private static InvalidOperationException StepFailed(string step, SqlException e) =>
        new($"Current row, step '{step}' failed: {e.Message}", e);

    private static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary>Une égalité paramétrée par colonne de clé, IS NULL pour une valeur NULL ; l'uniqueifier ne se cherche pas, il départage à l'étape 5.</summary>
    private static string SeekText(TableSchema schema, IReadOnlyList<(SchemaColumn Column, object? Value)> key, SqlParameterCollection parameters)
    {
        var predicates = new List<string>();
        foreach (var (column, value) in key)
        {
            if (column.IsUniqueifier) continue;
            if (value is null) { predicates.Add($"{Quote(column.Name)} IS NULL"); continue; }
            var name = "@k" + parameters.Count;
            parameters.Add(Parameter(name, column, value));
            predicates.Add($"{Quote(column.Name)} = {name}");
        }
        return $"SELECT sys.fn_PhysLocFormatter(%%physloc%%) FROM {Quote(schema.SchemaName)}.{Quote(schema.TableName)} WITH (ROWLOCK, HOLDLOCK) WHERE {string.Join(" AND ", predicates)};";
    }

    /// <summary>Paramètre du type exact de la colonne : un type plus large convertirait la colonne, ou perdrait de la précision (datetime2).</summary>
    private static SqlParameter Parameter(string name, SchemaColumn c, object value)
    {
        var p = new SqlParameter { ParameterName = name };
        if (value is SqlTemporal t) value = t.Value;   // le paramètre porte la valeur .NET, son texte ne sert qu'à l'affichage
        (p.SqlDbType, p.Value) = c.SystemTypeId switch
        {
            48 => (SqlDbType.TinyInt, value),
            52 => (SqlDbType.SmallInt, value),
            56 => (SqlDbType.Int, value),
            127 => (SqlDbType.BigInt, value),
            104 => (SqlDbType.Bit, value),
            106 or 108 => (SqlDbType.Decimal, value),
            60 => (SqlDbType.Money, value),
            122 => (SqlDbType.SmallMoney, value),
            59 => (SqlDbType.Real, value),
            62 => (SqlDbType.Float, value),
            40 => (SqlDbType.Date, ((DateOnly)value).ToDateTime(TimeOnly.MinValue)),
            41 => (SqlDbType.Time, ((TimeOnly)value).ToTimeSpan()),
            61 => (SqlDbType.DateTime, value),
            58 => (SqlDbType.SmallDateTime, value),
            42 => (SqlDbType.DateTime2, value),
            43 => (SqlDbType.DateTimeOffset, value),
            36 => (SqlDbType.UniqueIdentifier, value),
            175 => (SqlDbType.Char, value),
            167 => (SqlDbType.VarChar, value),
            239 => (SqlDbType.NChar, value),
            231 => (SqlDbType.NVarChar, value),
            173 => (SqlDbType.Binary, value),
            165 => (SqlDbType.VarBinary, value),
            _ => throw new NotSupportedException($"system type {c.SystemTypeId} cannot be sought"),
        };
        if (c.SystemTypeId is 106 or 108) { p.Precision = (byte)c.Precision; p.Scale = (byte)c.Scale; }
        if (c.SystemTypeId is 41 or 42 or 43) p.Scale = (byte)c.Scale;
        if (c.SystemTypeId is 175 or 167 or 173 or 165) p.Size = c.MaxInrowLength;
        if (c.SystemTypeId is 239 or 231) p.Size = c.MaxInrowLength / 2;
        return p;
    }

    /// <summary>fn_PhysLocFormatter rend "(fichier:page:slot)".</summary>
    private static (int File, int Page, int Slot) ParseAddress(string text)
    {
        var parts = text.Trim('(', ')', ' ').Split(':');
        if (parts.Length != 3) throw new FormatException($"Unexpected row address '{text}'.");
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    /// <summary>Chaque colonne de clé de la ligne lue vaut celle de l'enregistrement choisi, uniqueifier compris (0 pour la première ligne d'une clé).
    /// Les valeurs sont comparées sans collation : deux chaînes que la collation confond restent distinctes, comme leurs octets.</summary>
    private static bool KeyMatches(byte[] row, TableSchema schema, IReadOnlyList<(SchemaColumn Column, object? Value)> key)
    {
        var decoded = RowDecoder.Decode(row, schema);
        foreach (var (column, value) in key)
        {
            var actual = decoded.Columns.FirstOrDefault(c => c.Name == column.Name)?.Value;
            if (column.IsUniqueifier) actual = actual is int i ? i : 0;
            var same = (actual, value) switch
            {
                (null, null) => true,
                (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
                _ => Equals(actual, value),
            };
            if (!same) return false;
        }
        return true;
    }

    [GeneratedRegex(@"^Slot (\d+), Offset 0x[0-9a-fA-F]+, Length (\d+), DumpStyle BYTE$")]
    private static partial Regex SlotHeader();

    /// <summary>Octets d'un slot dans DBCC PAGE (…, 1) WITH TABLERESULTS : par ligne de dump, les 44 caractères d'hexadécimal après ":   ",
    /// espaces retirés, lignes concaténées dans l'ordre, tronqué à la Length de ParentObject. La partie ASCII n'est jamais lue :
    /// elle peut ressembler à de l'hexadécimal. null quand le slot n'est pas dans le dump.</summary>
    internal static byte[]? ParseSlotDump(IEnumerable<(string ParentObject, string Value)> lines, int slot)
    {
        var hex = new StringBuilder();
        int? length = null;
        foreach (var (parent, value) in lines)
        {
            var m = SlotHeader().Match(parent);
            if (!m.Success || int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) != slot) continue;
            length = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var at = value.IndexOf(":   ", StringComparison.Ordinal);
            if (at < 0) continue;
            var area = value.Substring(at + 4, Math.Min(44, value.Length - at - 4));
            hex.Append(area.Replace(" ", ""));
        }
        if (length is null || hex.Length % 2 != 0) return null;
        var bytes = Convert.FromHexString(hex.ToString());
        return bytes.Length < length ? null : bytes[..length.Value];
    }
}
