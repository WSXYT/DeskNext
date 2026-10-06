using System.Text.Json.Nodes;
using DeskNest.Core.Flow;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class ManualFlowDefinitionTests
{
    [Fact]
    public void EditorKeepsManualDefinitionsDisabledAndRejectsUnsafeHeaders()
    {
        string json = ManualFlowDefinitions.Create("文件流程", "提示", "你好");
        var definition = ManualFlowDefinitions.Read(json);
        Assert.NotEqual(Guid.Empty, definition.Id);
        Assert.Equal("文件流程", definition.Name);
        var node = JsonNode.Parse(json)!;
        node["enabled"] = true;
        Assert.Equal(FlowDefinitionValidation.Invalid, ManualFlowDefinitions.Validate(node.ToJsonString()));
        Assert.Throws<InvalidDataException>(() => ManualFlowDefinitions.Read(ManualFlowDefinitions.Create("name", "title", "nul\0message")));
        Assert.Equal(FlowDefinitionValidation.NativeUnavailable, ManualFlowDefinitions.Validate(json, "relative-native-library.dll"));
    }

    [FlowNativeFact]
    public async Task ImportAndExportPreserveNodesWithoutActivationOverwriteOrExecution()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "DeskNext-flow-transfer-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string library = Environment.GetEnvironmentVariable("DESKNEXT_FLOW_NATIVE_LIBRARY")!;
            string json = ManualFlowDefinitions.Create("导入", "عنوان", "message");
            var original = ManualFlowDefinitions.Read(json);
            var input = JsonNode.Parse(json)!;
            input["enabled"] = true;
            input["revision"] = 42;
            input["extensions"] = new JsonObject { ["note"] = "preserve" };
            string path = Path.Combine(root, "input.flow.json"), bytes = input.ToJsonString();
            File.WriteAllText(path, bytes, new System.Text.UTF8Encoding(true));
            var imported = await ManualFlowDefinitions.ImportFileAsync(path, libraryPath: library);
            Assert.NotEqual(original.Id, imported.Id);
            Assert.Equal(0, imported.Revision);
            Assert.False(JsonNode.Parse(imported.Json)!["enabled"]!.GetValue<bool>());
            Assert.True(JsonNode.DeepEquals(input["actions"], JsonNode.Parse(imported.Json)!["actions"]));
            Assert.True(JsonNode.DeepEquals(input["extensions"], JsonNode.Parse(imported.Json)!["extensions"]));
            string first = await ManualFlowDefinitions.ExportFileAsync(root, imported.Json, libraryPath: library);
            string second = await ManualFlowDefinitions.ExportFileAsync(root, imported.Json, libraryPath: library);
            Assert.NotEqual(first, second);
            Assert.Equal(imported.Json, File.ReadAllText(first));
            Assert.Equal(imported.Json, File.ReadAllText(second));
            Assert.Equal(bytes, File.ReadAllText(path));
            Assert.Throws<NotSupportedException>(() => ManualFlowDefinitions.ImportDraft(json, "missing-library"));
            input["trigger"]!["type"] = "pogget.trigger.schedule";
            Assert.Throws<InvalidDataException>(() => ManualFlowDefinitions.ImportDraft(input.ToJsonString(), library));
            File.WriteAllBytes(path, [0xff, 0xfe]);
            await Assert.ThrowsAsync<System.Text.DecoderFallbackException>(() => ManualFlowDefinitions.ImportFileAsync(path, libraryPath: library));
            File.WriteAllBytes(path, new byte[ManualFlowDefinitions.MaximumBytes + 1]);
            await Assert.ThrowsAsync<InvalidDataException>(() => ManualFlowDefinitions.ImportFileAsync(path, libraryPath: library));
            Assert.Equal(3, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [FlowNativeFact]
    public async Task DefinitionStorageRetainsRevisionsAndRefusesCorruptReset()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "DeskNext-flow-store-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            await using var workspace = await DeskNest.Core.Workspace.WorkspaceStore.OpenAsync(root);
            var store = new ManualFlowStore(workspace, Environment.GetEnvironmentVariable("DESKNEXT_FLOW_NATIVE_LIBRARY"));
            Assert.Empty(await store.LoadAsync());
            string draft = ManualFlowDefinitions.Create("流程", "title", "message");
            var incoming = ManualFlowDefinitions.Read(draft);
            var saved = await store.SaveAsync(incoming.Id, draft);
            Assert.Equal(1, saved.Revision);
            Assert.Equal(saved, Assert.Single(await store.LoadAsync()));
            Assert.Equal(0, workspace.Snapshot.Revision);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(incoming.Id, draft));
            await store.SaveAsync(saved.Id, saved.Json);
            string path = Path.Combine(root, "flow-definitions.json");
            File.WriteAllText(path, "{");
            File.WriteAllText(path + ".bak", "{");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
            Assert.True(File.Exists(path + ".recovery-required"));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(incoming.Id, draft));
            Assert.NotEmpty(Directory.GetFiles(root, "*.corrupt-*"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [FlowNativeFact]
    public void RealNativeValidatorChecksModulesWithoutExecutingThem()
    {
        string library = Environment.GetEnvironmentVariable("DESKNEXT_FLOW_NATIVE_LIBRARY")!;
        string json = ManualFlowDefinitions.Create("流", "title", "message");
        Assert.Equal(FlowDefinitionValidation.Valid, ManualFlowDefinitions.Validate(json, library));
        Assert.Equal(FlowDefinitionValidation.Invalid, ManualFlowDefinitions.Validate(json.Replace("pogget.interaction.tip", "unknown.action"), library));
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "DeskNext-flow-definition-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string source = Path.Combine(root, "source.txt");
            File.WriteAllText(source, "must not move");
            var document = JsonNode.Parse(json)!;
            var action = document["actions"]![0]!;
            action["type"] = "pogget.action.file.move";
            action["parameters"] = new JsonObject { ["source"] = source, ["destinationDirectory"] = Path.Combine(root, "destination") };
            Assert.Equal(FlowDefinitionValidation.Valid, ManualFlowDefinitions.Validate(document.ToJsonString(), library));
            Assert.Equal("must not move", File.ReadAllText(source));
            Assert.False(Directory.Exists(Path.Combine(root, "destination")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
