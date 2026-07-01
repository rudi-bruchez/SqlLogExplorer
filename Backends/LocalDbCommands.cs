namespace SqlLogExplorer.Backends;

/// <summary>Construit les arguments de l'utilitaire <c>sqllocaldb</c>.</summary>
public static class LocalDbCommands
{
    public static string Create(string instance) => $"create \"{instance}\"";
    public static string Start(string instance)  => $"start \"{instance}\"";
    public static string Stop(string instance)   => $"stop \"{instance}\"";
}
