using System.IO;
using FluentAssertions;
using Shima.WindowsInterop.System;
using Windows.Win32;
using Xunit;

namespace Shima.WindowsInterop.Tests;

public sealed class BrandIconTrayTests
{
    [Fact]
    public void NativeTrayIconHost_TryLoadIconFromFile_LoadsMultiSizeShimaIcoCleanly()
    {
        var icoPath = Path.Combine(FindRepoRoot(), "src", "Shima.App", "Assets", "Shima.ico");
        File.Exists(icoPath).Should().BeTrue();

        var loaded = NativeTrayIconHost.TryLoadIconFromFile(icoPath, out var hicon);
        try
        {
            loaded.Should().BeTrue();
            hicon.IsNull.Should().BeFalse();
        }
        finally
        {
            if (!hicon.IsNull)
            {
                PInvoke.DestroyIcon(hicon).Value.Should().NotBe(0);
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Shima.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate Shima.sln from test base directory.");
    }
}
