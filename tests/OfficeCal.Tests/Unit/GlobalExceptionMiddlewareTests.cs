using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeCal.Core.Exceptions;
using OfficeCal.Web.Middleware;
using Xunit;

namespace OfficeCal.Tests.Unit;

public class GlobalExceptionMiddlewareTests
{
    private static async Task<(int status, JsonElement body)> RunAsync(Exception toThrow)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();

        var mw = new GlobalExceptionMiddleware(_ => throw toThrow,
                                               NullLogger<GlobalExceptionMiddleware>.Instance);
        await mw.InvokeAsync(ctx);

        ctx.Response.Body.Position = 0;
        var json = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, JsonDocument.Parse(json).RootElement);
    }

    [Fact]
    public async Task 驗證例外對應四百()
    {
        var (status, body) = await RunAsync(new ValidationException("欄位不合法"));
        Assert.Equal(400, status);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("欄位不合法", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task 找不到例外對應四百零四()
        => Assert.Equal(404, (await RunAsync(new NotFoundException("查無事件"))).status);

    [Fact]
    public async Task 權限例外對應四百零三()
        => Assert.Equal(403, (await RunAsync(new ForbiddenException("不可修改他人事件"))).status);

    [Fact]
    public async Task 衝突例外對應四百零九且帶明細()
    {
        var conflicts = new List<ConflictDetail>
        {
            new()
            {
                OccurrenceId = 881, RoomName = "A 棟 3F 大會議廳",
                StartAt = new DateTime(2026, 9, 14, 10, 0, 0),
                EndAt = new DateTime(2026, 9, 14, 11, 0, 0),
                OwnerName = "陳大明", Title = "季度檢討會",
            },
        };
        var (status, body) = await RunAsync(new ConflictException("會議廳於下列時段已被預約", conflicts));

        Assert.Equal(409, status);
        var first = body.GetProperty("data").GetProperty("conflicts")[0];
        Assert.Equal(881, first.GetProperty("occurrenceId").GetInt32());
        Assert.Equal("季度檢討會", first.GetProperty("title").GetString());
    }

    [Fact]
    public async Task 未預期例外對應五百且不外洩細節()
    {
        var (status, body) = await RunAsync(new InvalidOperationException("內部索引損毀"));
        Assert.Equal(500, status);
        Assert.DoesNotContain("索引", body.GetProperty("message").GetString());
    }
}
