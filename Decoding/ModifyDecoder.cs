using System;
using System.Collections.Generic;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Decoding;

/// <summary>Remplacer ForwardLength octets à Offset par After (en avant) ; After.Length octets par Before (en arrière).</summary>
public sealed record Fragment(int Offset, byte[] Before, byte[] After, int ForwardLength);

/// <summary>Un contrôle de rejeu a échoué : les octets attendus ne sont pas dans la ligne (spec03 §7.3).</summary>
public sealed class ReplayCheckException(string message) : Exception(message);

/// <summary>Un enregistrement de modification en fragments applicables (spec03 §6, docs/research/rowlog-layouts.md règle A).</summary>
public static class ModifyDecoder
{
    /// <summary>null = disposition inconnue (spec03 §6.2) ; liste vide jamais renvoyée pour un enregistrement de modification.</summary>
    public static IReadOnlyList<Fragment>? Decode(LogRecord record) => record.Operation switch
    {
        "LOP_MODIFY_ROW" => DecodeRow(record) is { } f ? [f] : null,
        "LOP_MODIFY_COLUMNS" => DecodeColumns(record),
        _ => null,
    };

    private static Fragment? DecodeRow(LogRecord r)
    {
        if (r.OffsetInRow is not { } offset) return null;   // pas de décalage : disposition inconnue, on n'invente pas 0
        var before = r.RowLogContents0 ?? [];
        // Compensation : ModifySize est la longueur remplacée, pas celle des octets restaurés.
        if (r.IsCompensation && r.ModifySize is null) return null;
        return new Fragment(offset, before, r.RowLogContents1 ?? [], r.IsCompensation ? r.ModifySize!.Value : before.Length);
    }

    private static int Pad4(int n) => (n + 3) & ~3;

    private static List<Fragment>? DecodeColumns(LogRecord r)
    {
        var b = r.LogRecordBytes;
        if (b is null || b.Length < 4) return null;

        var h = U16(b, 2);
        if (h + 2 > b.Length) return null;
        var count = U16(b, h);
        if (count < 6 || count % 2 != 0 || count != r.NumElements) return null;
        if (h + 2 + 2 * count > b.Length) return null;

        // Chaque élément commence sur un multiple de 4 ; le bourrage entre deux éléments n'est pas fiable, on ne le lit pas.
        var elements = new (int Start, int Length)[count];
        var p = Pad4(h + 2 + 2 * count);
        for (var k = 0; k < count; k++)
        {
            var len = U16(b, h + 2 + 2 * k);
            if (p + len > b.Length) return null;
            elements[k] = (p, len);
            p = Pad4(p + len);
        }

        var n = (count - 4) / 2;
        if (elements[0].Length != 4 * n || elements[1].Length != 2 * n) return null;

        var fragments = new List<Fragment>(n);
        for (var i = 0; i < n; i++)
        {
            var oa = U16(b, elements[0].Start + 4 * i + 2);   // paires (ob, oa) : on applique à oa, le décalage courant
            var lb = U16(b, elements[1].Start + 2 * i);
            var before = Slice(b, elements[4 + 2 * i]);
            // Avant : lb octets, ou rien dans une compensation ; tout autre longueur est une disposition inconnue.
            if (before.Length != (r.IsCompensation ? 0 : lb)) return null;
            fragments.Add(new Fragment(oa, before, Slice(b, elements[5 + 2 * i]), lb));
        }
        return fragments;
    }

    private static int U16(byte[] b, int at) => b[at] | b[at + 1] << 8;
    private static byte[] Slice(byte[] b, (int Start, int Length) e) => b.AsSpan(e.Start, e.Length).ToArray();

    public static byte[] ApplyForward(byte[] row, Fragment f)
    {
        // Avant vide avec une longueur remplacée non nulle : compensation, les octets d'avant ne sont pas journalisés.
        var compensation = f.Before.Length == 0 && f.ForwardLength > 0;
        // long : un décalage proche de int.MaxValue ne doit pas déborder.
        if (f.Offset < 0 || f.ForwardLength < 0 || (long)f.Offset + Math.Max(f.ForwardLength, f.Before.Length) > row.Length)
            throw new ReplayCheckException($"Fragment at {f.Offset} (+{f.ForwardLength}) runs past the row ({row.Length} bytes).");
        if (!compensation && !row.AsSpan(f.Offset, f.Before.Length).SequenceEqual(f.Before))
            throw new ReplayCheckException($"The bytes before the change are not in the row at offset {f.Offset}.");
        return Replace(row, f.Offset, f.ForwardLength, f.After);
    }

    public static byte[] ApplyBackward(byte[] row, Fragment f)
    {
        if (f.Before.Length == 0 && f.ForwardLength > 0)
            throw new ReplayCheckException("A compensation record does not log the bytes it replaced and cannot be undone.");
        if (f.Offset < 0 || (long)f.Offset + f.After.Length > row.Length || !row.AsSpan(f.Offset, f.After.Length).SequenceEqual(f.After))
            throw new ReplayCheckException($"The bytes after the change are not in the row at offset {f.Offset}.");
        return Replace(row, f.Offset, f.After.Length, f.Before);
    }

    private static byte[] Replace(byte[] row, int offset, int length, byte[] with)
    {
        var result = new byte[row.Length - length + with.Length];
        row.AsSpan(0, offset).CopyTo(result);
        with.CopyTo(result, offset);
        row.AsSpan(offset + length).CopyTo(result.AsSpan(offset + with.Length));
        return result;
    }
}
