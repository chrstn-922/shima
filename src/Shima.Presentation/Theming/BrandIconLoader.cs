using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Shima.Presentation.Theming;

/// <summary>
/// Loads Shima's multi-size brand icon from embedded assembly resources without System.Drawing or pack:// dependencies.
/// </summary>
internal static class BrandIconLoader
{
    private const string EmbeddedIconResourceName = "Shima.Presentation.Assets.Shima.ico";

    public static ImageSource? TryLoadWindowIcon()
    {
        try
        {
            using var resourceStream = typeof(BrandIconLoader).Assembly.GetManifestResourceStream(EmbeddedIconResourceName);
            if (resourceStream is null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            resourceStream.CopyTo(buffer);
            buffer.Position = 0;

            var frame = BitmapFrame.Create(
                buffer,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }
}
