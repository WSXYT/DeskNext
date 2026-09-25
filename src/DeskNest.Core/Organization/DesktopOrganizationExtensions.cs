namespace DeskNest.Core.Organization;

internal static class DesktopOrganizationExtensions
{
    public static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        string trimmed = extension.Trim();
        return (trimmed.StartsWith('.') ? trimmed : $".{trimmed}").ToLowerInvariant();
    }

}
