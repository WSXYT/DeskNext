using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using DeskNest.Core.Storage;

namespace DeskNest.Core.Flow;

public sealed record FlowPrompt(string Title, string Message);

/// <summary>One explicit run of literal prompt steps. No file adapter, watcher or tick is ever installed.</summary>
public static class ManualPromptFlowRunner
{
    public static Task<int> RunAsync(string json, Func<FlowPrompt, CancellationToken, Task<bool>> showPrompt,
        CancellationToken token = default, string? nativeLibraryPath = null)
    {
        ArgumentNullException.ThrowIfNull(showPrompt);
        token.ThrowIfCancellationRequested();
        ManualFlowDefinitions.Read(json); // Persisted definitions remain disabled/manual.
        var document = JsonNode.Parse(json)!.AsObject();
        if (document["trigger"]?["enabled"]?.GetValue<bool>() == false || document["actions"] is not JsonArray actions || actions.Count is < 1 or > 100)
            throw new InvalidDataException("Prompt runs require an enabled manual trigger and 1–100 prompt steps.");
        var expected = new List<FlowPrompt>();
        foreach (var item in actions)
        {
            if (item?["type"]?.GetValue<string>() != "pogget.interaction.tip")
                throw new NotSupportedException("Only prompt steps can run here; file and control steps are not enabled.");
            foreach (string field in new[] { "inputs", "portAliases", "variableAliases", "branches" })
                if (item[field] is { } value && (value is not JsonObject map || map.Count != 0))
                    throw new NotSupportedException("Prompt runs do not support bindings, aliases or branches.");
            if (item["enabled"]?.GetValue<bool>() == false) continue;
            expected.Add(new(item["parameters"]?["title"]?.GetValue<string>() ?? "", item["parameters"]?["message"]?.GetValue<string>() ?? ""));
        }
        if (expected.Count == 0) throw new InvalidDataException("There are no enabled prompt steps.");
        if (expected.Any(p => p.Title.Contains("${", StringComparison.Ordinal) || p.Message.Contains("${", StringComparison.Ordinal)))
            throw new NotSupportedException("Prompt runs accept literal text, not runtime substitutions.");
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
                        if (index >= expected.Count || prompt != expected[index])
                            throw new InvalidDataException("Native prompt output did not match the literal step sequence.");
                        if (!showPrompt(prompt, token).WaitAsync(token).GetAwaiter().GetResult())
                        { Interlocked.Exchange(ref declined, 1); return -1; }
                        Interlocked.Increment(ref accepted);
                        return 1;
                    }
                    catch (OperationCanceledException) { Interlocked.Exchange(ref declined, 1); return -1; }
                    catch (Exception error) { Interlocked.CompareExchange(ref callbackError, error, null); return -1; }
                };
                token.ThrowIfCancellationRequested();
                flow = create(callback, IntPtr.Zero);
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
                if (Volatile.Read(ref accepted) != expected.Count) throw new InvalidDataException("The native runtime did not acknowledge every prompt.");
                return accepted;
            }
            finally
            {
                // Task.Run keeps draining/destroying off the UI and native callback threads.
                if (flow != IntPtr.Zero) { cancel!(flow); destroy!(flow); }
                GC.KeepAlive(callback);
                if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text);
                NativeLibrary.Free(library);
            }
        }, token);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Tip(IntPtr context, IntPtr title, IntPtr message);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Create(Tip callback, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Submit(IntPtr flow, IntPtr json);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Busy(IntPtr flow);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Control(IntPtr flow);
}
