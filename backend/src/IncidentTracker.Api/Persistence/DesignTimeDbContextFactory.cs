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
            ?? "Host=localhost;Port=5432;Database=gitissues;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            // Cùng lý do như Program.cs: `dotnet ef` cũng phải tra bảng lịch sử đúng schema.
            .UseNpgsql(connectionString, npg =>
            {
                var schema = new Npgsql.NpgsqlConnectionStringBuilder(connectionString)
                    .SearchPath?.Split(',')[0].Trim();
                if (!string.IsNullOrWhiteSpace(schema))
                {
                    npg.MigrationsHistoryTable("__EFMigrationsHistory", schema);
                }
            })
            .Options;

        return new AppDbContext(options);
    }
}
