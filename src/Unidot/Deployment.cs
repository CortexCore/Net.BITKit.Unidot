using System.Diagnostics;
using System.Text.Json;

namespace Unidot;

internal sealed record DeploymentEntry(string Target, string Backup, bool Existed, string? OriginalHash, string DeployedHash);
internal sealed record DeploymentManifest(string Player, string Timestamp, DeploymentEntry[] Files);

internal static class Deployment
{
    public static IReadOnlyDictionary<string, string> ReadBuildOutputs(Configuration config)
    {
        var path = Path.Combine(config.GeneratedDirectory, "build-report.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        if (json.RootElement.GetProperty("failures").GetArrayLength() > 0)
            throw new UnidotException("The last build contains failures; build a complete set before deploying.");
        return json.RootElement.GetProperty("outputs").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
    }

    public static void Audit(Configuration config, PlayerLayout player)
    {
        var outputs = ReadBuildOutputs(config);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(player.DataDirectory, "ScriptingAssemblies.json")));
        var registered = json.RootElement.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToHashSet(StringComparer.Ordinal);
        var rows = outputs.Select(item =>
        {
            var target = Path.Combine(player.ManagedDirectory, item.Key + ".dll");
            return new
            {
                Assembly = item.Key, Output = item.Value, Target = target,
                OutputExists = File.Exists(item.Value), TargetExists = File.Exists(target),
                Registered = registered.Contains(item.Key + ".dll"),
                IdentityMatches = File.Exists(target) && AssemblyMetadata.Identity(item.Value) == AssemblyMetadata.Identity(target),
                Dependencies = AssemblyMetadata.References(item.Value)
            };
        }).ToArray();
        ProjectGenerator.WriteIfChanged(Path.Combine(config.GeneratedDirectory, "deployment-audit.json"), JsonSerializer.Serialize(rows, Configuration.JsonOptions));
        foreach (var row in rows.Where(r => !r.TargetExists || !r.Registered || !r.IdentityMatches))
            Console.WriteLine($"{row.Assembly}: file={row.TargetExists}, registered={row.Registered}, identity={row.IdentityMatches}");
        Console.WriteLine($"Audit: {rows.Length} outputs; {rows.Count(r => !r.TargetExists)} missing Player files, {rows.Count(r => !r.Registered)} unregistered, {rows.Count(r => r.TargetExists && !r.IdentityMatches)} identity mismatches.");
    }

    public static string? Deploy(Configuration config, PlayerLayout player, IReadOnlyDictionary<string, string> outputs)
    {
        if (PlayerRuntime.IsRunning(player.Executable)) throw new UnidotException("Player is running. Close it before deployment, or compile with --no-deploy.");
        var hashes = new ContentHashes();
        var changes = new List<(string Source, string Target)>();
        foreach (var (name, source) in outputs)
        {
            var target = Path.Combine(player.ManagedDirectory, name + ".dll");
            if (!File.Exists(target)) throw new UnidotException($"Cannot deploy a new assembly {name}; it must be registered by a base Unity Player build.");
            if (AssemblyMetadata.Identity(source) != AssemblyMetadata.Identity(target)) throw new UnidotException($"Assembly name/version/signing identity mismatch: {name}");
            if (hashes.Get(source) != hashes.Get(target)) changes.Add((source, target));
            var pdb = Path.ChangeExtension(source, ".pdb");
            var targetPdb = Path.ChangeExtension(target, ".pdb");
            if (File.Exists(pdb) && (!File.Exists(targetPdb) || hashes.Get(pdb) != hashes.Get(targetPdb))) changes.Add((pdb, targetPdb));
        }
        if (changes.Count == 0) { Console.WriteLine("[deploy] Player already has these outputs."); return null; }
        // Check every destination before changing anything. Windows denies write access to loaded assemblies.
        foreach (var (_, target) in changes.Where(c => File.Exists(c.Target)))
            using (new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        var directory = Path.Combine(config.GeneratedDirectory, "backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var entries = new List<DeploymentEntry>();
        foreach (var (source, target) in changes)
        {
            var backup = Path.Combine(directory, Path.GetFileName(target));
            var exists = File.Exists(target);
            var oldHash = exists ? hashes.Get(target) : null;
            if (exists)
            {
                File.Copy(target, backup, false);
                if (hashes.Get(backup) != oldHash) throw new UnidotException($"Backup verification failed: {target}");
            }
            entries.Add(new(target, backup, exists, oldHash, hashes.Get(source)));
        }
        var manifest = new DeploymentManifest(player.Executable, DateTime.UtcNow.ToString("O"), entries.ToArray());
        var manifestPath = Path.Combine(directory, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, Configuration.JsonOptions));
        var completed = new List<DeploymentEntry>();
        try
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Existed && hashes.Get(entry.Target) != entry.OriginalHash) throw new UnidotException($"Player file changed during deployment: {entry.Target}");
                completed.Add(entry);
                File.Copy(changes[i].Source, entry.Target, true);
                if (hashes.Get(entry.Target) != entry.DeployedHash) throw new UnidotException($"Deployment verification failed: {entry.Target}");
            }
        }
        catch
        {
            foreach (var entry in completed.AsEnumerable().Reverse())
                if (entry.Existed) File.Copy(entry.Backup, entry.Target, true);
                else File.Delete(entry.Target);
            throw;
        }
        Console.WriteLine($"[deploy] {outputs.Count} assemblies. Backup: {manifestPath}");
        return manifestPath;
    }

    public static void Restore(Configuration config, PlayerLayout player, string? requested)
    {
        Directory.CreateDirectory(config.GeneratedDirectory);
        using var buildLock = BuildEngine.AcquireLock(config.GeneratedDirectory);
        if (PlayerRuntime.IsRunning(player.Executable)) throw new UnidotException("Close the Player before restoring a backup.");
        var backups = Path.Combine(config.GeneratedDirectory, "backups");
        var path = requested is null ? (Directory.Exists(backups) ? Directory.EnumerateFiles(backups, "manifest.json", SearchOption.AllDirectories).OrderDescending(StringComparer.Ordinal).FirstOrDefault() : null) :
            Path.GetFullPath(requested, config.Directory);
        if (path is null || !File.Exists(path)) throw new UnidotException("No deployment backup found.");
        var manifest = JsonSerializer.Deserialize<DeploymentManifest>(File.ReadAllText(path), Configuration.JsonOptions) ?? throw new UnidotException("Invalid backup manifest.");
        if (!PathComparer.Instance.Equals(manifest.Player, player.Executable)) throw new UnidotException("Backup belongs to a different Player.");
        var hashes = new ContentHashes();
        foreach (var entry in manifest.Files)
        {
            if (!PathComparer.Instance.Equals(Path.GetDirectoryName(Path.GetFullPath(entry.Target)), player.ManagedDirectory))
                throw new UnidotException("Backup contains a destination outside Player Managed.");
            if (!File.Exists(entry.Target) || hashes.Get(entry.Target) != entry.DeployedHash)
                throw new UnidotException($"Player file has changed since deployment: {entry.Target}. Restore refused.");
            if (entry.Existed && (!File.Exists(entry.Backup) || hashes.Get(entry.Backup) != entry.OriginalHash))
                throw new UnidotException($"Backup verification failed: {entry.Backup}");
            using (new FileStream(entry.Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        foreach (var entry in manifest.Files)
            if (entry.Existed) File.Copy(entry.Backup, entry.Target, true);
            else File.Delete(entry.Target);
        Console.WriteLine($"Restored {manifest.Files.Length} files from {path}.");
    }
}

internal static class PlayerRuntime
{
    public static bool IsRunning(string executable)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                try { if (PathComparer.Instance.Equals(process.MainModule?.FileName, executable)) return true; }
                catch (System.ComponentModel.Win32Exception) { return true; }
                catch (InvalidOperationException) { }
            }
        }
        return false;
    }

}
