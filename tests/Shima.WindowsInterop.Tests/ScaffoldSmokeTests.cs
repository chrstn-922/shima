using FluentAssertions;
using NAudio.CoreAudioApi;
using Shima.WindowsInterop;
using Xunit;

namespace Shima.WindowsInterop.Tests;

public sealed class ScaffoldSmokeTests
{
    [Fact]
    public void WindowsInteropAssembly_ShouldExposeCsWin32AndNAudioWasapi()
    {
        WindowsInteropAssemblyMarker.Layer.Should().Be("WindowsInterop");

        var pinvokeType = typeof(WindowsInteropAssemblyMarker).Assembly.GetType("Windows.Win32.PInvoke");
        pinvokeType.Should().NotBeNull("CsWin32 should generate PInvoke bindings in Shima.WindowsInterop");

        typeof(MMDeviceEnumerator).Assembly.GetName().Name.Should().Be("NAudio.Wasapi");
        Shima.AgentBridge.Program.Main([]).Should().Be(0);
    }
}
