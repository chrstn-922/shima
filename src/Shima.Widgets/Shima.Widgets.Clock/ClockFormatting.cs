using System.Globalization;
using Shima.Core.Contracts;

namespace Shima.Widgets.Clock;

public sealed record ClockReadout(
    string DateText,
    string TimeText,
    string AmPmText,
    bool HasAmPm,
    string UtcOffsetLabel,
    string LocationSubtitle,
    string AccessibleStatus);

/// <summary>
/// Deterministic offline timezone conversion, DST offset calculation, 12h/24h formatting, and boundary timer math.
/// </summary>
public static class ClockFormatting
{
    private static readonly TimeSpan MinimumTimerDelay = TimeSpan.FromMilliseconds(50);

    public static TimeZoneInfo ResolveTimeZone(string windowsTzId, string? ianaTzId = null)
    {
        if (!string.IsNullOrWhiteSpace(windowsTzId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(windowsTzId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        if (!string.IsNullOrWhiteSpace(ianaTzId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(ianaTzId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }

    public static ClockReadout FormatReadout(
        DateTimeOffset utcNow,
        WorldClockLocation location,
        ClockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(location);
        settings ??= new ClockSettings();

        var tz = ResolveTimeZone(location.WindowsTzId, location.IanaTzId);
        var localTime = TimeZoneInfo.ConvertTime(utcNow, tz);
        var dateText = localTime.ToString("dddd, MMMM d", CultureInfo.InvariantCulture);

        string timeText;
        string amPmText;
        bool hasAmPm;

        if (settings.Use24HourFormat)
        {
            timeText = localTime.ToString(settings.ShowSeconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);
            amPmText = string.Empty;
            hasAmPm = false;
        }
        else
        {
            timeText = localTime.ToString(settings.ShowSeconds ? "h:mm:ss" : "h:mm", CultureInfo.InvariantCulture);
            amPmText = localTime.ToString("tt", CultureInfo.InvariantCulture);
            hasAmPm = true;
        }

        var utcOffsetLabel = FormatUtcOffset(localTime.Offset);
        var subtitle = $"{location.CityName} · {utcOffsetLabel}";
        var fullTimeForA11y = hasAmPm ? $"{timeText} {amPmText}" : timeText;
        var accessibleStatus = $"Selected city {location.CityName}, {utcOffsetLabel}, {fullTimeForA11y}, {dateText}";

        return new ClockReadout(
            dateText,
            timeText,
            amPmText,
            hasAmPm,
            utcOffsetLabel,
            subtitle,
            accessibleStatus);
    }

    public static string FormatCompactTime(DateTimeOffset utcNow, WorldClockLocation location, ClockSettings settings)
    {
        var tz = ResolveTimeZone(location.WindowsTzId, location.IanaTzId);
        var localTime = TimeZoneInfo.ConvertTime(utcNow, tz);
        return settings.Use24HourFormat
            ? localTime.ToString("HH:mm", CultureInfo.InvariantCulture)
            : localTime.ToString("h:mm tt", CultureInfo.InvariantCulture);
    }

    public static string FormatUtcOffset(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero)
        {
            return "UTC+0";
        }

        var totalMinutes = (int)Math.Round(offset.TotalMinutes);
        var sign = totalMinutes >= 0 ? "+" : "-";
        var abs = TimeSpan.FromMinutes(Math.Abs(totalMinutes));
        var wholeHours = (int)abs.TotalHours;
        return abs.Minutes == 0
            ? $"UTC{sign}{wholeHours}"
            : $"UTC{sign}{wholeHours}:{abs.Minutes:00}";
    }

    public static TimeSpan CalculateDelayUntilNextBoundary(DateTimeOffset utcNow, bool showSeconds)
    {
        var intervalTicks = showSeconds ? TimeSpan.TicksPerSecond : TimeSpan.TicksPerMinute;
        var remainder = utcNow.UtcTicks % intervalTicks;
        var remainingTicks = remainder == 0 ? intervalTicks : intervalTicks - remainder;
        var delay = TimeSpan.FromTicks(remainingTicks);
        return delay < MinimumTimerDelay ? MinimumTimerDelay : delay;
    }
}
