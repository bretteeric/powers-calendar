using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Dtos;
using OfficeCal.Infrastructure;
using Xunit;

namespace OfficeCal.Tests.Fixtures;

/// <summary>
/// 整合測試用的站台，掛在自己的 LocalDB 資料庫上。
/// IAsyncLifetime 採「明確介面實作」：WebApplicationFactory 已有一個回傳 ValueTask 的
/// DisposeAsync，直接宣告同名的 public 方法會與它打架。
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string Master =
        @"Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true";

    public string DatabaseName { get; } = $"OfficeCalApi_{Guid.NewGuid():N}";

    public string ConnectionString =>
        $@"Server=(localdb)\MSSQLLocalDB;Database={DatabaseName};Integrated Security=true;" +
        "MultipleActiveResultSets=true;TrustServerCertificate=true";

    /// <summary>與站台的序列化設定一致：camelCase + 列舉用字串。</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.UseSetting("ConnectionStrings:Default", ConnectionString);

    public OfficeCalDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<OfficeCalDbContext>()
            .UseSqlServer(ConnectionString).Options;
        return new OfficeCalDbContext(options);
    }

    /// <summary>回傳一個已登入的 HttpClient。</summary>
    public async Task<HttpClient> LoginAsync(string employeeNo, string password)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false,
        });
        var res = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest { EmployeeNo = employeeNo, Password = password });
        res.EnsureSuccessStatusCode();
        return client;
    }

    Task IAsyncLifetime.InitializeAsync()
    {
        // 觸發站台啟動，Program.cs 內的 DbSeeder 會建立資料庫與種子資料
        _ = CreateClient();
        return Task.CompletedTask;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                          $"DROP DATABASE [{DatabaseName}];";
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("Api")]
public class ApiCollection : ICollectionFixture<ApiFactory> { }
