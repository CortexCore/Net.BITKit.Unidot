using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Unidot;

internal sealed record CodeCompilation(string Id, string AssemblyPath, string SourcePath, string TypeName, string MethodName, string Diagnostics, bool Success);

internal sealed class RuntimeCodeCompiler(UnityToolchain toolchain, PlayerLayout player, string directory, string runtimeDirectory, bool shared)
{
    public static string ResponseFile(IEnumerable<string> references, string source, string output, bool debug = true)
    {
        static string Quote(string path)
        {
            if (path.IndexOfAny(['"', '\r', '\n']) >= 0) throw new UnidotException("Compiler paths cannot contain quotes or newlines.");
            return "\"" + path.Replace('\\', '/') + "\"";
        }
        var text = new StringBuilder("/target:library\n/nostdlib+\n/langversion:9.0\n/nologo\n/utf8output\n/preferreduilang:en-US\n/unsafe+\n");
        text.AppendLine("/out:" + Quote(output));
        if (debug) text.AppendLine("/debug:portable");
        foreach (var path in references.OrderBy(p => p, PathComparer.Instance)) text.AppendLine("/reference:" + Quote(path));
        text.AppendLine(Quote(source));
        return text.ToString();
    }

    public string[] References()
    {
        var references = System.IO.Directory.EnumerateFiles(player.ManagedDirectory, "*.dll")
            .ToDictionary(p => Path.GetFileName(p), p => p, PathComparer.Instance);
        foreach (var path in System.IO.Directory.EnumerateFiles(runtimeDirectory, "*.dll"))
            if (Path.GetFileName(path) != "Unidot.Runtime.Bootstrap.dll") references.TryAdd(Path.GetFileName(path), path);
        return references.Values.ToArray();
    }

    public async Task<CodeCompilation> CompileAsync(string code, string typeName, string methodName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new UnidotException("code must contain a complete C# type.");
        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(methodName)) throw new UnidotException("typeName and methodName are required.");
        var id = Guid.NewGuid().ToString("N");
        var root = Path.Combine(directory, "executions", id);
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "Script.cs");
        var output = Path.Combine(root, "Unidot.Execution." + id + ".dll");
        var response = Path.Combine(root, "compile.rsp");
        var log = Path.Combine(root, "compiler.log");
        await File.WriteAllTextAsync(source, code, new UTF8Encoding(false), cancellationToken);
        await File.WriteAllTextAsync(response, ResponseFile(References(), source, output), cancellationToken);
        var arguments = new List<string> { "exec", toolchain.Compiler, "/noconfig" };
        if (shared) arguments.Add("/shared");
        arguments.Add("@" + response);
        var exit = await Processes.ExecuteAsync(toolchain.Dotnet, arguments, root, cancellationToken, log, (_, _) => { });
        var diagnostics = await File.ReadAllTextAsync(log, cancellationToken);
        return new(id, output, source, typeName, methodName, diagnostics, exit == 0 && File.Exists(output));
    }

    public async Task<string> PrepareBootstrapAsync(CancellationToken cancellationToken)
    {
        var support = Path.Combine(AppContext.BaseDirectory, "runtime");
        if (!Directory.Exists(support) || !File.Exists(Path.Combine(support, "ModelContextProtocol.Core.dll")))
            throw new UnidotException("Runtime MCP dependencies are missing. Install a distribution containing runtime/*.dll.");
        Directory.CreateDirectory(runtimeDirectory);
        foreach (var path in Directory.EnumerateFiles(support, "*.dll")) File.Copy(path, Path.Combine(runtimeDirectory, Path.GetFileName(path)), true);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Unidot.Runtime.Bootstrap.cs")
            ?? throw new UnidotException("Embedded runtime bootstrap source is missing.");
        using var reader = new StreamReader(stream);
        var source = Path.Combine(runtimeDirectory, "Bootstrap.cs");
        await File.WriteAllTextAsync(source, await reader.ReadToEndAsync(cancellationToken), cancellationToken);
        var output = Path.Combine(runtimeDirectory, "Unidot.Runtime.Bootstrap.dll");
        var response = Path.Combine(runtimeDirectory, "bootstrap.rsp");
        var log = Path.Combine(runtimeDirectory, "compiler.log");
        await File.WriteAllTextAsync(response, ResponseFile(References(), source, output), cancellationToken);
        var exit = await Processes.ExecuteAsync(toolchain.Dotnet, ["exec", toolchain.Compiler, "/noconfig", "@" + response],
            runtimeDirectory, cancellationToken, log, (_, _) => { });
        if (exit != 0) throw new UnidotException("Runtime bootstrap compilation failed:\n" + await File.ReadAllTextAsync(log, cancellationToken));
        return output;
    }
}
