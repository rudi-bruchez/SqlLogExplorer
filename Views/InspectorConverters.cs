using Avalonia.Data.Converters;
using Avalonia.Media;
using SqlLogExplorer.Decoding;
using SqlLogExplorer.ViewModels;

namespace SqlLogExplorer.Views;

/// <summary>Couleurs de l'onglet "Bytes" par rôle de champ, surlignage des colonnes changées, texte des valeurs (spec03 §9).</summary>
public static class InspectorConverters
{
    // Teintes pâles, lisibles avec un texte sombre ; un contenu non décodé (rôle null) reste gris très pâle ; le texte est noir quel que soit le thème.
    public static readonly IValueConverter RoleBrush = new FuncValueConverter<FieldRole?, IBrush>(role => role switch
    {
        null => new SolidColorBrush(Color.Parse("#F4F4F4")),
        FieldRole.StatusA or FieldRole.StatusB => new SolidColorBrush(Color.Parse("#D0D0D0")),
        FieldRole.FixedEnd or FieldRole.ColumnCount or FieldRole.VariableCount => new SolidColorBrush(Color.Parse("#E8D8B0")),
        FieldRole.NullBitmap => new SolidColorBrush(Color.Parse("#E0C8E8")),
        FieldRole.VariableOffset => new SolidColorBrush(Color.Parse("#F0E0A0")),
        FieldRole.FixedColumn => new SolidColorBrush(Color.Parse("#B8E0B8")),
        FieldRole.VariableColumn => new SolidColorBrush(Color.Parse("#B8D0F0")),
        FieldRole.LobPointer or FieldRole.VersionTag => new SolidColorBrush(Color.Parse("#C8E8E8")),
        FieldRole.Malformed => new SolidColorBrush(Color.Parse("#F0A0A0")),
        _ => new SolidColorBrush(Color.Parse("#E8E8E8")),   // octets inutilisés, colonne supprimée, fin inconnue
    });

    public static readonly IValueConverter ChangedBrush = new FuncValueConverter<bool, IBrush>(changed =>
        changed ? new SolidColorBrush(Color.Parse("#60FFD54F")) : Brushes.Transparent);

    public static readonly IValueConverter Value = new FuncValueConverter<object?, string>(InspectorViewModel.ValueText);
}
