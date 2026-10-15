using System;
using System.Collections.Generic;
using System.Linq;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Decoding;

/// <summary>Issue de la transaction d'un enregistrement (spec03 §7.3).</summary>
public enum TxOutcome { Committed, RolledBack, PartlyRolledBack, Unknown }

/// <summary>Image complète de la ligne d'où part le rejeu (spec03 §7.2).</summary>
public enum AnchorKind { None, Insert, Delete, CurrentRow }

/// <summary>Before et After sont les images complètes de la ligne autour de l'enregistrement choisi, ou null si le rejeu a échoué ; Status est affiché tel quel.</summary>
public sealed record ReplayResult(byte[]? Before, byte[]? After, AnchorKind Anchor, string? AnchorLsn, string Status, bool IncludesUnknownOutcome);

/// <summary>Rejeu d'une chaîne d'enregistrements d'une ligne, en avant depuis un insert ou en arrière depuis un delete ou la ligne courante (spec03 §7.2 à §7.4). Pur : ne lit rien.</summary>
public static class RowReplayer
{
    private const string Insert = "LOP_INSERT_ROWS";
    private const string Delete = "LOP_DELETE_ROWS";
    private const string NoImage = "no full image of this row available";

    private sealed record Step(LogRecord Record, TxOutcome Outcome);

    /// <summary>Échec d'une direction ; Specific est faux pour « aucune ancre », qui cède la place à tout autre échec.</summary>
    private sealed record Failure(string Status, bool Specific = true) : Attempt;

    private abstract record Attempt;

    private sealed record Success(byte[] Before, byte[] After, AnchorKind Anchor, string? AnchorLsn, string Status, bool Unknown) : Attempt;

    /// <summary>chain : les enregistrements de données de cette ligne (insert, delete, modify), dans l'ordre des LSN ;
    /// les compensations n'y sont pas (elles n'ont pas de clé, spec03 §7.1). outcomes : par Transaction ID ;
    /// une transaction absente du dictionnaire, ou un TransactionId null, vaut Unknown.
    /// currentRow : image de l'ancre 3 (après le dernier enregistrement de chain), ou null.</summary>
    public static ReplayResult Replay(IReadOnlyList<LogRecord> chain, string selectedLsn,
        IReadOnlyDictionary<string, TxOutcome> outcomes, IReadOnlySet<string> adrReverts, byte[]? currentRow = null)
    {
        TxOutcome OutcomeOf(LogRecord r) =>
            r.TransactionId is not null && outcomes.TryGetValue(r.TransactionId, out var o) ? o : TxOutcome.Unknown;

        var selected = chain.FirstOrDefault(r => r.Lsn == selectedLsn);
        if (selected is null || selected.Operation is not ("LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS"))
            return Fail(NoImage);

        // Une révocation ADR n'importe où dans la chaîne, avant ou après l'enregistrement choisi, arrête tout rejeu (§7.3).
        var revert = chain.FirstOrDefault(r => r.TransactionId is not null && adrReverts.Contains(r.TransactionId));
        if (revert is not null) return Fail(Partial(revert));

        // Les compensations ne sont jamais dans une chaîne ; une transaction annulée n'y compte pas, sauf celle de l'enregistrement choisi, jusqu'à lui.
        var selectedOutcome = OutcomeOf(selected);
        var steps = new List<Step>();
        var index = -1;
        foreach (var r in chain)
        {
            if (r.IsCompensation) continue;
            var o = OutcomeOf(r);
            var inSelectedTx = selectedOutcome == TxOutcome.RolledBack && r.TransactionId == selected.TransactionId && index < 0;
            if (o == TxOutcome.RolledBack && !inSelectedTx) continue;
            if (r.Lsn == selectedLsn) index = steps.Count;
            steps.Add(new Step(r, o));
        }

        var forward = Forwards(steps, index);
        if (forward is Success f) return ToResult(f);

        // Un enregistrement annulé n'a pas de suite visible dans la chaîne : l'image après lui ne se retrouve pas à rebours.
        var rolledBack = selectedOutcome == TxOutcome.RolledBack;
        var backward = rolledBack ? new Failure(NoImage, false) : Backwards(steps, index, currentRow);
        if (backward is Success b) return ToResult(b);

        // Un échec précis l'emporte sur « pas d'image » ; à précision égale, l'avant.
        var precise = new[] { forward, backward }.OfType<Failure>().FirstOrDefault(x => x.Specific);
        if (precise is not null) return Fail(precise.Status);
        return Fail(rolledBack ? "rollback in the chain, rebuild forwards only" : NoImage);
    }

    private static ReplayResult Fail(string status) => new(null, null, AnchorKind.None, null, status, false);

    private static ReplayResult ToResult(Success s) => new ReplayResult(s.Before, s.After, s.Anchor, s.AnchorLsn, s.Status, s.Unknown);

    private static string Partial(LogRecord r) => $"partial rollback at LSN {r.Lsn}, not replayed";

    private static bool IsForwardingStub(byte[] row) => row.Length > 0 && (row[0] >> 1 & 7) == 2;

    private static Attempt Forwards(List<Step> steps, int index)
    {
        // Ancre 1 : le dernier insert avant l'enregistrement choisi, sans delete entre les deux.
        var a = -1;
        for (var i = index - 1; i >= 0; i--)
        {
            if (steps[i].Record.Operation == Delete) break;
            if (steps[i].Record.Operation == Insert) { a = i; break; }
        }
        if (a < 0) return new Failure(NoImage, false);

        var anchor = steps[a];
        if (anchor.Outcome == TxOutcome.PartlyRolledBack) return new Failure(Partial(anchor.Record));
        var row = anchor.Record.RowLogContents0;
        if (row is null || row.Length == 0) return new Failure(NoImage, false);
        if (IsForwardingStub(row)) return new Failure("row forwarded, not followed");

        var unknown = anchor.Outcome == TxOutcome.Unknown;
        var before = row;
        for (var i = a + 1; i <= index; i++)
        {
            var step = steps[i];
            if (step.Outcome == TxOutcome.PartlyRolledBack) return new Failure(Partial(step.Record));
            var fragments = ModifyDecoder.Decode(step.Record);
            if (fragments is null) return new Failure($"modification not decodable at LSN {step.Record.Lsn}");
            before = row;
            try
            {
                foreach (var f in fragments) row = ModifyDecoder.ApplyForward(row, f);
            }
            catch (ReplayCheckException)
            {
                return new Failure($"chain broken at LSN {step.Record.Lsn}");
            }
            if (IsForwardingStub(row)) return new Failure("row forwarded, not followed");
            unknown |= step.Outcome == TxOutcome.Unknown;
        }
        return new Success(before, row, AnchorKind.Insert, anchor.Record.Lsn, $"rebuilt forwards from INSERT at LSN {anchor.Record.Lsn}", unknown);
    }

    private static Attempt Backwards(List<Step> steps, int index, byte[]? currentRow)
    {
        // Ancre 2 : le premier delete après l'enregistrement choisi, sans insert entre les deux ; un insert d'abord ferme la voie, même à la ligne courante.
        var end = steps.Count;   // la dernière marche à défaire est end - 1
        var kind = AnchorKind.CurrentRow;
        string? anchorLsn = null;
        byte[]? row = currentRow;
        var unknown = false;
        for (var i = index + 1; i < steps.Count; i++)
        {
            var op = steps[i].Record.Operation;
            if (op == Insert) return new Failure(NoImage, false);
            if (op != Delete) continue;
            if (steps[i].Outcome == TxOutcome.PartlyRolledBack) return new Failure(Partial(steps[i].Record));
            end = i;
            kind = AnchorKind.Delete;
            anchorLsn = steps[i].Record.Lsn;
            row = steps[i].Record.RowLogContents0;
            unknown = steps[i].Outcome == TxOutcome.Unknown;
            break;
        }
        if (row is null || row.Length == 0) return new Failure(NoImage, false);
        if (IsForwardingStub(row)) return new Failure("row forwarded, not followed");

        var after = row;
        for (var i = end - 1; i >= index; i--)
        {
            var step = steps[i];
            if (step.Outcome == TxOutcome.PartlyRolledBack) return new Failure(Partial(step.Record));
            var fragments = ModifyDecoder.Decode(step.Record);
            if (fragments is null) return new Failure($"modification not decodable at LSN {step.Record.Lsn}");
            after = row;
            try
            {
                foreach (var f in Enumerable.Reverse(fragments)) row = ModifyDecoder.ApplyBackward(row, f);
            }
            catch (ReplayCheckException)
            {
                return new Failure($"chain broken at LSN {step.Record.Lsn}");
            }
            if (IsForwardingStub(row)) return new Failure("row forwarded, not followed");
            unknown |= step.Outcome == TxOutcome.Unknown;
        }
        var status = kind == AnchorKind.Delete ? $"rebuilt backwards from DELETE at LSN {anchorLsn}" : "rebuilt backwards from the current row";
        return new Success(row, after, kind, anchorLsn, status, unknown);
    }

    /// <summary>Classe les transactions d'après leurs enregistrements (LOP_COMMIT_XACT, LOP_ABORT_XACT, compensations, LOP_CTR_NEST_ABORT).</summary>
    public static IReadOnlyDictionary<string, TxOutcome> Classify(IEnumerable<LogRecord> transactionRecords) =>
        Summarize(transactionRecords).ToDictionary(t => t.Key, t => t.Value switch
        {
            { Abort: true } => TxOutcome.RolledBack,
            { Commit: true, Compensation: false, Nest: false } => TxOutcome.Committed,
            { Commit: true } => TxOutcome.PartlyRolledBack,
            _ => TxOutcome.Unknown,
        });

    /// <summary>Transactions partiellement annulées par une révocation ADR : LOP_CTR_NEST_ABORT sans aucun enregistrement de compensation (spec03 §7.3).</summary>
    public static IReadOnlySet<string> AdrReverts(IEnumerable<LogRecord> transactionRecords) =>
        Summarize(transactionRecords)
            .Where(t => t.Value is { Commit: true, Abort: false, Nest: true, Compensation: false })
            .Select(t => t.Key)
            .ToHashSet();

    private sealed record Flags(bool Commit = false, bool Abort = false, bool Compensation = false, bool Nest = false);

    private static Dictionary<string, Flags> Summarize(IEnumerable<LogRecord> records)
    {
        var flags = new Dictionary<string, Flags>();
        foreach (var r in records)
        {
            if (r.TransactionId is null) continue;
            var f = flags.GetValueOrDefault(r.TransactionId, new Flags());
            flags[r.TransactionId] = r.Operation switch
            {
                "LOP_COMMIT_XACT" => f with { Commit = true },
                "LOP_ABORT_XACT" => f with { Abort = true },
                "LOP_CTR_NEST_ABORT" => f with { Nest = true },
                // LOP_CTR_NEST_ABORT et LOP_LOCK_XACT portent aussi « COMPENSATION » (fixture ADR) : seule la compensation d'une ligne compte.
                "LOP_INSERT_ROWS" or "LOP_DELETE_ROWS" or "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS" => r.IsCompensation ? f with { Compensation = true } : f,
                _ => f,
            };
        }
        return flags;
    }
}
