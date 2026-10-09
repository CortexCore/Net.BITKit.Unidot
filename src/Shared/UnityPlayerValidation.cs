using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace Unidot;

internal sealed record UnityEngineVersion(int Major, int Minor, int Patch, string Name)
{
    public bool SupportsMcp => Major == 2022;
}

// Shared by the CLI and its child NativeHost: resource packing is not a backend/version marker.
internal static class UnityPlayerValidation
{
    public static UnityEngineVersion ParseVersion(string? productVersion)
    {
        var match = Regex.Match(productVersion ?? "", @"(?<!\d)(?<major>\d{4})\.(?<minor>\d+)\.(?<patch>\d+)[abfp]\d+(?:c\d+)?(?=$|[\s_(])");
        if (!match.Success) throw new ArgumentException("Cannot identify the Unity engine version from UnityPlayer.dll ProductVersion: " + productVersion);
        return new(int.Parse(match.Groups["major"].Value), int.Parse(match.Groups["minor"].Value),
            int.Parse(match.Groups["patch"].Value), match.Value);
    }

    public static UnityEngineVersion ReadVersion(string library)
    {
        if (!File.Exists(library)) throw new FileNotFoundException("UnityPlayer.dll not found.", library);
        using var stream = File.OpenRead(library);
        using var pe = new PEReader(stream);
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64)
            throw new PlatformNotSupportedException("The UnityPlayer.dll must be a Windows x64 engine.");
        return ParseVersion(FileVersionInfo.GetVersionInfo(library).ProductVersion);
    }

    public static bool HasMonoLayout(string root, string data) => Directory.Exists(data) &&
        File.Exists(Path.Combine(data, "Managed", "mscorlib.dll")) &&
        Directory.Exists(Path.Combine(root, "MonoBleedingEdge")) && !File.Exists(Path.Combine(root, "GameAssembly.dll"));

    public static void RequireMonoLayout(string root, string data)
    {
        if (!HasMonoLayout(root, data)) throw new InvalidOperationException(
            "Mono execution requires a data directory with Managed/mscorlib.dll and MonoBleedingEdge; GameAssembly.dll indicates IL2CPP. " + data);
    }
}
