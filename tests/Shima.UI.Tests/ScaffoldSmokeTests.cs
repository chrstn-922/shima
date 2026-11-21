using System.IO;
using FluentAssertions;
using Shima.Presentation;
using Xunit;

namespace Shima.UI.Tests;

public sealed class ScaffoldSmokeTests
{
    [Fact]
    public void PresentationAndAppManifest_ShouldSupportPerMonitorV2WithoutSystemDrawingCommon()
    {
        PresentationAssemblyMarker.Layer.Should().Be("Presentation");

        var refs = typeof(PresentationAssemblyMarker).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        refs.Should().NotContain("System.Drawing.Common");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Shima.App", "app.manifest")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("repository root containing src/Shima.App/app.manifest should be discoverable");
        var manifest = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Shima.App", "app.manifest"));
        manifest.Should().Contain("PerMonitorV2");
        manifest.Should().Contain("asInvoker");
    }
}
