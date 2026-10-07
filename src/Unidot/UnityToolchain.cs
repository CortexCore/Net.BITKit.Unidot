using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Unidot;

internal sealed record UnityToolchain(string DataDirectory, string Dotnet, string Compiler, string Version)
{
    public string[] Generators => new[] { "Unity.SourceGenerators.dll", "Unity.Properties.SourceGenerator.dll" }
        .Select(name => Path.Combine(DataDirectory, "Tools", "Unity.SourceGenerators", name)).Where(File.Exists).ToArray();

    public static UnityToolchain Discover(Configuration config)
    {
        var versionFile = Path.Combine(config.UnityProject, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(versionFile)) throw new UnidotException($"Not a Unity project: {config.UnityProject}");
        var match = Regex.Match(File.ReadAllText(versionFile), @"m_EditorVersion:\s*(\S+)");
        if (!match.Success) throw new UnidotException("Cannot read Unity project version.");
        var version = match.Groups[1].Value;
        var explicitPath = !string.IsNullOrWhiteSpace(config.UnityEditor) ? config.UnityEditor :
            Environment.GetEnvironmentVariable("UNIDOT_UNITY_EDITOR") ?? Environment.GetEnvironmentVariable("UNITY_EDITOR");
        var candidates = explicitPath is not null ? new[] { explicitPath } : new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor", version),
            Path.Combine(@"C:\Program Files\Unity\Hub\Editor", version)
        };
        foreach (var candidate in candidates.Distinct(PathComparer.Instance))
        {
            var root = Path.GetFullPath(candidate);
            if (File.Exists(root)) root = Path.GetDirectoryName(root)!;
            var options = new[] { root, Path.Combine(root, "Data"), Path.Combine(root, "Editor", "Data") };
            foreach (var data in options)
            {
                var compiler = Path.Combine(data, "DotNetSdkRoslyn", "csc.dll");
                var dotnet = Path.Combine(data, "NetCoreRuntime", "dotnet.exe");
                if (!File.Exists(compiler) || !File.Exists(dotnet)) continue;
                var editor = Path.Combine(Path.GetDirectoryName(data)!, "Unity.exe");
                if (File.Exists(editor))
                {
                    var productVersion = FileVersionInfo.GetVersionInfo(editor).ProductVersion;
                    if (!string.IsNullOrEmpty(productVersion) && !productVersion.StartsWith(version, StringComparison.Ordinal))
                        throw new UnidotException($"Unity installation version {productVersion} does not match project version {version}.");
                }
                return new(Path.GetFullPath(data), dotnet, compiler, version);
            }
        }
        throw new UnidotException($"Cannot locate Unity {version} Roslyn toolchain. Set UNIDOT_UNITY_EDITOR or use --unity-editor <path>.");
    }
}

internal static class PathComparer
{
    public static StringComparer Instance { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static string PhysicalDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        return Path.GetFullPath(info.ResolveLinkTarget(true)?.FullName ?? info.FullName);
    }
}
