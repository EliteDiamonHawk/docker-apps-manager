using DockApps.Core;
using Xunit;

namespace DockApps.Core.Tests;

public sealed class CoreTests
{
    [Fact] public void RegisteredApp_IsIndependentOfUi() { var app = new RegisteredApp { Id = "demo", Name = "Demo", ComposeFile = "compose.yml", ProjectName = "demo" }; Assert.Equal("demo", app.Id); Assert.True(app.OpenBrowser); }
    [Fact] public void OperationResult_TracksSuccess() { Assert.True(OperationResult.Success().Succeeded); Assert.False(OperationResult.Failure("bad").Succeeded); }
    [Fact] public async Task Registry_RejectsDuplicateIds()
    {
        var root = Path.Combine(Path.GetTempPath(), "dockapps-tests", Guid.NewGuid().ToString("N")); var registry = new JsonAppRegistry(root); await registry.AddAsync(new RegisteredApp { Id = "same", Name = "One", ComposeFile = "one.yml", ProjectName = "one" }); await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AddAsync(new RegisteredApp { Id = "same", Name = "Two", ComposeFile = "two.yml", ProjectName = "two" }));
    }
}
