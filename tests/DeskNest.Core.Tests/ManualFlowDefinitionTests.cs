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
