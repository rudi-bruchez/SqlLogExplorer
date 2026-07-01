namespace SqlLogExplorer.Models;

/// <summary>Bornes LSN au format fn_dblog (vlf:block:slot) passées à fn_dblog(@start,@end) (spec §3.1).</summary>
public sealed record LsnRange(string Start, string End);
