using Xunit;

namespace DeskNest.Core.Tests;

public sealed class FlowNativeFactAttribute : FactAttribute
{
    public FlowNativeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DESKNEXT_FLOW_NATIVE_LIBRARY")))
            Skip = "Requires an explicitly supplied compiled Pogget bridge; a skip is not native validation evidence.";
    }
}
