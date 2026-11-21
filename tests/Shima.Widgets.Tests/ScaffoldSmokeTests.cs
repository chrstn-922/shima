using FluentAssertions;
using Shima.Widgets.Clipboard;
using Shima.Widgets.Clock;
using Shima.Widgets.Developer;
using Shima.Widgets.FocusTimer;
using Shima.Widgets.Inbox;
using Shima.Widgets.Notes;
using Shima.Widgets.NowPlaying;
using Shima.Widgets.VolumeMixer;
using Xunit;

namespace Shima.Widgets.Tests;

public sealed class ScaffoldSmokeTests
{
    [Fact]
    public void AllEightWidgetModules_ShouldDefineDistinctWidgetIds()
    {
        var ids = new[]
        {
            ClockWidgetMarker.WidgetId,
            NowPlayingWidgetMarker.WidgetId,
            FocusTimerWidgetMarker.WidgetId,
            NotesWidgetMarker.WidgetId,
            InboxWidgetMarker.WidgetId,
            ClipboardWidgetMarker.WidgetId,
            VolumeMixerWidgetMarker.WidgetId,
            DeveloperWidgetMarker.WidgetId
        };

        ids.Should().OnlyHaveUniqueItems();
        ids.Should().HaveCount(8);
    }
}
