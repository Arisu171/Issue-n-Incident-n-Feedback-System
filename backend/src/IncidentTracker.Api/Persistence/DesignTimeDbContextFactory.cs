using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IncidentTracker.Api.Persistence;

/// <summary>
/// Cho phép <c>dotnet ef migrations add</c> hoạt động mà không cần khởi động cả web host
/// (không cần DB thật, không cần biến môi trường JWT). Chỉ dùng ở design time.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("CONNECTIONSTRINGS__DEFAULT")
            ?? "Host=localhost;Port=5432;Database=incident_tracker;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AppDbContext(options);
    }
}
