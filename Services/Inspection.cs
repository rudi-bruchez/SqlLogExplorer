using System.Collections.Generic;
using SqlLogExplorer.Decoding;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Services;

/// <summary>Une colonne d'un modify, avant et après ; valeurs null quand le fragment seul ne les donne pas (spec03 §6.1).</summary>
public sealed record ColumnChange(string Column, object? Before, object? After, bool Changed);

/// <summary>Ce que l'inspecteur montre d'un enregistrement (spec03 §9).
/// Row : image d'un insert ou d'un delete ; Before et After : lignes complètes d'un modify quand le rejeu a réussi ;
/// Changes : depuis Before et After, sinon depuis le fragment seul ; SchemaMessage, Label et Warning sont affichés tels quels.</summary>
public sealed record Inspection(
    LogRecord Record,
    DecodedRow? Row,
    DecodedRow? Before, DecodedRow? After,
    IReadOnlyList<ColumnChange> Changes,
    string? ReplayStatus, TxOutcome? Outcome, bool IncludesUnknownOutcome,
    string? SchemaMessage,
    string? Label,
    string? Warning,
    bool CanRebuildFromCurrentRow);
