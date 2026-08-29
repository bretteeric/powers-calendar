using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
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
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();

builder.Services.AddScoped<IRecurrenceService, RecurrenceService>();
builder.Services.AddScoped<IBookingService, BookingService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IEventRepository, EventRepository>();
builder.Services.AddScoped<IEventService, EventService>();
builder.Services.AddScoped<IRoomService, RoomService>();
builder.Services.AddScoped<IIcsService, IcsService>();
builder.Services.AddScoped<IUserService, UserService>();

// 與 GlobalExceptionMiddleware 一致的信封序列化設定：Cookie 驗證事件在 DI 容器組裝階段
// 執行，拿不到 MVC 的 JsonOptions，所以在這裡自建一份同樣是 camelCase 的設定共用。
var authEnvelopeJson = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "OfficeCal.Auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        o.LoginPath = "/Login";
        o.Events.OnRedirectToLogin = async ctx =>
        {
            // API 路徑不重導，直接回統一信封的 401 讓前端攔截器處理（規格 3：所有 /api/v1/* 回應都走信封）
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync(
                    JsonSerializer.Serialize(ApiResponse.Fail("請先登入"), authEnvelopeJson));
                return;
            }
            ctx.Response.Redirect(ctx.RedirectUri);
        };
        o.Events.OnRedirectToAccessDenied = async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(
                JsonSerializer.Serialize(ApiResponse.Fail("沒有權限執行此操作"), authEnvelopeJson));
        };
        // 任務 14 審查重要 2：角色與啟用狀態是登入當下烘進 cookie 的宣告，12 小時效期內
        // 若帳號被停用或降級，既有 session 原本完全不受影響。這裡每個請求都重讀一次資料庫，
        // 只要 IsActive 變 false，或目前角色與 cookie 內宣告的角色不同，就拒絕這個 principal
        // 並登出——內部系統規模下，每請求一次 DB 讀取可以接受，不需要快取層或 token 版本機制。
        o.Events.OnValidatePrincipal = async ctx =>
        {
            var idValue = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var roleValue = ctx.Principal?.FindFirstValue(ClaimTypes.Role);

            if (!int.TryParse(idValue, out var userId))
            {
                ctx.RejectPrincipal();
                return;
            }

            var db = ctx.HttpContext.RequestServices.GetRequiredService<OfficeCalDbContext>();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);

            if (user is null || !user.IsActive || user.Role.ToString() != roleValue)
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
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
