using DeskNest.Platform;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class JevCredentialStoreTests
{
    [Fact]
    public void ExplicitCredentialRoundTripUsesAnIsolatedTarget()
    {
        var store = new JevCredentialStore("DeskNext/Test/" + Guid.NewGuid().ToString("N"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<PlatformNotSupportedException>(() => store.Load());
            Assert.Throws<PlatformNotSupportedException>(() => store.Save("test-only-key"));
            Assert.Throws<PlatformNotSupportedException>(() => store.Delete());
            return;
        }
        try
        {
            Assert.Null(store.Load());
            store.Save("test-only-密钥-🔑");
            Assert.Equal("test-only-密钥-🔑", store.Load());
            Assert.Throws<ArgumentException>(() => store.Save("bad key"));
            Assert.Throws<ArgumentException>(() => store.Save(new string('x', 2561)));
            Assert.Equal("test-only-密钥-🔑", store.Load());
            store.Save("test-only-replacement");
            Assert.Equal("test-only-replacement", store.Load());
            store.Delete();
            Assert.Null(store.Load());
            store.Delete();
        }
        finally { store.Delete(); }
    }
}
