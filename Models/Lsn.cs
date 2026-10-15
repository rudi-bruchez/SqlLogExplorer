using System;
using System.Globalization;

namespace SqlLogExplorer.Models;

/// <summary>LSN au format texte de fn_dblog (vlf:block:slot en hexadécimal), spec03 §2 et §3.</summary>
public static class Lsn
{
    /// <summary>2017 et 2019 rendent l'hexadécimal en minuscules : on stocke en majuscules pour un tri textuel unique.</summary>
    public static string Normalize(string lsn) => lsn.ToUpperInvariant();

    /// <summary>fn_dblog refuse la forme hexadécimale à deux-points (Msg 9005) ; il accepte la forme décimale a:b:c.</summary>
    public static string ToFnDblogArgument(string lsn)
    {
        var parts = lsn.Split(':');
        if (parts.Length != 3) throw new FormatException($"Invalid LSN '{lsn}'.");
        return string.Join(':', Array.ConvertAll(parts,
            p => long.Parse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)));
    }
}
