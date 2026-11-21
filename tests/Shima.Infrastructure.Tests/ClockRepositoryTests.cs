using FluentAssertions;
using Shima.Core.Contracts;
using Shima.Infrastructure.Persistence;
using Shima.Infrastructure.Repositories;
using Xunit;

namespace Shima.Infrastructure.Tests;

public sealed class ClockRepositoryTests : TempDirectoryTest
{
    private async Task<ClockRepository> CreateInitializedRepositoryAsync()
    {
        var dbPath = Path.Combine(Root, "data", "shima.db");
        await new SqliteMigrationRunner(dbPath).InitializeAsync();
        return new ClockRepository(dbPath);
    }

    [Fact]
    public async Task UpsertGetSetPrimaryUpdateOrderReplaceAndDelete_RoundTripsCorrectly()
    {
        var repo = await CreateInitializedRepositoryAsync();

        var manila = new WorldClockLocation
        {
            LocationId = "loc-manila",
            CityName = "Manila",
            CountryName = "Philippines",
            WindowsTzId = "Singapore Standard Time",
            IanaTzId = "Asia/Manila",
            SortOrder = 0,
            IsPrimary = true
        };
        var london = new WorldClockLocation
        {
            LocationId = "loc-london",
            CityName = "London",
            CountryName = "United Kingdom",
            WindowsTzId = "GMT Standard Time",
            IanaTzId = "Europe/London",
            SortOrder = 1,
            IsPrimary = false
        };
        var tokyo = new WorldClockLocation
        {
            LocationId = "loc-tokyo",
            CityName = "Tokyo",
            CountryName = "Japan",
            WindowsTzId = "Tokyo Standard Time",
            IanaTzId = "Asia/Tokyo",
            SortOrder = 2,
            IsPrimary = false
        };

        await repo.UpsertAsync(manila);
        await repo.UpsertAsync(london);
        await repo.UpsertAsync(tokyo);

        var loaded = await repo.GetAllAsync();
        loaded.Select(c => c.CityName).Should().Equal("Manila", "London", "Tokyo");
        loaded.Single(c => c.IsPrimary).LocationId.Should().Be("loc-manila");

        await repo.SetPrimaryAsync("loc-tokyo");
        loaded = await repo.GetAllAsync();
        loaded.Where(c => c.IsPrimary).Select(c => c.LocationId).Should().Equal("loc-tokyo");

        await repo.UpdateOrderAsync(["loc-tokyo", "loc-london", "loc-manila"]);
        loaded = await repo.GetAllAsync();
        loaded.Select(c => c.CityName).Should().Equal("Tokyo", "London", "Manila");
        loaded.Select(c => c.SortOrder).Should().Equal(0, 1, 2);

        await repo.DeleteAsync("loc-london");
        (await repo.GetAllAsync()).Select(c => c.CityName).Should().Equal("Tokyo", "Manila");

        await repo.ReplaceAllAsync([manila]);
        loaded = await repo.GetAllAsync();
        loaded.Should().ContainSingle(c => c.LocationId == "loc-manila" && c.IsPrimary);
    }

    [Fact]
    public async Task UpsertAsync_RejectsUnknownWindowsTimeZoneAndInvalidCityName()
    {
        var repo = await CreateInitializedRepositoryAsync();

        await Assert.ThrowsAsync<TimeZoneNotFoundException>(() =>
            repo.UpsertAsync(new WorldClockLocation
            {
                LocationId = "invalid-tz",
                CityName = "Atlantis",
                WindowsTzId = "Not A Real Windows TimeZone",
                IanaTzId = "Mars/Olympus"
            }));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.UpsertAsync(new WorldClockLocation
            {
                LocationId = "blank-city",
                CityName = "   ",
                WindowsTzId = "UTC",
                IanaTzId = "Etc/UTC"
            }));
    }
}
