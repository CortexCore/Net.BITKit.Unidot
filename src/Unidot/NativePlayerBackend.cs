using System.Diagnostics;
using System.Text.Json;

namespace Unidot;

internal sealed record PlayerLaunchOptions(string Backend = "exe", bool FallbackToExe = false, string? NativeHost = null,
    IReadOnlyDictionary<string, string>? Environment = null, bool InjectOriginal = false);

internal static class NativePlayerBackend
{
    public static async Task<ProcessStartInfo> CreateAsync(Configuration config, PlayerLayout player, string log, string diagnostics, string[] arguments, string? hostOverride, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new UnidotException("Native Player backend requires Windows x64.");
        var host = hostOverride ?? config.NativeHost ?? Path.Combine(AppContext.BaseDirectory, "native", "Unidot.NativeHost.exe");
        if (!File.Exists(host)) throw new UnidotException($"Native Host not found: {host}. Install a distribution containing native/Unidot.NativeHost.exe.");
        if (Path.GetFileName(host).Equals("Unidot.NativeHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(host))!;
            var prepared = Path.Combine(player.RootDirectory, ".unidot", "native-host");
            Directory.CreateDirectory(prepared);
            foreach (var file in Directory.EnumerateFiles(sourceDirectory, "Unidot.NativeHost.*"))
            {
                var destination = Path.Combine(prepared, Path.GetFileName(file));
                if (!PathComparer.Instance.Equals(file, destination)) File.Copy(file, destination, true);
            }
            var mono = Path.Combine(player.RootDirectory, "MonoBleedingEdge");
            if (Directory.Exists(mono)) await PlayerSources.LinkAsync(mono, Path.Combine(prepared, "MonoBleedingEdge"), cancellationToken);
            // Older 2022.3 Players only export UnityMain, which derives data from the executable's name.
            await PlayerSources.LinkAsync(player.DataDirectory, Path.Combine(prepared, "Unidot.NativeHost_Data"), cancellationToken);
            host = Path.Combine(prepared, "Unidot.NativeHost.exe");
        }
        var start = new ProcessStartInfo(host) { WorkingDirectory = player.RootDirectory, UseShellExecute = false };
        start.ArgumentList.Add("--unity-player"); start.ArgumentList.Add(player.UnityPlayerLibrary);
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(player.DataDirectory);
        start.ArgumentList.Add("--diagnostics"); start.ArgumentList.Add(diagnostics);
        start.ArgumentList.Add("--"); start.ArgumentList.Add("-logFile"); start.ArgumentList.Add(log);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    public static bool CanFallback(string diagnostics, int exit)
    {
        if (exit != 110 || !File.Exists(diagnostics)) return false;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(diagnostics));
            return json.RootElement.TryGetProperty("protocol", out var protocol) && protocol.TryGetInt32(out var version) && version == 1 &&
                json.RootElement.TryGetProperty("unityEntered", out var entered) && entered.ValueKind == JsonValueKind.False;
        }
        catch (JsonException) { return false; }
        catch (KeyNotFoundException) { return false; }
    }

    public static string Describe(string diagnostics)
    {
        if (!File.Exists(diagnostics)) return "Native Host did not write a startup status.";
        using var json = JsonDocument.Parse(File.ReadAllText(diagnostics));
        return "phase=" + json.RootElement.GetProperty("phase").GetString() + "; " +
            (json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "");
    }
}
