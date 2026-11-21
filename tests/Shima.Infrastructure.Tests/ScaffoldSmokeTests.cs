using FluentAssertions;
using Microsoft.Data.Sqlite;
using Shima.Infrastructure;
using Xunit;

namespace Shima.Infrastructure.Tests;

public sealed class ScaffoldSmokeTests
{
    [Fact]
    public void InfrastructureAssembly_AndSqliteProvider_ShouldLoad()
    {
        InfrastructureAssemblyMarker.Layer.Should().Be("Infrastructure");
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        conn.State.Should().Be(System.Data.ConnectionState.Open);
    }
}
