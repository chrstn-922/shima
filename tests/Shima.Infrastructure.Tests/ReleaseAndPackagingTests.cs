using System.Buffers.Binary;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Shima.Infrastructure.Tests;

public sealed class ReleaseAndPackagingTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly Regex HardcodedHexPattern = new(@"#[0-9A-Fa-f]{6,8}\b", RegexOptions.Compiled);
    private static readonly Regex SystemDrawingSourcePattern = new(@"using\s+System\.Drawing\b|PackageReference.*System\.Drawing", RegexOptions.Compiled);

    [Fact]
    public void SparseAppxManifest_AndAssets_AreValidAndConsistentWithAppManifest()
    {
        var repoRoot = FindRepoRoot();
        var appxPath = Path.Combine(repoRoot, "build", "sparse-msix", "AppxManifest.xml");
        var appManifestPath = Path.Combine(repoRoot, "src", "Shima.App", "app.manifest");

        File.Exists(appxPath).Should().BeTrue();
        File.Exists(appManifestPath).Should().BeTrue();

        var appxDoc = XDocument.Load(appxPath);
        XNamespace pkgNs = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace uap3Ns = "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";
        XNamespace uap10Ns = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
        XNamespace desktopNs = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
        XNamespace rescapNs = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

        var identity = appxDoc.Root!.Element(pkgNs + "Identity")!;
        var packageName = identity.Attribute("Name")!.Value;
        var publisher = identity.Attribute("Publisher")!.Value;

        appxDoc.Root.Element(pkgNs + "Properties")!.Element(uap10Ns + "AllowExternalContent")!.Value
            .Should().Be("true");
        appxDoc.Root.Element(pkgNs + "Dependencies")!.Element(pkgNs + "TargetDeviceFamily")!
            .Attribute("MinVersion")!.Value.Should().Be("10.0.19041.0");

        var app = appxDoc.Root.Element(pkgNs + "Applications")!.Element(pkgNs + "Application")!;
        var appId = app.Attribute("Id")!.Value;
        app.Attribute("Executable")!.Value.Should().Be("Shima.App.exe");
        app.Attribute("EntryPoint")!.Value.Should().Be("Windows.FullTrustApplication");

        var startupTask = app.Element(pkgNs + "Extensions")!
            .Element(desktopNs + "Extension")!
            .Element(desktopNs + "StartupTask")!;
        startupTask.Attribute("TaskId")!.Value.Should().Be("ShimaStartupTask");

        var capabilities = appxDoc.Root.Element(pkgNs + "Capabilities")!;
        capabilities.Elements(rescapNs + "Capability").Select(e => e.Attribute("Name")!.Value)
            .Should().Contain("runFullTrust");
        capabilities.Elements(uap3Ns + "Capability").Select(e => e.Attribute("Name")!.Value)
            .Should().Contain("userNotificationListener");

        // Verify matching external-location <msix> binding & PerMonitorV2 in Shima.App\app.manifest
        var appManifestDoc = XDocument.Load(appManifestPath);
        XNamespace msixNs = "urn:schemas-microsoft-com:msix.v1";
        XNamespace asmV3Ns = "urn:schemas-microsoft-com:asm.v3";
        XNamespace smi16Ns = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";

        var msixElement = appManifestDoc.Root!.Element(msixNs + "msix");
        msixElement.Should().NotBeNull();
        msixElement!.Attribute("packageName")!.Value.Should().Be(packageName);
        msixElement.Attribute("publisher")!.Value.Should().Be(publisher);
        msixElement.Attribute("applicationId")!.Value.Should().Be(appId);

        appManifestDoc.Descendants(smi16Ns + "dpiAwareness").Single().Value
            .Should().Contain("PerMonitorV2");
        appManifestDoc.Descendants(asmV3Ns + "requestedExecutionLevel").Single()
            .Attribute("level")!.Value.Should().Be("asInvoker");

        // Verify exact PNG dimensions for all Sparse MSIX assets
        AssertPngDimensions(Path.Combine(repoRoot, "build", "sparse-msix", "Assets", "StoreLogo.png"), 50, 50);
        AssertPngDimensions(Path.Combine(repoRoot, "build", "sparse-msix", "Assets", "Square44x44Logo.png"), 44, 44);
        AssertPngDimensions(Path.Combine(repoRoot, "build", "sparse-msix", "Assets", "Square150x150Logo.png"), 150, 150);
    }

    [Fact]
    public void BuildScripts_AndCodeSafeguards_ConformToPhase10Standards()
    {
        var repoRoot = FindRepoRoot();
        var buildDir = Path.Combine(repoRoot, "build");
        foreach (var scriptName in new[]
                 {
                     "register-sparse-identity.ps1",
                     "sign-artifacts.ps1",
                     "build-release.ps1",
                     "verify-release.ps1"
                 })
        {
            var scriptPath = Path.Combine(buildDir, scriptName);
            File.Exists(scriptPath).Should().BeTrue($"build script {scriptName} must exist");
            File.ReadAllText(scriptPath).Length.Should().BeGreaterThan(200);
        }

        var srcRoot = Path.Combine(repoRoot, "src");
        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var content = File.ReadAllText(file);
                SystemDrawingSourcePattern.IsMatch(content).Should().BeFalse(
                    $"System.Drawing is forbidden in {Path.GetRelativePath(repoRoot, file)}");
            }
            else if (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                     && !file.EndsWith("ThemeTokens.xaml", StringComparison.OrdinalIgnoreCase))
            {
                var content = File.ReadAllText(file);
                HardcodedHexPattern.IsMatch(content).Should().BeFalse(
                    $"Hardcoded hex literal found in XAML outside ThemeTokens.xaml: {Path.GetRelativePath(repoRoot, file)}");
            }
        }
    }

    private static void AssertPngDimensions(string filePath, int expectedWidth, int expectedHeight)
    {
        File.Exists(filePath).Should().BeTrue($"asset '{filePath}' must exist");
        var bytes = File.ReadAllBytes(filePath);
        bytes.Length.Should().BeGreaterOrEqualTo(24);
        bytes.AsSpan(0, 8).SequenceEqual(PngSignature).Should().BeTrue();
        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        width.Should().Be(expectedWidth);
        height.Should().Be(expectedHeight);
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
