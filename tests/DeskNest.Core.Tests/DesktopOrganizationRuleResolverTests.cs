using DeskNest.Core.Organization;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class DesktopOrganizationRuleResolverTests
{
    private static DesktopOrganizationFileSnapshot Snapshot(string extension) =>
        new("/desktop/report.pdf", "report.pdf", extension, 1, DateTime.UtcNow,
            DesktopOrganizationCategoryIds.Documents, DesktopOrganizationSubtypeIds.Pdf,
            DesktopOrganizationExclusionReason.None);

    private static WidgetConfig Widget(string id) => new()
    {
        Id = id,
        MappedFolderPath = "/managed/" + id
    };

    [Fact]
    public void Resolve_PrefersExtensionOverSubtypeAndCategory_ButExcludedExtensionCannotWin()
    {
        var rules = new[]
        {
            new DesktopOrganizationRule { Id = "a", TargetWidgetId = "category", CategoryIds = [DesktopOrganizationCategoryIds.Documents] },
            new DesktopOrganizationRule { Id = "b", TargetWidgetId = "subtype", SubtypeIds = [DesktopOrganizationSubtypeIds.Pdf] },
            new DesktopOrganizationRule { Id = "c", TargetWidgetId = "extension", Extensions = ["PDF"] }
        };
        var resolver = new DesktopOrganizationRuleResolver();
        var widgets = new[] { Widget("category"), Widget("subtype"), Widget("extension") };
        Assert.Same(rules[2], resolver.Resolve(Snapshot(".pdf"), rules, widgets));
        rules[2].ExcludedExtensions.Add("pdf");
        Assert.Same(rules[1], resolver.Resolve(Snapshot(".pdf"), rules, widgets));
        widgets[1].IsDisabled = true;
        Assert.Same(rules[0], resolver.Resolve(Snapshot(".pdf"), rules, widgets));
    }

    [Fact]
    public void FindConflicts_ReportsOnlyEnabledCrossTargetRulesAndAssignTransfersOwnership()
    {
        var rules = new List<DesktopOrganizationRule>
        {
            new() { TargetWidgetId = "first", Extensions = [".PDF"], CategoryIds = ["Documents"] },
            new() { TargetWidgetId = "second", Extensions = ["pdf"], CategoryIds = ["documents"] },
            new() { TargetWidgetId = "third", IsEnabled = false, Extensions = [".pdf"] }
        };
        var resolver = new DesktopOrganizationRuleResolver();
        Assert.Contains(resolver.FindConflicts(rules), c =>
            c.Kind == DesktopOrganizationRuleConflictKind.Extension &&
            c.Value.Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
            c.TargetWidgetIds.Count == 2);
        Assert.Contains(resolver.FindConflicts(rules), c =>
            c.Kind == DesktopOrganizationRuleConflictKind.Category && c.TargetWidgetIds.Count == 2);
        resolver.AssignExtensionExclusively(rules, "second", ".PDF");
        Assert.DoesNotContain(rules[0].Extensions, e => e.Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(".pdf", rules[1].Extensions);
        Assert.DoesNotContain(resolver.FindConflicts(rules), c => c.Kind == DesktopOrganizationRuleConflictKind.Extension);
    }

    [Fact]
    public void Resolve_TiesBreakOnRuleId_NotWidgetName()
    {
        var first = new DesktopOrganizationRule { Id = "z", TargetWidgetId = "first", Extensions = [".pdf"] };
        var second = new DesktopOrganizationRule { Id = "a", TargetWidgetId = "second", Extensions = [".pdf"] };
        Assert.Same(second, new DesktopOrganizationRuleResolver().Resolve(Snapshot(".pdf"),
            [first, second], [Widget("first"), Widget("second")]));
    }
}
