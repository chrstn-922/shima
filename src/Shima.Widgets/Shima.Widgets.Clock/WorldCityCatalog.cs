using System.Globalization;
using Shima.Core.Contracts;

namespace Shima.Widgets.Clock;

public sealed record CatalogCityEntry(
    string CityName,
    string CountryName,
    string WindowsTzId,
    string IanaTzId)
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(CountryName)
        ? CityName
        : $"{CityName}, {CountryName}";
}

/// <summary>
/// Offline searchable global timezone/city catalog resolved strictly via Windows TimeZoneInfo (zero network calls).
/// </summary>
public static class WorldCityCatalog
{
    private static readonly IReadOnlyList<CatalogCityEntry> BuiltInCities =
    [
        new("Manila", "Philippines", "Singapore Standard Time", "Asia/Manila"),
        new("London", "United Kingdom", "GMT Standard Time", "Europe/London"),
        new("New York", "United States", "Eastern Standard Time", "America/New_York"),
        new("Tokyo", "Japan", "Tokyo Standard Time", "Asia/Tokyo"),
        new("Los Angeles", "United States", "Pacific Standard Time", "America/Los_Angeles"),
        new("Chicago", "United States", "Central Standard Time", "America/Chicago"),
        new("Denver", "United States", "Mountain Standard Time", "America/Denver"),
        new("Honolulu", "United States", "Hawaiian Standard Time", "Pacific/Honolulu"),
        new("Anchorage", "United States", "Alaskan Standard Time", "America/Anchorage"),
        new("Toronto", "Canada", "Eastern Standard Time", "America/Toronto"),
        new("Vancouver", "Canada", "Pacific Standard Time", "America/Vancouver"),
        new("Mexico City", "Mexico", "Central Standard Time (Mexico)", "America/Mexico_City"),
        new("Sao Paulo", "Brazil", "E. South America Standard Time", "America/Sao_Paulo"),
        new("Buenos Aires", "Argentina", "Argentina Standard Time", "America/Argentina/Buenos_Aires"),
        new("UTC", "Coordinated Universal Time", "UTC", "Etc/UTC"),
        new("Dublin", "Ireland", "GMT Standard Time", "Europe/Dublin"),
        new("Paris", "France", "Romance Standard Time", "Europe/Paris"),
        new("Berlin", "Germany", "W. Europe Standard Time", "Europe/Berlin"),
        new("Zurich", "Switzerland", "W. Europe Standard Time", "Europe/Zurich"),
        new("Madrid", "Spain", "Romance Standard Time", "Europe/Madrid"),
        new("Rome", "Italy", "W. Europe Standard Time", "Europe/Rome"),
        new("Amsterdam", "Netherlands", "W. Europe Standard Time", "Europe/Amsterdam"),
        new("Athens", "Greece", "GTB Standard Time", "Europe/Athens"),
        new("Cairo", "Egypt", "Egypt Standard Time", "Africa/Cairo"),
        new("Johannesburg", "South Africa", "South Africa Standard Time", "Africa/Johannesburg"),
        new("Dubai", "United Arab Emirates", "Arabian Standard Time", "Asia/Dubai"),
        new("Kolkata", "India", "India Standard Time", "Asia/Kolkata"),
        new("Mumbai", "India", "India Standard Time", "Asia/Kolkata"),
        new("Kathmandu", "Nepal", "Nepal Standard Time", "Asia/Kathmandu"),
        new("Bangkok", "Thailand", "SE Asia Standard Time", "Asia/Bangkok"),
        new("Jakarta", "Indonesia", "SE Asia Standard Time", "Asia/Jakarta"),
        new("Singapore", "Singapore", "Singapore Standard Time", "Asia/Singapore"),
        new("Hong Kong", "China", "China Standard Time", "Asia/Hong_Kong"),
        new("Taipei", "Taiwan", "Taipei Standard Time", "Asia/Taipei"),
        new("Shanghai", "China", "China Standard Time", "Asia/Shanghai"),
        new("Seoul", "South Korea", "Korea Standard Time", "Asia/Seoul"),
        new("Sydney", "Australia", "AUS Eastern Standard Time", "Australia/Sydney"),
        new("Melbourne", "Australia", "AUS Eastern Standard Time", "Australia/Melbourne"),
        new("Adelaide", "Australia", "Cen. Australia Standard Time", "Australia/Adelaide"),
        new("Auckland", "New Zealand", "New Zealand Standard Time", "Pacific/Auckland")
    ];

    public static IReadOnlyList<CatalogCityEntry> AllCities => BuiltInCities;

    public static IReadOnlyList<WorldClockLocation> CreateDefaultLocations() =>
    [
        CreateLocation(BuiltInCities[0], sortOrder: 0, isPrimary: true, fixedId: "default.manila"),
        CreateLocation(BuiltInCities[1], sortOrder: 1, isPrimary: false, fixedId: "default.london"),
        CreateLocation(BuiltInCities[2], sortOrder: 2, isPrimary: false, fixedId: "default.newyork"),
        CreateLocation(BuiltInCities[3], sortOrder: 3, isPrimary: false, fixedId: "default.tokyo")
    ];

    public static WorldClockLocation CreateLocation(
        CatalogCityEntry entry,
        int sortOrder,
        bool isPrimary,
        string? fixedId = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new WorldClockLocation
        {
            LocationId = fixedId ?? $"clock.{Guid.NewGuid():N}",
            CityName = entry.CityName,
            CountryName = entry.CountryName,
            WindowsTzId = entry.WindowsTzId,
            IanaTzId = entry.IanaTzId,
            SortOrder = Math.Max(0, sortOrder),
            IsPrimary = isPrimary
        };
    }

    public static IReadOnlyList<CatalogCityEntry> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BuiltInCities;
        }

        var term = query.Trim();
        var results = BuiltInCities
            .Where(c =>
                c.CityName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || c.CountryName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || c.IanaTzId.Contains(term, StringComparison.OrdinalIgnoreCase)
                || c.WindowsTzId.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (results.Count > 0)
        {
            return results;
        }

        // Fall back across OS system timezones when searching an unlisted region or offset.
        return TimeZoneInfo.GetSystemTimeZones()
            .Where(tz =>
                tz.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || tz.StandardName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || tz.Id.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .Select(tz =>
            {
                var iana = TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var mapped) && !string.IsNullOrWhiteSpace(mapped)
                    ? mapped
                    : tz.Id;
                return new CatalogCityEntry(tz.StandardName, string.Format(CultureInfo.InvariantCulture, "{0}", tz.DisplayName), tz.Id, iana);
            })
            .ToArray();
    }
}
