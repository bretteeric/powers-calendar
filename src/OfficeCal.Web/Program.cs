using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Common;
using OfficeCal.Core.Enums;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;
using OfficeCal.Services;
using OfficeCal.Web.Infrastructure;
using OfficeCal.Web.Middleware;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<OfficeCalDbContext>(o =>
    // 刻意不使用 EnableRetryOnFailure：本系統自行管理交易，重試執行策略與之不相容。
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUserContext, HttpUserContext>();
builder.Services.AddSingleton<IPasswordService, PasswordService>();

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IRoomRepository, RoomRepository>();
builder.Services.AddScoped<IEventOccurrenceRepository, EventOccurrenceRepository>();

builder.Services.AddScoped<IRecurrenceService, RecurrenceService>();
builder.Services.AddScoped<IBookingService, BookingService>();
// 任務 9–14 會在此陸續加入 INotificationService、IEventService、IRoomService、IIcsService、IUserService

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "OfficeCal.Auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        o.LoginPath = "/Login";
        o.Events.OnRedirectToLogin = ctx =>
        {
            // API 路徑不重導，直接回 401 讓前端攔截器處理
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization(o =>
    o.AddPolicy("Admin", p => p.RequireRole(nameof(UserRole.Admin))));

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        // 規格 7.1 的請求範例用字串表示列舉（"Weekly"、["Monday"]），必須加這個轉換器
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddRazorPages();

// 模型驗證失敗也走統一信封
builder.Services.Configure<ApiBehaviorOptions>(o =>
{
    o.InvalidModelStateResponseFactory = ctx =>
    {
        var errors = ctx.ModelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage)
            .ToList();
        return new BadRequestObjectResult(ApiResponse.Fail("輸入資料不正確", errors));
    };
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OfficeCalDbContext>();
    var pwd = scope.ServiceProvider.GetRequiredService<IPasswordService>();
    await DbSeeder.SeedAsync(db, (u, p) => pwd.Hash(u, p), () => pwd.NewFeedToken());
}

app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapRazorPages();

app.Run();

/// <summary>供 WebApplicationFactory 在整合測試中取用。</summary>
public partial class Program { }
