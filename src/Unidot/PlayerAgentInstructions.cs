using System.Text;

namespace Unidot;

internal static class PlayerAgentInstructions
{
    internal const string StartMarker = "<!-- unidot:agents:start -->";
    internal const string EndMarker = "<!-- unidot:agents:end -->";

    public static bool Ensure(Configuration config, PlayerLayout player)
    {
        var root = Path.GetDirectoryName(player.Executable)!;
        var path = Path.Combine(root, "AGENTS.md");
        var file = new FileInfo(path);
        if (file.LinkTarget is not null)
        {
            Console.Error.WriteLine($"Warning: linked AGENTS.md preserved; Unidot will not update its external target: {path}");
            return false;
        }
        var temporary = Path.Combine(root, ".unidot-agents-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var stateDirectory = Path.Combine(root, ".unidot");
            Directory.CreateDirectory(stateDirectory);
            using var instructionsLock = new FileStream(Path.Combine(stateDirectory, "agents.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var original = File.Exists(path) ? File.ReadAllBytes(path) : [];
            var (encoding, prefixLength) = DetectEncoding(original);
            var text = encoding.GetString(original, prefixLength, original.Length - prefixLength);
            if (text.Contains('\0'))
            {
                Console.Error.WriteLine($"Warning: unsupported AGENTS.md text encoding in {path}; file preserved.");
                return false;
            }
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var block = StartMarker + newline + Render(config, player).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal).TrimEnd('\r', '\n') + newline + EndMarker;
            var start = text.IndexOf(StartMarker, StringComparison.Ordinal);
            var end = text.IndexOf(EndMarker, StringComparison.Ordinal);
            string updated;
            if (start < 0 && end < 0)
            {
                updated = text.Length == 0 ? block + newline : text + (text.EndsWith('\n') ? "" : newline) + newline + block + newline;
            }
            else if (start < 0 || end < start || text.IndexOf(StartMarker, start + StartMarker.Length, StringComparison.Ordinal) >= 0 ||
                     text.IndexOf(EndMarker, end + EndMarker.Length, StringComparison.Ordinal) >= 0)
            {
                Console.Error.WriteLine($"Warning: malformed Unidot markers in {path}; file preserved. Fix the managed block to enable updates.");
                return false;
            }
            else updated = text[..start] + block + text[(end + EndMarker.Length)..];
            if (updated == text) return false;
            var body = encoding.GetBytes(updated);
            var bytes = original.Take(prefixLength).Concat(body).ToArray();
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            Console.WriteLine($"Updated Player agent instructions: {path}");
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            Console.Error.WriteLine($"Warning: could not update Player AGENTS.md; existing instructions preserved: {error.Message}");
            return false;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Render(Configuration config, PlayerLayout player)
    {
        using var stream = typeof(PlayerAgentInstructions).Assembly.GetManifestResourceStream("Unidot.PlayerAgentInstructions.md")
            ?? throw new UnidotException("Embedded Player agent instructions are missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var root = Path.GetDirectoryName(player.Executable)!;
        var configuration = config.FilePath ?? Path.Combine(config.Directory, "unidot.json");
        var suffix = PathComparer.Instance.Equals(Path.GetDirectoryName(configuration), root) ? "" : " --config \"" + configuration + "\"";
        var source = config.SourcePath ?? (config.BuildRoots.Length > 0 ? string.Join("; ", config.BuildRoots) : Path.Combine(config.UnityProject, "Assets"));
        return reader.ReadToEnd().Replace("{{PLAYER}}", player.Executable, StringComparison.Ordinal)
            .Replace("{{CONFIG}}", configuration, StringComparison.Ordinal).Replace("{{SOURCE}}", source, StringComparison.Ordinal)
            .Replace("{{GENERATED}}", config.GeneratedDirectory, StringComparison.Ordinal).Replace("{{CONFIG_ARGUMENT}}", suffix, StringComparison.Ordinal);
    }

    private static (Encoding Encoding, int PrefixLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0) return (new UTF32Encoding(false, false, true), 4);
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF) return (new UTF32Encoding(true, false, true), 4);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return (new UTF8Encoding(false, true), 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return (new UnicodeEncoding(false, false, true), 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return (new UnicodeEncoding(true, false, true), 2);
        return (new UTF8Encoding(false, true), 0);
    }
}
