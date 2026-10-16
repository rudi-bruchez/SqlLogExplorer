using System.Collections.Generic;
using SqlLogExplorer.Decoding;
using SqlLogExplorer.Models;

namespace SqlLogExplorer.Services;

/// <summary>Une colonne d'un modify, avant et après ; valeurs null quand le fragment seul ne les donne pas (spec03 §6.1).
/// Changed null : on ne sait pas, une valeur d'un côté n'a pas été lue et ses octets ne tranchent pas (spec03 §1.2).</summary>
public sealed record ColumnChange(string Column, object? Before, object? After, bool? Changed);

/// <summary>Une valeur décodée et la note que RowDecoder y a posée (spec03 §1.2). HasValue faux : la valeur n'a pas été lue (hors ligne,
/// éparse, octets qui ne vont pas au type) et la note la remplace ; vrai : la valeur vaut, la note l'explique (défaut d'une colonne
/// ajoutée après l'écriture de la ligne, octets bruts d'un type non décodé).</summary>
public sealed record NotedValue(object? Value, bool HasValue, string Note);

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
