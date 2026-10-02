using DockApps.Core;
using Xunit;

namespace DockApps.Core.Tests;

public sealed class HardeningTests
{
    [Fact]
    public void ComposeStatus_IsNotHealthyWhenAnyContainerIsStopped()
    {
        var status = new ComposeProjectStatus("demo", [
            new ContainerStatus("1", "web", "running", "healthy", "demo", "web", "nginx", null),
            new ContainerStatus("2", "db", "exited", "none", "demo", "db", "postgres", 0)
        ]);

        Assert.True(status.IsRunning);
        Assert.False(status.IsHealthy);
    }

    [Fact]
    public void IpcValidation_RejectsInvalidAndAcceptsManagerRequests()
    {
        var valid = new ManagerRequest(IpcProtocol.Version, "open-manager", null, "request-1");
        var invalid = new ManagerRequest(IpcProtocol.Version, "open-app", "bad\nvalue", "request-2");

        Assert.True(IpcProtocol.TryValidateRequest(valid, out var validError), validError);
        Assert.False(IpcProtocol.TryValidateRequest(invalid, out var invalidError));
        Assert.Contains("invalid", invalidError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegistryValidation_RejectsNonHttpUrls()
    {
        var root = Path.Combine(Path.GetTempPath(), "dockapps-tests", Guid.NewGuid().ToString("N"));
        var registry = new JsonAppRegistry(root);
        var app = new RegisteredApp { Id = "demo", Name = "Demo", ComposeFile = "compose.yml", ProjectName = "demo", Url = "file:///unsafe" };
        Assert.Throws<InvalidOperationException>(() => registry.AddAsync(app).GetAwaiter().GetResult());
    }
}
