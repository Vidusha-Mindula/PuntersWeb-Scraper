using PuntersScraper.App.Services;

namespace PuntersScraper.Tests;

public class MachineIdentityTests
{
    [Fact]
    public void Resolve_UsesMachineGuidFromReader()
    {
        var identity = MachineIdentity.Resolve(() => "0b5c4a2e-1f3d-4e6a-9b8c-7d2e1f0a3b4c");

        Assert.Equal("0b5c4a2e-1f3d-4e6a-9b8c-7d2e1f0a3b4c", identity.MachineGuid);
    }

    [Fact]
    public void Resolve_PopulatesMachineNameUserNameAndVersion()
    {
        var identity = MachineIdentity.Resolve(() => null);

        Assert.Equal(Environment.MachineName, identity.MachineName);
        Assert.Equal(Environment.UserName, identity.UserName);
        Assert.Equal(UpdateChecker.CurrentVersionText, identity.ApplicationVersion);
    }

    [Fact]
    public void Resolve_ReaderThrows_MachineGuidIsNullAndDoesNotThrow()
    {
        var identity = MachineIdentity.Resolve(() => throw new UnauthorizedAccessException("locked down"));

        Assert.Null(identity.MachineGuid);
        Assert.NotNull(identity.MachineName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankMachineGuid_IsNull(string? value)
    {
        Assert.Null(MachineIdentity.Resolve(() => value).MachineGuid);
    }

    [Fact]
    public void ReadMachineGuidFromRegistry_ReturnsValidGuidOnWindows()
    {
        var value = MachineIdentity.ReadMachineGuidFromRegistry();

        Assert.True(Guid.TryParse(value, out _), $"Expected a GUID but got '{value}'.");
    }

    [Fact]
    public void Current_IsCachedAndHasMachineGuid()
    {
        Assert.Same(MachineIdentity.Current, MachineIdentity.Current);
        Assert.NotNull(MachineIdentity.Current.MachineGuid);
    }
}
