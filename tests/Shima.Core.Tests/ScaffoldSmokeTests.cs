using FluentAssertions;
using Xunit;

namespace Shima.Core.Tests;

public sealed class ScaffoldSmokeTests
{
    [Fact]
    public void CoreAssembly_ShouldLoadWithoutUiOrOsDependencies()
    {
        var coreAssembly = typeof(CoreAssemblyMarker).Assembly;
        var referencedNames = coreAssembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        CoreAssemblyMarker.Layer.Should().Be("Core");
        referencedNames.Should().NotContain(name =>
            name != null && (name.Contains("PresentationFramework") || name.Contains("Shima.WindowsInterop")));
    }
}
