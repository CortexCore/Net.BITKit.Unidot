using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiffPlex;

namespace Unidot;

internal sealed record WorkspaceResolution(string Target, string? BaseHash, string? MainHash, string? WorkspaceHash, string Resolved);
internal sealed record WorkspaceApplyState(int Version, string WorkspaceId, Dictionary<string, string?> Baselines, Dictionary<string, WorkspaceResolution> Resolutions);
internal sealed record WorkspaceApplyEntry(string Path, string Target, string? BaseHash, string? MainHash, string? WorkspaceHash, string Action, string? Conflict);
internal sealed record WorkspaceApplyResult(int Applied, int AlreadyMatched, int Conflicts, bool DryRun, string Report, string? Backup);
internal sealed record WorkspaceApplyBackup(string Path, string Target, string? BeforeHash, string? AfterHash, string? Backup, long ModifiedTicks, FileAttributes Attributes);

internal static class WorkspaceMerge
{
    private const string StateName = "workspace-apply.json";

    internal static WorkspaceApplyResult Apply(string root, bool dryRun, CancellationToken token, Action<int>? beforeWrite = null)
    {
        root = Path.GetFullPath(root);
        using var management = WorkspaceManager.AcquireNameLock(root);
        var manifest = WorkspaceManager.Read(root);
        using var buildLock = BuildEngine.AcquireLock(Path.Combine(root, ".unidot"));
        var state = ReadState(manifest);
        var entries = Plan(manifest, state, token);
        var report = Path.Combine(root, ".unidot", "merge-report.json");
        var conflicts = entries.Count(e => e.Conflict is not null);
        WriteReport(report, manifest, entries, conflicts > 0 ? "conflicted" : "ready", dryRun, null);
        if (conflicts > 0)
        {
            var diffPath = Path.Combine(root, ".unidot", "merge-conflicts.diff");
            var diff = new StringBuilder();
            foreach (var entry in entries.Where(e => e.Conflict is not null))
            {
                token.ThrowIfCancellationRequested();
                diff.AppendLine($"CONFLICT {entry.Path}: {entry.Conflict}");
                diff.AppendLine($"Base: {entry.BaseHash ?? "absent"}; main: {entry.MainHash ?? "absent"}; workspace: {entry.WorkspaceHash ?? "absent"}");
                diff.AppendLine(ConflictDiff(entry, SourceFile(root, entry.Path)));
            }
            AtomicWrite(diffPath, Encoding.UTF8.GetBytes(diff.ToString()));
            foreach (var line in diff.ToString().Split('\n').Take(100)) Console.Error.WriteLine(line.TrimEnd('\r'));
            Console.Error.WriteLine($"Apply blocked: {conflicts} conflicts. No source changes applied. Full diff: {diffPath}");
            Console.WriteLine("Merge report: " + report);
            return new(0, 0, conflicts, dryRun, report, null);
        }
        foreach (var entry in entries) Console.WriteLine($"{entry.Action} {entry.Path}");
        var changed = entries.Where(e => e.Action != "already-matched").ToArray();
        var matched = entries.Length - changed.Length;
        if (dryRun)
        {
            Console.WriteLine($"Apply dry run: {changed.Length} source changes, {matched} already matched; no source or merge baseline changes.");
            Console.WriteLine("Merge report: " + report);
            return new(0, matched, 0, true, report, null);
        }
        if (entries.Length == 0)
        {
            WriteReport(report, manifest, entries, "up-to-date", false, null);
            Console.WriteLine("Apply: no pending source changes.");
            return new(0, 0, 0, false, report, null);
        }
        using var sourceLocks = AcquireSourceLocks(entries.Select(e => e.Target));
        foreach (var entry in entries) VerifyInputs(manifest, entry, token);
        var directory = Path.Combine(root, ".unidot", "apply-backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        WorkspaceManager.RequireRealAncestors(directory);
        Directory.CreateDirectory(directory);
        var backups = new List<WorkspaceApplyBackup>();
        var staged = new Dictionary<string, byte[]>(PathComparer.Instance);
        var committed = new List<WorkspaceApplyBackup>();
        var createdDirectories = new List<string>();
        var transactionPath = Path.Combine(directory, "transaction.json");
        try
        {
            foreach (var entry in changed)
            {
                token.ThrowIfCancellationRequested();
                if (entry.WorkspaceHash is not null) staged.Add(entry.Path, ReadVerified(SourceFile(root, entry.Path), entry.WorkspaceHash));
                string? backup = null;
                var ticks = 0L; var attributes = FileAttributes.Normal;
                if (entry.MainHash is not null)
                {
                    backup = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Path))) + ".before";
                    var original = ReadVerified(entry.Target, entry.MainHash);
                    ticks = File.GetLastWriteTimeUtc(entry.Target).Ticks; attributes = File.GetAttributes(entry.Target);
                    using (new FileStream(entry.Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    var backupPath = Path.Combine(directory, backup.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!); File.WriteAllBytes(backupPath, original);
                }
                backups.Add(new(entry.Path, entry.Target, entry.MainHash, entry.WorkspaceHash, backup, ticks, attributes));
            }
            WriteTransaction("prepared");
            var index = 0;
            foreach (var entry in changed)
            {
                token.ThrowIfCancellationRequested();
                beforeWrite?.Invoke(index++);
                VerifyInputs(manifest, entry, token);
                var backup = backups.Single(b => b.Path == entry.Path);
                if (entry.WorkspaceHash is null) File.Delete(entry.Target);
                else
                {
                    CreateDirectories(Path.GetDirectoryName(entry.Target)!, createdDirectories);
                    ReplaceFile(entry.Target, staged[entry.Path], entry.MainHash is not null);
                }
                committed.Add(backup);
                VerifyHash(entry.Target, entry.WorkspaceHash);
            }
            // Catch edits to any input during earlier writes before committing the merge baseline.
            foreach (var entry in entries)
            {
                VerifyTarget(manifest, entry);
                VerifyHash(entry.Target, entry.WorkspaceHash);
                VerifyHash(SourceFile(root, entry.Path), entry.WorkspaceHash);
            }
            token.ThrowIfCancellationRequested();
            foreach (var entry in entries) { state.Baselines[entry.Path] = entry.WorkspaceHash; state.Resolutions.Remove(entry.Path); }
            WriteTransaction("source-written");
            SaveState(manifest, state);
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<string>();
            foreach (var backup in committed.AsEnumerable().Reverse())
            {
                try
                {
                    WorkspaceManager.RequireRealAncestors(backup.Target);
                    VerifyHash(backup.Target, backup.AfterHash);
                    if (backup.BeforeHash is null) File.Delete(backup.Target);
                    else
                    {
                        var bytes = ReadVerified(Path.Combine(directory, backup.Backup!.Replace('/', Path.DirectorySeparatorChar)), backup.BeforeHash);
                        ReplaceFile(backup.Target, bytes, backup.AfterHash is not null);
                        File.SetLastWriteTimeUtc(backup.Target, new DateTime(backup.ModifiedTicks, DateTimeKind.Utc));
                        File.SetAttributes(backup.Target, backup.Attributes);
                    }
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException or UnidotException) { rollbackErrors.Add(backup.Path + ": " + rollback.Message); }
            }
            foreach (var path in createdDirectories.AsEnumerable().Reverse())
                try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
                catch (IOException cleanup) { rollbackErrors.Add(path + ": " + cleanup.Message); }
            try { WriteTransaction(rollbackErrors.Count == 0 ? "rolled-back" : "rollback-incomplete"); WriteReport(report, manifest, entries, "failed", false, directory); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("Could not update apply report: " + logError.Message); }
            if (rollbackErrors.Count > 0) throw new UnidotException($"Apply failed: {error.Message}. Concurrent changes were preserved; manual recovery needed for {string.Join("; ", rollbackErrors)}. Backups: {directory}");
            Console.Error.WriteLine("Apply failed; source writes rolled back. Backups: " + directory);
            throw;
        }
        try { WriteTransaction("applied"); WriteReport(report, manifest, entries, "applied", false, directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("Apply committed, but report update failed: " + error.Message); }
        Console.WriteLine($"Applied {changed.Length} source changes; {matched} already matched. Merge baseline updated. Backups: {directory}");
        return new(changed.Length, matched, 0, false, report, directory);

        void WriteTransaction(string status) => AtomicWrite(transactionPath, JsonSerializer.SerializeToUtf8Bytes(new
        { WorkspaceId = manifest.Id, Status = status, Timestamp = DateTime.UtcNow.ToString("O"), Files = backups }, Configuration.JsonOptions));
    }

    internal static void Resolve(string root, string path, CancellationToken token)
    {
        root = Path.GetFullPath(root); path = Relative(path);
        using var management = WorkspaceManager.AcquireNameLock(root);
        var manifest = WorkspaceManager.Read(root);
        using var buildLock = BuildEngine.AcquireLock(Path.Combine(root, ".unidot"));
        var state = ReadState(manifest);
        var entry = Plan(manifest, state, token, path).SingleOrDefault(e => PathComparer.Instance.Equals(e.Path, path))
            ?? throw new UnidotException($"No workspace source change to resolve: {path}");
        if (entry.BaseHash == entry.WorkspaceHash && entry.MainHash == entry.WorkspaceHash) throw new UnidotException("No source change or conflict to resolve: " + path);
        using var sourceLocks = AcquireSourceLocks([entry.Target]);
        VerifyInputs(manifest, entry, token);
        if (entry.WorkspaceHash is not null && HasMarkers(ReadVerified(SourceFile(root, path), entry.WorkspaceHash)))
            throw new UnidotException($"Conflict markers remain in {path}. Edit the workspace file before resolving.");
        state.Resolutions[path] = new(entry.Target, entry.BaseHash, entry.MainHash, entry.WorkspaceHash, DateTime.UtcNow.ToString("O"));
        SaveState(manifest, state);
        Console.WriteLine($"Resolved {path}: main {entry.MainHash ?? "absent"} -> workspace {entry.WorkspaceHash ?? "deleted"}.");
        Console.WriteLine("Source has not been written. Run workspace apply; either file changing invalidates this resolution.");
    }

    private static WorkspaceApplyEntry[] Plan(WorkspaceManifest manifest, WorkspaceApplyState state, CancellationToken token, string? include = null)
    {
        var baseline = manifest.Sources.ToDictionary(s => Relative(s.Path), s => (string?)s.Hash, PathComparer.Instance);
        foreach (var entry in state.Baselines) baseline[entry.Key] = entry.Value;
        var workspace = WorkspaceManager.Snapshot(Path.Combine(manifest.Root, "Src"), token).ToDictionary(s => Relative(s.Path), s => s.Hash, PathComparer.Instance);
        var entries = new List<WorkspaceApplyEntry>();
        foreach (var path in baseline.Keys.Union(workspace.Keys, PathComparer.Instance).Order(PathComparer.Instance))
        {
            token.ThrowIfCancellationRequested();
            if (Excluded(path)) continue;
            var b = baseline.GetValueOrDefault(path); var w = workspace.GetValueOrDefault(path);
            if (b == w && !PathComparer.Instance.Equals(path, include) && !state.Resolutions.ContainsKey(path)) continue;
            var target = Target(manifest, path);
            var m = Hash(target);
            var action = w == m ? "already-matched" : w is null ? "delete" : m is null ? "add" : "modify";
            string? conflict = null;
            if (w != m && m != b)
            {
                var resolved = state.Resolutions.GetValueOrDefault(path);
                if (resolved is null || resolved.Target != target || resolved.BaseHash != b || resolved.MainHash != m || resolved.WorkspaceHash != w)
                    conflict = resolved is null ? "Main and workspace both changed." : "Resolution is stale; edit/resolve again against current files.";
            }
            if (w is not null && new FileInfo(SourceFile(manifest.Root, path)).Length <= 1048576 && HasMarkers(ReadVerified(SourceFile(manifest.Root, path), w)))
                conflict = "Conflict markers remain in the workspace file.";
            entries.Add(new(path, target, b, m, w, action, conflict));
        }
        return entries.ToArray();
    }

    private static string Relative(string path)
    {
        path = path.Replace('\\', '/');
        var parts = path.Split('/');
        if (path.Length == 0 || Path.IsPathRooted(path) || path.Contains(':') || parts.Any(p => p.Length == 0 || p is "." or ".." || p.IndexOfAny(['\0', '\r', '\n']) >= 0))
            throw new UnidotException("Source path must be relative to Src, without traversal, rooted paths or stream names.");
        return path;
    }

    private static bool Excluded(string path) => path.Split('/').Any(p => p is ".git" or ".unidot" or "bin" or "obj") ||
        new[] { ".dll", ".pdb", ".exe", ".nupkg" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string SourceFile(string root, string path)
    {
        var result = Path.GetFullPath(Relative(path), Path.Combine(root, "Src"));
        WorkspaceManager.RequireRealAncestors(result);
        return result;
    }

    private static string Target(WorkspaceManifest manifest, string path)
    {
        path = Relative(path);
        string target;
        if (manifest.SourceRoots is { Length: > 0 })
        {
            var route = manifest.SourceRoots.Where(r => r.Path.Length == 0 || PathComparer.Instance.Equals(path, r.Path) || path.StartsWith(r.Path + "/", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .OrderByDescending(r => r.Path.Length).FirstOrDefault() ?? throw new UnidotException("No source origin mapping for " + path);
            if (route.Path.Length > 0) Relative(route.Path);
            if (route.Path.Length > 0 && PathComparer.Instance.Equals(path, route.Path)) throw new UnidotException("Source path replaces a mapped directory root: " + path);
            if (!Path.IsPathFullyQualified(route.Target) || !Directory.Exists(route.Target)) throw new UnidotException("Source origin root is missing or invalid: " + route.Target);
            target = Path.GetFullPath(path[(route.Path.Length == 0 ? 0 : route.Path.Length + 1)..], route.Target);
            if (!WorkspaceManager.Within(target, route.Target)) throw new UnidotException("Source target escaped its origin root.");
        }
        else
        {
            // Version 0.5 manifests have no frozen link routes. Resolve the old Src mapping within its recorded discovery exclusions.
            target = Path.GetFullPath(manifest.SourceOrigin);
            target = ResolveDirectory(target);
            var parts = path.Split('/');
            foreach (var part in parts[..^1]) target = ResolveDirectory(Path.Combine(target, part));
            target = Path.Combine(target, parts[^1]);
            var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(Path.Combine(manifest.Root, "unidot.json")), Configuration.JsonOptions)!;
            if (!config.WorkspaceExcludedSources.Any(r => WorkspaceManager.Within(target, r))) throw new UnidotException("Legacy Src link target changed outside the recorded source roots. Recreate the workspace.");
        }
        if (WorkspaceManager.Within(target, manifest.Root)) throw new UnidotException("Apply destination overlaps its own workspace.");
        WorkspaceManager.RequireRealAncestors(target);
        if (Directory.Exists(target)) throw new UnidotException("Source destination is a directory, not a file: " + target);
        return target;
    }

    private static string ResolveDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        return info.LinkTarget is null ? info.FullName : info.ResolveLinkTarget(true)?.FullName ?? throw new UnidotException("Broken source origin link: " + path);
    }

    private static void VerifyTarget(WorkspaceManifest manifest, WorkspaceApplyEntry entry)
    {
        if (!PathComparer.Instance.Equals(Target(manifest, entry.Path), entry.Target)) throw new UnidotException("Source origin mapping changed: " + entry.Path);
    }
    private static void VerifyInputs(WorkspaceManifest manifest, WorkspaceApplyEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); VerifyTarget(manifest, entry);
        VerifyHash(entry.Target, entry.MainHash); VerifyHash(SourceFile(manifest.Root, entry.Path), entry.WorkspaceHash);
    }
    private static string? Hash(string path)
    {
        WorkspaceManager.RequireRealAncestors(path);
        if (!File.Exists(path)) return null;
        using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file));
    }
    private static void VerifyHash(string path, string? expected)
    {
        if (Hash(path) != expected) throw new UnidotException("Source file changed since merge preflight: " + path);
    }
    private static byte[] ReadVerified(string path, string expected)
    {
        WorkspaceManager.RequireRealAncestors(path);
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != expected) throw new UnidotException("Source file changed since merge preflight: " + path);
        return bytes;
    }

    private static WorkspaceApplyState ReadState(WorkspaceManifest manifest)
    {
        var path = Path.Combine(manifest.Root, ".unidot", StateName); WorkspaceManager.RequireRealAncestors(path);
        if (!File.Exists(path)) return new(1, manifest.Id, new(PathComparer.Instance), new(PathComparer.Instance));
        var state = JsonSerializer.Deserialize<WorkspaceApplyState>(File.ReadAllText(path), Configuration.JsonOptions) ?? throw new UnidotException("Invalid apply state.");
        if (state.Version != 1 || state.WorkspaceId != manifest.Id || state.Baselines is null || state.Resolutions is null) throw new UnidotException("Apply state belongs to a different workspace or version.");
        foreach (var entry in state.Baselines) { Relative(entry.Key); ValidateHash(entry.Value); }
        foreach (var entry in state.Resolutions) { Relative(entry.Key); ValidateHash(entry.Value.BaseHash); ValidateHash(entry.Value.MainHash); ValidateHash(entry.Value.WorkspaceHash); }
        return state with { Baselines = new(state.Baselines, PathComparer.Instance), Resolutions = new(state.Resolutions, PathComparer.Instance) };
    }
    private static void ValidateHash(string? hash)
    {
        if (hash is not null && !Regex.IsMatch(hash, @"^[A-F0-9]{64}$")) throw new UnidotException("Invalid source hash in apply state.");
    }
    private static void SaveState(WorkspaceManifest manifest, WorkspaceApplyState state) => AtomicWrite(Path.Combine(manifest.Root, ".unidot", StateName), JsonSerializer.SerializeToUtf8Bytes(state, Configuration.JsonOptions));
    private static void WriteReport(string path, WorkspaceManifest manifest, WorkspaceApplyEntry[] entries, string status, bool dryRun, string? backup) => AtomicWrite(path,
        JsonSerializer.SerializeToUtf8Bytes(new { WorkspaceId = manifest.Id, Timestamp = DateTime.UtcNow.ToString("O"), Status = status, DryRun = dryRun, Backup = backup, Files = entries }, Configuration.JsonOptions));

    private static void CreateDirectories(string path, List<string> created)
    {
        if (Directory.Exists(path)) return;
        CreateDirectories(Path.GetDirectoryName(path)!, created); Directory.CreateDirectory(path); created.Add(path);
    }
    private static void ReplaceFile(string path, byte[] bytes, bool overwrite)
    {
        WorkspaceManager.RequireRealAncestors(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".unidot-apply-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, overwrite); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        WorkspaceManager.RequireRealAncestors(path); ReplaceFile(path, bytes, File.Exists(path));
    }

    private sealed class SourceLocks : IDisposable
    {
        public List<FileStream> Streams { get; } = [];
        public void Dispose() { foreach (var stream in Streams.AsEnumerable().Reverse()) stream.Dispose(); }
    }
    private static SourceLocks AcquireSourceLocks(IEnumerable<string> targets)
    {
        var directory = Path.Combine(Path.GetTempPath(), "opencode", "unidot-source-locks"); Directory.CreateDirectory(directory);
        var locks = new SourceLocks();
        try
        {
            foreach (var parent in targets.Select(p => Path.GetDirectoryName(p)!).Distinct(PathComparer.Instance).Order(PathComparer.Instance))
            {
                var key = OperatingSystem.IsWindows() ? parent.ToUpperInvariant() : parent;
                var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
                locks.Streams.Add(new FileStream(Path.Combine(directory, name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            return locks;
        }
        catch (IOException) { locks.Dispose(); throw new UnidotException("Another workspace is applying/resolving the same source directory."); }
    }

    private static string? Decode(byte[] bytes)
    {
        try
        {
            Encoding encoding = new UTF8Encoding(false, true); var offset = 0;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = new UTF32Encoding(false, false, true); offset = 4; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, false, true); offset = 4; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, false, true); offset = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, false, true); offset = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) offset = 3;
            var text = encoding.GetString(bytes, offset, bytes.Length - offset);
            return text.Contains('\0') ? null : text;
        }
        catch (DecoderFallbackException) { return null; }
    }

    private static bool HasMarkers(byte[] bytes) => Decode(bytes) is { } text &&
        Regex.IsMatch(text, @"(?m)^(?:<<<<<<<(?:[ \t\r]|$)|=======[ \t]*\r?$|>>>>>>>(?:[ \t\r]|$))");

    private static string ConflictDiff(WorkspaceApplyEntry entry, string workspaceFile)
    {
        if ((File.Exists(entry.Target) && new FileInfo(entry.Target).Length > 1048576) || (File.Exists(workspaceFile) && new FileInfo(workspaceFile).Length > 1048576))
            return "Binary/large-file conflict: compare the files directly; hashes are recorded above.";
        var main = entry.MainHash is null ? "" : Decode(File.ReadAllBytes(entry.Target));
        var workspace = entry.WorkspaceHash is null ? "" : Decode(File.ReadAllBytes(workspaceFile));
        if (main is null || workspace is null) return "Binary/unsupported-text conflict: hashes are recorded above.";
        var diff = Differ.Instance.CreateLineDiffs(main, workspace, false);
        var result = new StringBuilder().AppendLine("--- main/" + entry.Path).AppendLine("+++ workspace/" + entry.Path);
        foreach (var block in diff.DiffBlocks)
        {
            var contextBefore = Math.Min(3, Math.Min(block.DeleteStartA, block.InsertStartB));
            var contextAfter = Math.Min(3, Math.Min(diff.PiecesOld.Count - block.DeleteStartA - block.DeleteCountA, diff.PiecesNew.Count - block.InsertStartB - block.InsertCountB));
            result.AppendLine($"@@ -{block.DeleteStartA - contextBefore + 1},{block.DeleteCountA + contextBefore + contextAfter} +{block.InsertStartB - contextBefore + 1},{block.InsertCountB + contextBefore + contextAfter} @@");
            foreach (var line in diff.PiecesOld.Skip(block.DeleteStartA - contextBefore).Take(contextBefore)) result.AppendLine(" " + line);
            foreach (var line in diff.PiecesOld.Skip(block.DeleteStartA).Take(block.DeleteCountA)) result.AppendLine("-" + line);
            foreach (var line in diff.PiecesNew.Skip(block.InsertStartB).Take(block.InsertCountB)) result.AppendLine("+" + line);
            foreach (var line in diff.PiecesOld.Skip(block.DeleteStartA + block.DeleteCountA).Take(contextAfter)) result.AppendLine(" " + line);
        }
        if (!diff.DiffBlocks.Any()) result.AppendLine("Text lines match; bytes differ in encoding, BOM or line endings.");
        return result.ToString();
    }
}
