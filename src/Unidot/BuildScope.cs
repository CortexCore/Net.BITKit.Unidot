namespace Unidot;

internal static class BuildScope
{
    public static bool Contains(Configuration config, AssemblyNode node)
    {
        if (config.SourcePath is not null && !node.PlayerSource) return false;
        if (config.BuildRoots.Length == 0) return true;
        if (node.DefinitionPath.Length == 0) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var directory = Path.GetDirectoryName(node.DefinitionPath)!;
        return config.BuildRoots.Any(root =>
        {
            var physical = PathComparer.PhysicalDirectory(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return directory.Equals(physical, comparison) || directory.StartsWith(physical + Path.DirectorySeparatorChar, comparison);
        });
    }
}
