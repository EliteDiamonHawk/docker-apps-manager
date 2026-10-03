using DockApps.Core;
using Xunit;

namespace DockApps.Core.Tests;

public sealed class ComposeServiceTests
{
    [Fact]
    public async Task ComposeStatus_ParsesSingleServiceJsonObject()
    {
        var runner = new StubProcessRunner(
            "{\"ID\":\"demo-web-1\",\"Name\":\"demo-web-1\",\"Project\":\"demo\",\"Service\":\"web\",\"State\":\"running\",\"Health\":\"healthy\",\"ExitCode\":0,\"Publishers\":null}\n");
        var status = await new ComposeService(runner).GetProjectStatusAsync(App());

        var container = Assert.Single(status.Containers);
        Assert.Null(status.Diagnostic);
        Assert.Equal("demo-web-1", container.Name);
        Assert.Equal("web", container.Service);
        Assert.Equal("running", container.State);
        Assert.Equal("healthy", container.Health);
        Assert.True(status.IsRunning);
        Assert.True(status.IsHealthy);
    }

    [Fact]
    public async Task ComposeStatus_ParsesJsonLinesForMultipleServices()
    {
        // This is the format emitted by `docker compose ps -a --format json`:
        // one JSON object per container, not a JSON array.
        var output = string.Join(Environment.NewLine,
            "{\"ID\":\"demo-web-1\",\"Name\":\"demo-web-1\",\"Project\":\"demo\",\"Service\":\"web\",\"State\":\"running\",\"Health\":\"healthy\",\"ExitCode\":0,\"Publishers\":null}",
            "{\"ID\":\"demo-db-1\",\"Name\":\"demo-db-1\",\"Project\":\"demo\",\"Service\":\"db\",\"State\":\"exited\",\"Health\":\"none\",\"ExitCode\":1,\"Publishers\":null}");
        var status = await new ComposeService(new StubProcessRunner(output)).GetProjectStatusAsync(App());

        Assert.Null(status.Diagnostic);
        Assert.Equal("web", status.Containers[0].Service);
        Assert.Equal("db", status.Containers[1].Service);
        Assert.True(status.IsRunning);
        Assert.False(status.IsHealthy);
        Assert.Equal(1, status.Containers[1].ExitCode);
    }

    [Fact]
    public async Task ComposeStatus_StillParsesLegacyJsonArray()
    {
        var output = "[{\"ID\":\"demo-web-1\",\"Name\":\"demo-web-1\",\"Project\":\"demo\",\"Service\":\"web\",\"State\":\"running\",\"Health\":\"\",\"ExitCode\":0}]";
        var status = await new ComposeService(new StubProcessRunner(output)).GetProjectStatusAsync(App());

        Assert.Null(status.Diagnostic);
        Assert.Single(status.Containers);
        Assert.Equal("web", status.Containers[0].Service);
    }

    private static RegisteredApp App() => new()
    {
        Id = "demo",
        Name = "Demo",
        ComposeFile = "compose.yml",
        ProjectName = "demo"
    };

    private sealed class StubProcessRunner(string standardOutput, int exitCode = 0) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(exitCode, standardOutput, ""));
    }
}
