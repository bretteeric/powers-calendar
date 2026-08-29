using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OfficeCal.Infrastructure;
using Xunit;

namespace OfficeCal.Tests.Fixtures;

/// <summary>
/// 每個測試集合一個獨立的 LocalDB 資料庫。
/// 不使用 SQLite：UPDLOCK/HOLDLOCK 是 SQL Server 專屬語法，測試環境必須與正式環境一致。
/// </summary>
public class LocalDbFixture : IAsyncLifetime
{
    private const string Master =
        @"Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true";

    public string DatabaseName { get; } = $"OfficeCalTest_{Guid.NewGuid():N}";

    public string ConnectionString =>
        $@"Server=(localdb)\MSSQLLocalDB;Database={DatabaseName};Integrated Security=true;" +
        "MultipleActiveResultSets=true;TrustServerCertificate=true";

    public OfficeCalDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<OfficeCalDbContext>()
            .UseSqlServer(ConnectionString)   // 刻意不啟用 EnableRetryOnFailure：與使用者交易不相容
            .Options;
        return new OfficeCalDbContext(options);
    }

    public async Task InitializeAsync()
    {
        await using var ctx = CreateContext();
        await ctx.Database.MigrateAsync();
    }

    /// <summary>清空所有資料表，讓每個測試從乾淨狀態開始。刪除順序遵守外鍵相依。</summary>
    public async Task ResetAsync()
    {
        await using var ctx = CreateContext();
        await ctx.Database.ExecuteSqlRawAsync(
            "DELETE FROM Notifications; DELETE FROM EventAttendees; DELETE FROM EventOccurrences; " +
            "DELETE FROM Events; DELETE FROM Rooms; DELETE FROM Users; DELETE FROM Departments;");
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{DatabaseName}];";
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("LocalDb")]
public class LocalDbCollection : ICollectionFixture<LocalDbFixture> { }
