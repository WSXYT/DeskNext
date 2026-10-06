using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DeskNest.Core.Flow;

public sealed record ManualFlowDefinition(Guid Id, string Name, long Revision, string Json);
public enum FlowDefinitionValidation { Valid, Invalid, NativeUnavailable }

/// <summary>Read/write the upstream definition format. Validation never creates a native runtime or executes a step.</summary>
public static class ManualFlowDefinitions
{
    public const int MaximumBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Create(string name, string title, string message) => JsonSerializer.Serialize(new
    {
        format = "pogget.flow", schemaVersion = 1, id = Guid.NewGuid().ToString("N"), revision = 0,
        name, publisher = "DeskNext", description = "", enabled = false,
        trigger = new { id = "manual", type = "pogget.trigger.manual", version = 1, enabled = true, parameters = new { } },
        actions = new[] { new { id = "step-1", type = "pogget.interaction.tip", version = 1, enabled = true,
            parameters = new { title, message } } }
    }, JsonOptions);

    public static ManualFlowDefinition Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Contains('\0') || Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            throw new InvalidDataException("Flow definition is empty, oversized or contains a NUL character.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            RejectNul(root);
            if (root.GetProperty("format").GetString() != "pogget.flow" || root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("enabled").GetBoolean() || root.GetProperty("trigger").GetProperty("type").GetString() != "pogget.trigger.manual")
                throw new InvalidDataException("Only disabled manual Flow definitions are supported by this editor.");
            string? name = root.GetProperty("name").GetString();
            if (!Guid.TryParse(root.GetProperty("id").GetString(), out var id) || id == Guid.Empty || string.IsNullOrWhiteSpace(name) || name.Length > 160)
                throw new InvalidDataException("Invalid Flow identity or name.");
            long revision = root.GetProperty("revision").GetInt64();
            if (revision < 0) throw new InvalidDataException("Invalid Flow revision.");
            return new(id, name, revision, json);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("Invalid Flow definition schema.", error); }
    }

    private static void RejectNul(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString()!.Contains('\0'))
            throw new InvalidDataException("Flow strings cannot contain NUL characters.");
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) RejectNul(child);
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name.Contains('\0')) throw new InvalidDataException("Flow keys cannot contain NUL characters.");
                RejectNul(property.Value);
            }
    }

    public static string DefaultLibraryPath => Path.Combine(AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "desknest_pogget.dll" : OperatingSystem.IsMacOS() ? "libdesknest_pogget.dylib" : "libdesknest_pogget.so");

    // The optional path is for trusted developer probes, never a field from an imported Flow definition.
    public static FlowDefinitionValidation Validate(string json, string? libraryPath = null)
    {
        try { Read(json); }
        catch (InvalidDataException) { return FlowDefinitionValidation.Invalid; }
        string path = libraryPath ?? DefaultLibraryPath;
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return FlowDefinitionValidation.NativeUnavailable;
        IntPtr library = IntPtr.Zero, input = IntPtr.Zero;
        try
        {
            Storage.FileSystemVolume.RequireNoReparsePoints(path);
            library = NativeLibrary.Load(path);
            var validate = Marshal.GetDelegateForFunctionPointer<ValidateDelegate>(NativeLibrary.GetExport(library, "dn_flow_validate"));
            input = Marshal.StringToCoTaskMemUTF8(json);
            return validate(input) == 1 ? FlowDefinitionValidation.Valid : FlowDefinitionValidation.Invalid;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return FlowDefinitionValidation.NativeUnavailable; }
        finally
        {
            if (input != IntPtr.Zero) Marshal.FreeCoTaskMem(input);
            if (library != IntPtr.Zero) NativeLibrary.Free(library);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ValidateDelegate(IntPtr json);
}
