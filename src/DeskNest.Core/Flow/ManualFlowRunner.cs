using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text;
using DeskNest.Core.Storage;

namespace DeskNest.Core.Flow;

public sealed record FlowPrompt(string Title, string Message);
public sealed record FlowMove(string SourcePath, string DestinationPath);

/// <summary>Explicit literal prompts and optionally host-confirmed moves. No automatic trigger or native file mutation.</summary>
public static class ManualFlowRunner
{
    public static Task<int> RunAsync(string json, Func<FlowPrompt, CancellationToken, Task<bool>> showPrompt,
        CancellationToken token = default, string? nativeLibraryPath = null,
        Func<FlowMove, CancellationToken, Task<string>>? executeMove = null, Action<FlowMove>? validateMove = null)
    {
        ArgumentNullException.ThrowIfNull(showPrompt);
        token.ThrowIfCancellationRequested();
        ManualFlowDefinitions.Read(json); // Persisted definitions remain disabled/manual.
        var document = JsonNode.Parse(json)!.AsObject();
        if (document["trigger"]?["enabled"]?.GetValue<bool>() == false || document["actions"] is not JsonArray actions || actions.Count is < 1 or > 100)
            throw new InvalidDataException("Prompt runs require an enabled manual trigger and 1–100 prompt steps.");
        var expected = new List<(FlowPrompt? Prompt, FlowMove? Move)>();
        foreach (var item in actions)
        {
            if (item is not JsonObject) throw new InvalidDataException("A Flow action must be an object.");
            string? type = item["type"]?.GetValue<string>();
            bool moveStep = type == "pogget.action.file.move" && executeMove is not null && validateMove is not null;
            if (type != "pogget.interaction.tip" && !moveStep)
                throw new NotSupportedException("This run accepts literal prompts and explicitly authorized file moves only.");
            foreach (string field in new[] { "inputs", "portAliases", "variableAliases", "branches" })
                if (item[field] is { } value && (value is not JsonObject map || map.Count != 0))
                    throw new NotSupportedException("Prompt runs do not support bindings, aliases or branches.");
            if (item["enabled"]?.GetValue<bool>() == false) continue;
            var parameters = item["parameters"];
            if (!moveStep)
                expected.Add((new(parameters?["title"]?.GetValue<string>() ?? "", parameters?["message"]?.GetValue<string>() ?? ""), null));
            else
            {
                string source = parameters?["source"]?.GetValue<string>() ?? "";
                string directory = parameters?["destinationDirectory"]?.GetValue<string>() ?? "";
                if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(directory) ||
                    !string.IsNullOrEmpty(parameters?["destinationContainerId"]?.GetValue<string>()) || source.Contains("${") || directory.Contains("${"))
                    throw new NotSupportedException("Move runs require literal absolute source and destination-folder paths.");
                var request = new FlowMove(Path.GetFullPath(source), Path.Combine(Path.GetFullPath(directory), Path.GetFileName(source)));
                expected.Add((null, request));
            }
        }
        if (expected.Count == 0) throw new InvalidDataException("There are no enabled prompt steps.");
        if (expected.Any(p => p.Prompt is { } prompt && (prompt.Title.Contains("${", StringComparison.Ordinal) || prompt.Message.Contains("${", StringComparison.Ordinal))))
            throw new NotSupportedException("Prompt runs accept literal text, not runtime substitutions.");
        // Authorize only after every step has passed the restricted-shape policy, before native path probing.
        foreach (var step in expected) if (step.Move is { } move) validateMove!(move);
        string libraryPath = nativeLibraryPath ?? ManualFlowDefinitions.DefaultLibraryPath;
        // Only this transient, isolated submission is enabled; neither the draft nor stored definition changes.
        document["enabled"] = true;
        string submitted = document.ToJsonString();
        return Task.Run(async () =>
        {
            if (ManualFlowDefinitions.Validate(json, libraryPath) != FlowDefinitionValidation.Valid)
                throw new InvalidDataException("Native validation is unavailable or rejected the prompt definition.");
            FileSystemVolume.RequireNoReparsePoints(libraryPath);
            IntPtr library = NativeLibrary.Load(libraryPath), flow = IntPtr.Zero, text = IntPtr.Zero;
            Tip? callback = null;
            FileExecutor? fileCallback = null;
            Control? cancel = null, destroy = null;
            Exception? callbackError = null;
            int seen = 0, accepted = 0, declined = 0;
            try
            {
                T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
                var create = Export<Create>("dn_flow_create");
                var submit = Export<Submit>("dn_flow_submit");
                var busy = Export<Busy>("dn_flow_busy");
                cancel = Export<Control>("dn_flow_cancel");
                destroy = Export<Control>("dn_flow_destroy");
                callback = (_, title, message) =>
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        int index = Interlocked.Increment(ref seen) - 1;
                        var prompt = new FlowPrompt(Marshal.PtrToStringUTF8(title) ?? "", Marshal.PtrToStringUTF8(message) ?? "");
                        if (index >= expected.Count || prompt != expected[index].Prompt)
                            throw new InvalidDataException("Native prompt output did not match the literal step sequence.");
                        if (!showPrompt(prompt, token).WaitAsync(token).GetAwaiter().GetResult())
                        { Interlocked.Exchange(ref declined, 1); return -1; }
                        Interlocked.Increment(ref accepted);
                        return 1;
                    }
                    catch (OperationCanceledException) { Interlocked.Exchange(ref declined, 1); return -1; }
                    catch (Exception error) { Interlocked.CompareExchange(ref callbackError, error, null); return -1; }
                };
                if (executeMove is not null)
                {
                    fileCallback = (_, source, destination, actualPath, capacity) =>
                    {
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            int index = Interlocked.Increment(ref seen) - 1;
                            var request = new FlowMove(Marshal.PtrToStringUTF8(source) ?? "", Marshal.PtrToStringUTF8(destination) ?? "");
                            if (index >= expected.Count || expected[index].Move is not { } authorized ||
                                !SamePath(request.SourcePath, authorized.SourcePath) || !SamePath(request.DestinationPath, authorized.DestinationPath) ||
                                Encoding.UTF8.GetByteCount(authorized.DestinationPath) + 1 > capacity)
                                throw new InvalidDataException("Native move output differs from its authorized literal step.");
                            // Do not abandon an in-flight transaction with WaitAsync(token). Cancellation must drain its commit/recovery checkpoint.
                            string actual = executeMove(authorized, token).GetAwaiter().GetResult();
                            if (!SamePath(actual, authorized.DestinationPath)) throw new InvalidDataException("Host move returned an unexpected destination; inspect operation history.");
                            byte[] bytes = Encoding.UTF8.GetBytes(actual + "\0");
                            if (bytes.Length > capacity) throw new InvalidDataException("Host move receipt exceeds the native buffer; inspect operation history.");
                            Marshal.Copy(bytes, 0, actualPath, bytes.Length);
                            Interlocked.Increment(ref accepted);
                            return 1;
                        }
                        catch (OperationCanceledException) { Interlocked.Exchange(ref declined, 1); return -1; }
                        catch (Exception error) { Interlocked.CompareExchange(ref callbackError, error, null); return -1; }
                    };
                }
                token.ThrowIfCancellationRequested();
                flow = fileCallback is null ? create(callback, IntPtr.Zero) : Export<CreateWithExecutor>("dn_flow_create_with_executor")(callback, fileCallback, IntPtr.Zero);
                if (flow == IntPtr.Zero) throw new InvalidOperationException("Native Flow runtime creation failed.");
                text = Marshal.StringToCoTaskMemUTF8(submitted);
                if (submit(flow, text) != 1) throw new InvalidDataException("Native Flow runtime refused the manual submission.");
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int state = busy(flow);
                    if (state == 0) break;
                    if (state != 1) throw new InvalidOperationException("Native Flow state is unavailable.");
                    await Task.Delay(25, token).ConfigureAwait(false);
                }
                if (Volatile.Read(ref callbackError) is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
                if (Volatile.Read(ref declined) != 0) throw new OperationCanceledException("The prompt run was cancelled.", token);
                if (Volatile.Read(ref accepted) != expected.Count) throw new InvalidDataException("The native runtime did not complete every step; inspect operation history.");
                return accepted;
            }
            finally
            {
                // Task.Run keeps draining/destroying off the UI and native callback threads.
                if (flow != IntPtr.Zero) { cancel!(flow); destroy!(flow); }
                GC.KeepAlive(callback);
                GC.KeepAlive(fileCallback);
                if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text);
                NativeLibrary.Free(library);
            }
        }, token);
    }

    internal static bool SamePath(string left, string right) => Path.IsPathFullyQualified(left) && Path.IsPathFullyQualified(right) &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FileExecutor(IntPtr context, IntPtr source, IntPtr destination, IntPtr actualPath, uint capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateWithExecutor(Tip callback, FileExecutor executor, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Tip(IntPtr context, IntPtr title, IntPtr message);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Create(Tip callback, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Submit(IntPtr flow, IntPtr json);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Busy(IntPtr flow);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Control(IntPtr flow);
}
