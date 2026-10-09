// Compiled by the target Unity compiler against the target Player, not by MSBuild.
using System;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Unidot.Runtime
{
    public static class Bootstrap
    {
        private static SynchronizationContext _context;
        private static CancellationTokenSource _lifetime;
        private static string _directory;
        private static string _session;

        public static string Install()
        {
            if (_lifetime != null) return "Unidot runtime already installed.";
            _context = SynchronizationContext.Current;
            if (_context == null || _context.GetType().FullName != "UnityEngine.UnitySynchronizationContext")
                return "UNIDOT_NOT_READY";
            _directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _session = Environment.GetEnvironmentVariable("UNIDOT_RUNTIME_DIRECTORY");
            AppDomain.CurrentDomain.AssemblyResolve += ResolveDependency;
            _lifetime = new CancellationTokenSource();
            // Agent requests must keep reaching the PlayerLoop while the game window is in the background.
            Application.runInBackground = true;
            Application.quitting += Stop;
            Task.Run(async () =>
            {
                try { await ServeAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                catch (Exception error) { File.WriteAllText(Path.Combine(_session, "runtime-error.txt"), error.ToString()); }
            });
            return "Unidot runtime installed on managed thread " + Thread.CurrentThread.ManagedThreadId;
        }

        private static Assembly ResolveDependency(object sender, ResolveEventArgs args)
        {
            var path = Path.Combine(_directory, new AssemblyName(args.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }

        private static async Task ServeAsync()
        {
            using (var pipe = new NamedPipeServerStream(Environment.GetEnvironmentVariable("UNIDOT_RUNTIME_PIPE"),
                PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                await pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                await using (var server = McpServer.Create(new StreamServerTransport(pipe, pipe),
                    new McpServerOptions
                    {
                        ServerInfo = new Implementation { Name = "Unidot Unity Runtime", Version = "1" },
                        ToolCollection = new McpServerPrimitiveCollection<McpServerTool>
                        {
                            McpServerTool.Create((Func<CancellationToken, Task<string>>)StatusAsync,
                                new McpServerToolCreateOptions { Name = "runtime_status" }),
                            McpServerTool.Create((Func<string, string, string, CancellationToken, Task<CallToolResult>>)InvokeAsync,
                                new McpServerToolCreateOptions { Name = "invoke_assembly" })
                        }
                    }))
                {
                    File.WriteAllText(Path.Combine(_session, "runtime-ready.txt"), "MCP server created");
                    await server.RunAsync(_lifetime.Token).ConfigureAwait(false);
                }
            }
        }

        private static Task<string> StatusAsync(CancellationToken cancellationToken) => OnMainThread(() => Task.FromResult(
            JsonSerializer.Serialize(new
            {
                unityVersion = Application.unityVersion,
                developmentBuild = Debug.isDebugBuild,
                runInBackground = Application.runInBackground,
                scene = SceneManager.GetActiveScene().name,
                sceneCount = SceneManager.sceneCount,
                managedThreadId = Thread.CurrentThread.ManagedThreadId,
                synchronizationContext = SynchronizationContext.Current.GetType().FullName,
                sessionDirectory = _session
            })), cancellationToken);

        private static async Task<CallToolResult> InvokeAsync(string assemblyPath, string typeName,
            string methodName, CancellationToken cancellationToken)
        {
            try
            {
                var fullPath = Path.GetFullPath(assemblyPath);
                var executions = Path.Combine(_session, "executions") + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(executions, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Only this session's compiled assemblies can be executed.");
                var value = await OnMainThread(async () =>
                {
                    var assembly = Assembly.Load(File.ReadAllBytes(fullPath));
                    var type = assembly.GetType(typeName, true);
                    var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static,
                        null, Type.EmptyTypes, null);
                    if (method == null || method.ContainsGenericParameters)
                        throw new MissingMethodException(typeName, methodName + " (public static, parameterless)");
                    if ((method.ReturnType == typeof(void) && method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>() != null) ||
                        method.ReturnType.FullName.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.Ordinal))
                        throw new InvalidOperationException("Use a synchronous or Task-returning entry point, not async void or ValueTask.");
                    var result = method.Invoke(null, null);
                    if (result is Task task)
                    {
                        await task;
                        var resultProperty = task.GetType().GetProperty("Result");
                        result = resultProperty == null ? null : resultProperty.GetValue(task);
                    }
                    return result == null ? "null" : JsonSerializer.Serialize(result, result.GetType());
                }, cancellationToken).ConfigureAwait(false);
                return Text(value, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { return Text(error.GetBaseException().ToString(), true); }
        }

        private static CallToolResult Text(string value, bool error) => new CallToolResult
        {
            IsError = error,
            Content = new ContentBlock[] { new TextContentBlock { Text = value } }
        };

        private static Task<T> OnMainThread<T>(Func<Task<T>> action, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = cancellationToken.Register(() => completion.TrySetCanceled());
            _context.Post(async _ =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _lifetime.Token.ThrowIfCancellationRequested();
                    completion.TrySetResult(await action());
                }
                catch (OperationCanceledException) { completion.TrySetCanceled(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { registration.Dispose(); }
            }, null);
            return completion.Task;
        }

        private static void Stop() => _lifetime.Cancel();
    }
}
