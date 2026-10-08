using System.Text;
using System.Text.RegularExpressions;

namespace Unidot;

internal sealed class BuildDiagnostics : IDisposable
{
    private readonly TextWriter journal;
    private readonly bool verbose;
    private int compilerWarnings, ilppWarnings, processed;
    public int CompilerWarnings => compilerWarnings;
    public int IlppWarnings => ilppWarnings;
    public int Processed => processed;
    public int DiscoveryWarnings { get; private set; }
    public int Warnings => CompilerWarnings + IlppWarnings + DiscoveryWarnings;

    public BuildDiagnostics(string directory, bool verbose)
    {
        Directory.CreateDirectory(directory);
        this.verbose = verbose;
        journal = TextWriter.Synchronized(new StreamWriter(new FileStream(Path.Combine(directory, "build.log"), FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true });
    }

    public void Record(string phase, string message) => journal.WriteLine($"[{phase}] {message}");
    public void Detail(string message) { Record("detail", message); if (verbose) Console.WriteLine(message); }
    public void Discovery(IEnumerable<string> warnings)
    {
        foreach (var warning in warnings)
        {
            DiscoveryWarnings++;
            Record("discovery warning", warning);
            if (verbose) Console.Error.WriteLine("Warning: " + warning);
        }
    }
    public void Compiler(string line, bool stderr)
    {
        Record(stderr ? "compiler stderr" : "compiler", line);
        if (IsWarning(line) && !IsError(line)) { Interlocked.Increment(ref compilerWarnings); if (verbose) Console.WriteLine(line); }
        else if (IsError(line) || stderr) Console.Error.WriteLine(line);
        else if (verbose) Console.WriteLine(line);
    }
    public void Ilpp(string line, bool stderr = false)
    {
        Record(stderr ? "ilpp stderr" : "ilpp", line);
        if (line.StartsWith("[ilpp processed]", StringComparison.Ordinal)) Interlocked.Increment(ref processed);
        if (IsWarning(line) && !IsError(line)) { Interlocked.Increment(ref ilppWarnings); if (verbose) Console.WriteLine(line); }
        else if (IsError(line) || stderr) Console.Error.WriteLine(line);
        else if (verbose) Console.WriteLine(line);
    }
    public void Error(string message)
    {
        Record("error", message);
        Console.Error.WriteLine(verbose ? message : message.Split('\n')[0].TrimEnd('\r'));
    }
    internal static bool IsWarning(string line) => line.StartsWith("[ilpp Warning]", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(line, @"(^|:\s*)warning(?:\s+[A-Z]+\d+)?\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static bool IsError(string line) => line.StartsWith("[ilpp Error]", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(line, @"(^|:\s*)(?:fatal\s+)?error(?:\s+[A-Z]+\d+)?\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) || RuntimeLogWriter.IsError(line);
    public void Dispose() => journal.Dispose();
}

// Unity logs are unstructured. Filtering is best-effort; the original log and native sidecars always retain all text.
internal sealed class RuntimeLogWriter(TextWriter output, bool verbose, bool stderr = false, string channel = "player", TextWriter? errorOutput = null) : TextWriter
{
    private readonly StringBuilder pending = new();
    private bool errorContext;
    private bool nativeLineStart = true;
    public override Encoding Encoding => Encoding.UTF8;
    internal static bool IsError(string line) => Regex.IsMatch(line,
        @"\b[\w.+`]*Exception\b|(^|\]\s*)(?:error|fatal|assertion failed|crash!!!)(\b|:)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static bool IsStack(string line) => Regex.IsMatch(line,
        @"^\s*(at\s+|--- End|Rethrow|Caused by:|Inner exception:|[\w.+`<>]+\s*\([^)]*\).*\(at\s)", RegexOptions.CultureInvariant);

    public override Task WriteAsync(string? value) => AppendAsync(value ?? "");
    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) => AppendAsync(buffer.ToString());
    public override Task WriteAsync(char[] buffer, int index, int count) => AppendAsync(new string(buffer, index, count));
    public override Task FlushAsync() => output.FlushAsync();

    private async Task AppendAsync(string text)
    {
        if (stderr)
        {
            // Native errors must remain live even when the producer does not write a newline.
            var offset = 0;
            while (offset < text.Length)
            {
                if (nativeLineStart) await output.WriteAsync($"[{channel}] ");
                var newline = text.IndexOf('\n', offset);
                var end = newline < 0 ? text.Length : newline + 1;
                await output.WriteAsync(text.AsMemory(offset, end - offset));
                nativeLineStart = newline >= 0;
                offset = end;
            }
            await output.FlushAsync();
            return;
        }
        pending.Append(text);
        while (true)
        {
            var current = pending.ToString();
            var newline = current.IndexOf('\n');
            if (newline >= 0)
            {
                await EmitAsync(current[..newline].TrimEnd('\r'));
                pending.Remove(0, newline + 1);
            }
            else if (pending.Length >= 65536)
            {
                var count = char.IsHighSurrogate(current[^1]) ? current.Length - 1 : current.Length;
                await EmitAsync(current[..count]); pending.Remove(0, count);
            }
            else break;
        }
    }

    private async Task EmitAsync(string line)
    {
        var error = IsError(line);
        var stack = errorContext && IsStack(line);
        errorContext = error || stack;
        if (verbose || stderr || error || stack)
            await (stderr || error || stack ? errorOutput ?? output : output).WriteLineAsync($"[{channel}] {line}");
    }

    public async Task CompleteAsync()
    {
        if (stderr && !nativeLineStart) { await output.WriteLineAsync(); nativeLineStart = true; }
        if (pending.Length > 0) { await EmitAsync(pending.ToString()); pending.Clear(); }
        await output.FlushAsync();
    }
}
