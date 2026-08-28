# 個人行事曆與會議廳預約系統 實作計劃

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推薦）或 superpowers:executing-plans 逐任務實現此計劃。步驟使用複選框（`- [ ]`）語法來跟蹤進度。

**目標：** 建立一套單一公司內部使用的網頁系統，讓員工管理個人日程並預約會議廳，且以資料庫交易鎖保證同一間會議廳的同一時段絕不會被重複預約。

**架構：** .NET 10 四層架構（Web → Services → Infrastructure → Core），SQL Server LocalDB 儲存。所有行事曆顯示與衝突偵測只讀單一權威表 `EventOccurrences`（單次事件也產生 1 筆），重複事件在寫入前於記憶體展開（上限 730 筆）。併發正確性由「對目標 Room 資料列下 `UPDLOCK, HOLDLOCK` 後才檢查與寫入」的交易流程保證，鎖粒度為單一會議廳。前端為 Razor Pages 外殼 + Vue 3 掛載，資料一律走 Axios。

**技術棧：** .NET 10 / ASP.NET Core（Web API + Razor Pages 同專案）、EF Core 10、SQL Server LocalDB、Cookie 驗證 + `PasswordHasher<User>`、`Ical.Net` 5.2.3、xUnit、Bootstrap 5.3 + Vue 3 + Axios + SweetAlert2（離線）。

---

## 執行前必讀：全域決策

以下六項決策貫穿多個任務。**任何任務都不得自行更改這些決策**；若某任務的程式碼與此處衝突，以此處為準。

### D1. 命名與方案佈局

方案名稱 **`OfficeCal`**，根命名空間 `OfficeCal.*`。**不使用 `Calendar` 當命名空間** —— `Ical.Net.Calendar` 是型別名，命名空間叫 `Calendar` 會在 `RecurrenceService` 造成無法解析的名稱衝突。

### D2. 交易拓撲（最重要）

規格 5.2 要求 Event / EventOccurrence / EventAttendee / Notification 在**同一個交易**內寫入，規格 6.1 又要求 `BookingService` 是唯一能寫入 `EventOccurrence` 的地方。兩者的協調方式**固定如下**：

- 三個 Service 共用同一個 **scoped `OfficeCalDbContext`**（DI 生命週期 `AddDbContext` 預設即為 Scoped）。
- **`EventService` 是唯一開啟／提交交易的地方**：`await using var tx = await _db.Database.BeginTransactionAsync(ct);` … `await tx.CommitAsync(ct);`
- **`BookingService` 只在呼叫端已開啟的交易內執行**：它負責取得 Room 鎖、檢查衝突、寫入／刪除 `EventOccurrence`，但**不開交易、不提交**。
- **`NotificationService` 在同一個 DbContext 上 `Add` 通知列**，因此自動落在同一交易內。
- 交易內可以多次 `SaveChangesAsync`（例如先存 `Event` 取得 `Id`），這不會提交交易。
- **絕對不要啟用 `EnableRetryOnFailure`。** EF Core 的重試執行策略與使用者自行管理的交易不相容，會在執行期丟 `InvalidOperationException`。

### D3. 鎖的規則

- 鎖的對象是 **`Rooms` 資料表中目標會議廳那一列**，語法 `SELECT * FROM Rooms WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id`。
- `RoomId` 為 `null`（純個人事件）→ **不取鎖、不檢查衝突**。
- **一次交易只鎖一間會議廳。** 系列編輯若更換會議廳，**只鎖新的會議廳**：釋放舊會議廳的時段永遠不可能製造雙重預約，而同時鎖兩間會議廳會產生鎖順序不一致的死鎖。任何人都不要為了「對稱」而補上第二把鎖。

### D4. 時間與時鐘

- 全系統時間為 `Asia/Taipei` 當地時間，存 `datetime2`，`DateTimeKind.Unspecified`，不做 UTC 轉換。
- **「現在」一律透過注入的 `TimeProvider` 取得**，並用固定 +08:00 位移換算（台灣無日光節約，固定位移完全正確且避開時區資料庫差異）：
  ```csharp
  public static class TaipeiTime
  {
      public static readonly TimeSpan Offset = TimeSpan.FromHours(8);
      public static DateTime Now(TimeProvider clock) =>
          DateTime.SpecifyKind(clock.GetUtcNow().ToOffset(Offset).DateTime, DateTimeKind.Unspecified);
  }
  ```
- 「重新展開不改動已發生過的 occurrence」這條規則沒有可控的「現在」就無法測試，因此 `TimeProvider` 從任務 1 就必須存在。

### D5. Ical.Net 的使用邊界

- `RecurrenceService` 是唯一 `using Ical.Net` 的檔案。其他層只認識 `RecurrencePatternDto` 與 `TimeSlot`。
- **不使用 Ical.Net 解析 RRULE 字串，也不使用它序列化 RRULE。** 字串 ↔ DTO 由自寫的 `RruleFormatter` 負責；Ical.Net 只負責「把程式化建構的 `RecurrencePattern` 展開成發生時間清單」。這樣把外部函式庫的 API 風險壓到最小的一個方法呼叫。
- 這不違反規格 5.3 的「支援完整 RRULE 語法」：**`RruleFormatter` 是資料庫中 `RecurrenceRule` 欄位的唯一寫入者**，所以它的解析器只需認得自己寫出來的子集；語法的完整性由 Ical.Net 的展開引擎提供。
- `.ics` 輸出**手寫**，不用 Ical.Net 的序列化器（見 D6）。

### D6. .ics 輸出規則

- CRLF 換行；每行以 **75 個 octet（不是字元）** 為上限折行，續行以一個空白開頭。標題是中文，用字元數折行會切斷 UTF-8 位元組序列而產生亂碼。
- 文字值需跳脫：`\` → `\`、`;` → `\;`、`,` → `\,`、換行 → `\n`。
- **必須輸出 `Asia/Taipei` 的 `VTIMEZONE` 區塊**（固定 +08:00、無 DST）。只寫 `TZID=Asia/Taipei` 而不附 `VTIMEZONE`，Outlook 訂閱時會顯示錯誤時間——這正是驗收標準 7 的失敗點。

---

## 檔案結構

```
OfficeCal.sln
src/
  OfficeCal.Core/                      # 不依賴任何其他層
    Entities/                          # Department, User, Room, Event, EventOccurrence,
                                       #   EventAttendee, Notification
    Enums/                             # UserRole, EventStatus, NotificationType,
                                       #   RecurrenceFrequency, RecurrenceEndMode, CalendarScope
    Dtos/                              # 所有 API 請求／回應 DTO、TimeSlot、RecurrencePatternDto
    Exceptions/                        # ValidationException, NotFoundException,
                                       #   ForbiddenException, ConflictException
    Common/                            # ApiResponse<T>, TaipeiTime
  OfficeCal.Infrastructure/            # EF Core
    OfficeCalDbContext.cs
    Configurations/                    # 每個實體一個 IEntityTypeConfiguration
    Repositories/                      # 只做資料存取，無業務判斷
    Migrations/
    DesignTimeDbContextFactory.cs      # 讓 dotnet ef 不需要啟動專案
  OfficeCal.Services/                  # 全部業務規則
    IRecurrenceService / RecurrenceService
    RruleFormatter.cs                  # 結構化 <-> RRULE 字串（純函式）
    IBookingService / BookingService    # 唯一寫入 EventOccurrence 的地方
    IEventService / EventService        # 唯一開啟交易的地方
    INotificationService / NotificationService
    IRoomService / RoomService
    IIcsService / IcsService
    IUserService / UserService
    IUserContext                        # 目前登入者，Web 層提供實作
  OfficeCal.Web/
    Program.cs
    Controllers/                        # API，薄，不寫 try/catch
    Middleware/GlobalExceptionMiddleware.cs
    Infrastructure/HttpUserContext.cs
    Pages/                              # Razor Pages 外殼
    wwwroot/css, wwwroot/js             # 離線前端資源
tests/
  OfficeCal.Tests/
    Fixtures/                           # LocalDbFixture, FixedTimeProvider, ApiFactory
    Unit/                               # RruleFormatter, Recurrence 展開, 重疊判定
    Integration/                        # Repository, BookingService, 併發, API
docs/superpowers/plans/                 # 本文件
```

**職責界線：**

| 檔案／元件 | 唯一職責 |
|---|---|
| `RruleFormatter` | 結構化設定 ↔ RRULE 字串的純函式轉換與驗證 |
| `RecurrenceService` | 包住 Ical.Net，把規則展開成 `TimeSlot` 清單並套用 730 上限 |
| `BookingService` | Room 鎖、衝突偵測、`EventOccurrence` 的全部寫入 |
| `EventService` | 交易邊界、權限判定、組裝查詢結果、呼叫上述三者 |
| `NotificationService` | 產生完整句子的通知列 |
| `IcsService` | RFC 5545 文字產生與訂閱 feed 內容組裝 |
| `GlobalExceptionMiddleware` | 領域例外 → HTTP 狀態碼 + 統一信封 |

---

## 任務總覽

| # | 任務 | 交付物 |
|---|---|---|
| 1 | 方案骨架、Core 實體與資料庫 | 可建立的 LocalDB 結構描述，索引經測試驗證 |
| 2 | `RruleFormatter`：結構化 ↔ RRULE 字串 | 純函式與單元測試 |
| 3 | `RecurrenceService`：Ical.Net 展開與上限 | 展開器與邊界測試 |
| 4 | Repository 層與重疊判定 | 查詢 API 與整合測試 |
| 5 | `BookingService`：建立事件的鎖與衝突偵測 | 可用的預約寫入路徑 |
| 6 | 併發整合測試（驗收核心） | 50 輪「恰好一個成功」 |
| 7 | `BookingService`：系列重新展開與單筆改期 | 完整的編輯／取消語意 |
| 8 | Web 骨架、Cookie 驗證、全域例外處理、種子資料 | 可登入的站台 |
| 9 | `NotificationService` 與通知 API | 站內通知 |
| 10 | `EventService`：建立、查詢、明細與權限 | 事件業務邏輯 |
| 11 | `EventsController` 與權限整合測試 | 事件 API |
| 12 | `RoomService` 與會議廳 API | 會議廳主檔與空房查詢 |
| 13 | `IcsService`：單筆匯出與訂閱 feed | .ics 輸出 |
| 14 | 使用者與個人設定 API | 帳號維護、改密碼、重設 token |
| 15 | 前端：離線資源、Layout、登入頁、Axios 共用層 | 可登入的畫面 |
| 16 | 前端：重複設定器與事件建立／編輯／明細彈窗 | 兩個頁面共用的元件 |
| 17 | 前端：我的行事曆（月／週／日） | 行事曆頁 |
| 18 | 前端：會議廳資源時間軸 | 甘特式檢視 |
| 19 | 前端：通知中心與個人設定 | 通知下拉與設定頁 |
| 20 | 前端：Admin 會議廳管理與員工管理 | 後台頁面 |
| 21 | 端到端驗收 | 對照規格 §11 逐條確認 |

---

## 階段一：資料與領域核心

### 任務 1：方案骨架、Core 實體與資料庫

**文件：**
- 創建：`OfficeCal.sln`
- 創建：`src/OfficeCal.Core/Enums/Enums.cs`
- 創建：`src/OfficeCal.Core/Entities/Entities.cs`
- 創建：`src/OfficeCal.Core/Exceptions/DomainExceptions.cs`
- 創建：`src/OfficeCal.Core/Common/ApiResponse.cs`
- 創建：`src/OfficeCal.Core/Common/TaipeiTime.cs`
- 創建：`src/OfficeCal.Core/Dtos/TimeSlot.cs`
- 創建：`src/OfficeCal.Infrastructure/OfficeCalDbContext.cs`
- 創建：`src/OfficeCal.Infrastructure/DesignTimeDbContextFactory.cs`
- 測試：`tests/OfficeCal.Tests/Fixtures/LocalDbFixture.cs`
- 測試：`tests/OfficeCal.Tests/Fixtures/FixedTimeProvider.cs`
- 測試：`tests/OfficeCal.Tests/Integration/SchemaTests.cs`

- [ ] **步驟 1：建立方案與五個專案**

```bash
dotnet new sln -n OfficeCal
dotnet new classlib -n OfficeCal.Core           -o src/OfficeCal.Core           -f net10.0
dotnet new classlib -n OfficeCal.Infrastructure -o src/OfficeCal.Infrastructure -f net10.0
dotnet new classlib -n OfficeCal.Services       -o src/OfficeCal.Services       -f net10.0
dotnet new webapp   -n OfficeCal.Web            -o src/OfficeCal.Web            -f net10.0
dotnet new xunit    -n OfficeCal.Tests          -o tests/OfficeCal.Tests        -f net10.0

rm -f src/OfficeCal.Core/Class1.cs src/OfficeCal.Infrastructure/Class1.cs src/OfficeCal.Services/Class1.cs

dotnet sln add src/OfficeCal.Core src/OfficeCal.Infrastructure src/OfficeCal.Services src/OfficeCal.Web tests/OfficeCal.Tests

dotnet add src/OfficeCal.Infrastructure reference src/OfficeCal.Core
dotnet add src/OfficeCal.Services       reference src/OfficeCal.Core src/OfficeCal.Infrastructure
dotnet add src/OfficeCal.Web            reference src/OfficeCal.Core src/OfficeCal.Infrastructure src/OfficeCal.Services
dotnet add tests/OfficeCal.Tests        reference src/OfficeCal.Core src/OfficeCal.Infrastructure src/OfficeCal.Services src/OfficeCal.Web

dotnet add src/OfficeCal.Infrastructure package Microsoft.EntityFrameworkCore.SqlServer
dotnet add src/OfficeCal.Infrastructure package Microsoft.EntityFrameworkCore.Design
dotnet add src/OfficeCal.Services       package Ical.Net --version 5.2.3
dotnet add src/OfficeCal.Services       package Microsoft.Extensions.Identity.Core
dotnet add tests/OfficeCal.Tests        package Microsoft.AspNetCore.Mvc.Testing

dotnet tool install --global dotnet-ef
```

註：`Microsoft.Extensions.Identity.Core` 是 `PasswordHasher<TUser>` 所在的套件（規格 6.2 只要密碼雜湊與 Cookie 登入，不引入完整 Identity）。若 EF Core 套件還原失敗，改用 `dotnet add ... package Microsoft.EntityFrameworkCore.SqlServer --prerelease` 取得對應 .NET 10 的版本，並讓四個 EF 套件版本一致。

**先確認 xUnit 的主版本**：

```bash
grep -i "xunit" tests/OfficeCal.Tests/OfficeCal.Tests.csproj
```

本計劃的測試程式碼以 **xUnit v2** 撰寫（`IAsyncLifetime` 的兩個方法回傳 `Task`）。若 `dotnet new xunit` 產生的是 **v3**（套件名為 `xunit.v3`），`IAsyncLifetime.InitializeAsync` / `DisposeAsync` 改為回傳 `ValueTask`——把後續所有 fixture 的這兩個方法簽章一併改成 `ValueTask` 即可，其餘程式碼不受影響。兩者擇一，**不要混用**。

- [ ] **步驟 2：寫下 Core 的列舉**

`src/OfficeCal.Core/Enums/Enums.cs`：

```csharp
namespace OfficeCal.Core.Enums;

public enum UserRole { Employee = 0, Admin = 1 }

public enum EventStatus { Active = 0, Cancelled = 1 }

public enum NotificationType
{
    AddedToEvent = 0,
    EventUpdated = 1,
    EventCancelled = 2,
    ForcedCancellation = 3,
}

public enum RecurrenceFrequency { Daily = 0, Weekly = 1, Monthly = 2, Yearly = 3 }

/// <summary>每月重複的兩種模式：每月 N 日 / 每月第 N 個星期 X。</summary>
public enum MonthlyMode { DayOfMonth = 0, WeekDayOfMonth = 1 }

public enum RecurrenceEndMode { UntilDate = 0, Count = 1 }

public enum CalendarScope { Me = 0, Room = 1, All = 2 }

public enum EditMode { Single = 0, Series = 1 }
```

`UserRole`、`EventStatus`、`NotificationType` 在資料庫中存為字串（見步驟 5 的 `HasConversion<string>()`），符合規格 4.2／4.4／4.7 的 `nvarchar` 定義。

- [ ] **步驟 3：寫下 Core 的實體**

`src/OfficeCal.Core/Entities/Entities.cs`：

```csharp
using OfficeCal.Core.Enums;

namespace OfficeCal.Core.Entities;

public class Department
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class User
{
    public int Id { get; set; }
    public string EmployeeNo { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Employee;
    public string IcsFeedToken { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class Room
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Location { get; set; }
    public int Capacity { get; set; }
    public string? Equipment { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>事件本體 / 系列定義。StartAt、EndAt 為系列首次發生的時間。</summary>
public class Event
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public int OwnerId { get; set; }
    public User? Owner { get; set; }
    public int? RoomId { get; set; }
    public Room? Room { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public bool IsAllDay { get; set; }
    /// <summary>RRULE 字串；null 表示單次事件。唯一寫入者為 RruleFormatter。</summary>
    public string? RecurrenceRule { get; set; }
    public EventStatus Status { get; set; } = EventStatus.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<EventOccurrence> Occurrences { get; set; } = new();
    public List<EventAttendee> Attendees { get; set; } = new();
}

/// <summary>唯一的權威占用表。單次事件也一律產生 1 筆。</summary>
public class EventOccurrence
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event? Event { get; set; }
    /// <summary>展開時的原始起始時間，等同 iCalendar 的 RECURRENCE-ID。</summary>
    public DateTime OriginalStartAt { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public int? RoomId { get; set; }
    public Room? Room { get; set; }
    public string? TitleOverride { get; set; }
    public bool IsModified { get; set; }
    public bool IsCancelled { get; set; }
}

public class EventAttendee
{
    public int EventId { get; set; }
    public Event? Event { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
}

public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public NotificationType Type { get; set; }
    public int? EventId { get; set; }
    /// <summary>產生當下就寫成完整句子。</summary>
    public string Message { get; set; } = "";
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

- [ ] **步驟 4：寫下領域例外、回傳信封、時間輔助與 TimeSlot**

`src/OfficeCal.Core/Exceptions/DomainExceptions.cs`：

```csharp
namespace OfficeCal.Core.Exceptions;

/// <summary>輸入不合法 → HTTP 400。</summary>
public class ValidationException : Exception
{
    public List<string> Errors { get; } = new();
    public ValidationException(string message) : base(message) { }
    public ValidationException(string message, IEnumerable<string> errors) : base(message)
        => Errors.AddRange(errors);
}

/// <summary>查無資料 → HTTP 404。</summary>
public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }
}

/// <summary>權限不足 → HTTP 403。</summary>
public class ForbiddenException : Exception
{
    public ForbiddenException(string message) : base(message) { }
}

/// <summary>會議廳時段衝突 → HTTP 409，回應的 data 帶 conflicts 明細。</summary>
public class ConflictException : Exception
{
    public IReadOnlyList<ConflictDetail> Conflicts { get; }
    public ConflictException(string message, IReadOnlyList<ConflictDetail> conflicts) : base(message)
        => Conflicts = conflicts;
}

/// <summary>規格 7.2 的 conflicts 陣列元素。</summary>
public class ConflictDetail
{
    public int OccurrenceId { get; set; }
    public string RoomName { get; set; } = "";
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public string OwnerName { get; set; } = "";
    public string Title { get; set; } = "";
}
```

`src/OfficeCal.Core/Common/ApiResponse.cs`：

```csharp
namespace OfficeCal.Core.Common;

public class ApiResponse<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string Message { get; set; } = "";
    public List<string> Errors { get; set; } = new();
}

public static class ApiResponse
{
    public static ApiResponse<T> Ok<T>(T data, string message = "")
        => new() { Success = true, Data = data, Message = message };

    /// <summary>
    /// 只回訊息、沒有資料的成功回應。C# 的多載解析在兩者皆適用時偏好非泛型版本，
    /// 因此 <c>ApiResponse.Ok("已登出")</c> 會走到這裡而不是 Ok&lt;string&gt;。
    /// </summary>
    public static ApiResponse<object?> Ok(string message = "")
        => new() { Success = true, Data = null, Message = message };

    public static ApiResponse<object?> Fail(string message, IEnumerable<string>? errors = null,
                                            object? data = null)
        => new() { Success = false, Data = data, Message = message, Errors = errors?.ToList() ?? new() };
}
```

`src/OfficeCal.Core/Common/TaipeiTime.cs`：

```csharp
namespace OfficeCal.Core.Common;

/// <summary>
/// 全系統時間為 Asia/Taipei 當地時間。台灣無日光節約，固定 +08:00 位移完全正確，
/// 且避開作業系統時區資料庫的 ID 差異。
/// </summary>
public static class TaipeiTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    public static DateTime Now(TimeProvider clock)
        => DateTime.SpecifyKind(clock.GetUtcNow().ToOffset(Offset).DateTime, DateTimeKind.Unspecified);
}
```

`src/OfficeCal.Core/Dtos/TimeSlot.cs`：

```csharp
namespace OfficeCal.Core.Dtos;

/// <summary>一段時間區間。展開結果與衝突檢查的通用單位。</summary>
public readonly record struct TimeSlot(DateTime Start, DateTime End);
```

- [ ] **步驟 5：寫下 DbContext 與索引設定**

`src/OfficeCal.Infrastructure/OfficeCalDbContext.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure;

public class OfficeCalDbContext : DbContext
{
    public OfficeCalDbContext(DbContextOptions<OfficeCalDbContext> options) : base(options) { }

    public DbSet<Department> Departments => Set<Department>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<EventOccurrence> EventOccurrences => Set<EventOccurrence>();
    public DbSet<EventAttendee> EventAttendees => Set<EventAttendee>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Department>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<User>(e =>
        {
            e.Property(x => x.EmployeeNo).HasMaxLength(20).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(50).IsRequired();
            e.Property(x => x.Email).HasMaxLength(100).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(200).IsRequired();
            e.Property(x => x.Role).HasMaxLength(20).HasConversion<string>().IsRequired();
            e.Property(x => x.IcsFeedToken).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.EmployeeNo).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.IcsFeedToken).IsUnique();
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Room>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50).IsRequired();
            e.Property(x => x.Location).HasMaxLength(100);
            e.Property(x => x.Equipment).HasMaxLength(200);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Event>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.RecurrenceRule).HasMaxLength(500);
            e.Property(x => x.Status).HasMaxLength(20).HasConversion<string>().IsRequired();
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Room).WithMany().HasForeignKey(x => x.RoomId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<EventOccurrence>(e =>
        {
            e.Property(x => x.TitleOverride).HasMaxLength(100);
            e.HasOne(x => x.Event).WithMany(x => x.Occurrences).HasForeignKey(x => x.EventId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Room).WithMany().HasForeignKey(x => x.RoomId)
             .OnDelete(DeleteBehavior.Restrict);

            // 一次發生只能有一列
            e.HasIndex(x => new { x.EventId, x.OriginalStartAt }).IsUnique();

            // 衝突偵測的主要查詢路徑
            e.HasIndex(x => new { x.RoomId, x.StartAt, x.EndAt })
             .HasDatabaseName("IX_EventOccurrences_Room_Range")
             .HasFilter("[IsCancelled] = 0 AND [RoomId] IS NOT NULL");

            // 行事曆區間查詢
            e.HasIndex(x => new { x.StartAt, x.EndAt });
        });

        b.Entity<EventAttendee>(e =>
        {
            e.HasKey(x => new { x.EventId, x.UserId });
            e.HasOne(x => x.Event).WithMany(x => x.Attendees).HasForeignKey(x => x.EventId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Notification>(e =>
        {
            e.Property(x => x.Type).HasMaxLength(30).HasConversion<string>().IsRequired();
            e.Property(x => x.Message).HasMaxLength(300).IsRequired();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            // 事件被硬刪除時 EventId 設為 NULL
            e.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId)
             .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.UserId, x.IsRead, x.CreatedAt })
             .HasDatabaseName("IX_Notifications_User_Unread");
        });
    }
}
```

`src/OfficeCal.Infrastructure/DesignTimeDbContextFactory.cs`（讓 `dotnet ef` 不必指定啟動專案）：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OfficeCal.Infrastructure;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OfficeCalDbContext>
{
    public const string DefaultConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=OfficeCal;Integrated Security=true;" +
        "MultipleActiveResultSets=true;TrustServerCertificate=true";

    public OfficeCalDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OfficeCalDbContext>()
            .UseSqlServer(DefaultConnectionString)
            .Options;
        return new OfficeCalDbContext(options);
    }
}
```

- [ ] **步驟 6：產生初始 Migration**

```bash
dotnet ef migrations add Initial -p src/OfficeCal.Infrastructure
```

預期：`src/OfficeCal.Infrastructure/Migrations/` 下出現 `*_Initial.cs`。開啟它確認 `IX_EventOccurrences_Room_Range` 帶有 `filter: "[IsCancelled] = 0 AND [RoomId] IS NOT NULL"`。若沒有，回步驟 5 修正後 `dotnet ef migrations remove -p src/OfficeCal.Infrastructure` 再重跑。

- [ ] **步驟 7：寫測試基礎設施**

`tests/OfficeCal.Tests/Fixtures/FixedTimeProvider.cs`：

```csharp
namespace OfficeCal.Tests.Fixtures;

/// <summary>可控制的時鐘。傳入的是 Asia/Taipei 當地時間。</summary>
public sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public FixedTimeProvider(DateTime taipeiLocalNow)
        => _utcNow = new DateTimeOffset(taipeiLocalNow, TimeSpan.FromHours(8)).ToUniversalTime();

    public void SetTaipeiNow(DateTime taipeiLocalNow)
        => _utcNow = new DateTimeOffset(taipeiLocalNow, TimeSpan.FromHours(8)).ToUniversalTime();

    public override DateTimeOffset GetUtcNow() => _utcNow;
}
```

`tests/OfficeCal.Tests/Fixtures/LocalDbFixture.cs`：

```csharp
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
```

- [ ] **步驟 8：編寫失敗的結構描述測試**

`tests/OfficeCal.Tests/Integration/SchemaTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Entities;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("LocalDb")]
public class SchemaTests
{
    private readonly LocalDbFixture _db;
    public SchemaTests(LocalDbFixture db) => _db = db;

    [Fact]
    public async Task 同一事件的同一次發生不能有兩列()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();

        var owner = new User
        {
            EmployeeNo = "E001", DisplayName = "測試員", Email = "e001@corp.local",
            PasswordHash = "x", IcsFeedToken = Guid.NewGuid().ToString("N"),
        };
        ctx.Users.Add(owner);
        await ctx.SaveChangesAsync();

        var ev = new Event
        {
            Title = "測試事件", OwnerId = owner.Id,
            StartAt = new DateTime(2026, 9, 7, 10, 0, 0),
            EndAt = new DateTime(2026, 9, 7, 11, 0, 0),
        };
        ctx.Events.Add(ev);
        await ctx.SaveChangesAsync();

        ctx.EventOccurrences.Add(new EventOccurrence
        {
            EventId = ev.Id,
            OriginalStartAt = new DateTime(2026, 9, 7, 10, 0, 0),
            StartAt = new DateTime(2026, 9, 7, 10, 0, 0),
            EndAt = new DateTime(2026, 9, 7, 11, 0, 0),
        });
        await ctx.SaveChangesAsync();

        ctx.EventOccurrences.Add(new EventOccurrence
        {
            EventId = ev.Id,
            OriginalStartAt = new DateTime(2026, 9, 7, 10, 0, 0),   // 同一次發生
            StartAt = new DateTime(2026, 9, 7, 14, 0, 0),
            EndAt = new DateTime(2026, 9, 7, 15, 0, 0),
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task 衝突偵測用的篩選索引存在且帶有正確的篩選條件()
    {
        await using var ctx = _db.CreateContext();
        var conn = ctx.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT filter_definition FROM sys.indexes WHERE name = 'IX_EventOccurrences_Room_Range'";
        var filter = (string?)await cmd.ExecuteScalarAsync();

        Assert.NotNull(filter);
        Assert.Contains("IsCancelled", filter);
        Assert.Contains("RoomId", filter);
    }
}
```

- [ ] **步驟 9：運行測試驗證失敗**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~SchemaTests"
```

預期：若 migration 或索引設定有誤，第二個測試會因 `filter` 為 null 而 FAIL。此步驟同時確認測試基礎設施可運行——若 `(localdb)\MSSQLLocalDB` 不存在，執行 `sqllocaldb create MSSQLLocalDB -s` 後重試。

- [ ] **步驟 10：運行測試驗證通過**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~SchemaTests"
```

預期：Passed! 2 passed。

- [ ] **步驟 11：Commit**

第一次 commit 要指名路徑。工作區裡另有 `.claude/`（技能設定）與規格的 PDF 副本是未追蹤狀態，`git add -A` 會把它們一起掃進來；先確認要不要納入，或把它們寫進 `.gitignore`。後續任務的 `git add -A` 就不會再有這個問題。

```bash
git status --short          # 先看一眼有哪些未追蹤檔案
git add OfficeCal.sln src tests .gitignore
git commit -m "feat: 建立方案骨架、Core 實體與 EF Core 結構描述"
```

`dotnet new sln` 不會產生 `.gitignore`；若還沒有，先執行 `dotnet new gitignore`。

---

### 任務 2：`RruleFormatter` —— 結構化設定 ↔ RRULE 字串

規格 5.3：「使用者永遠不會看到或輸入 RRULE 字串」。本任務實作雙向轉換的純函式，不碰資料庫也不碰 Ical.Net。

**文件：**
- 創建：`src/OfficeCal.Core/Dtos/RecurrencePatternDto.cs`
- 創建：`src/OfficeCal.Services/RruleFormatter.cs`
- 測試：`tests/OfficeCal.Tests/Unit/RruleFormatterTests.cs`

**RRULE 的正規順序（`ToRrule` 一律依此輸出，`Parse` 不依賴順序）：**

```
FREQ=…;INTERVAL=…[;BYMONTH=…][;BYMONTHDAY=…][;BYDAY=…][;BYSETPOS=…];(UNTIL=…|COUNT=…)
```

- [ ] **步驟 1：定義 `RecurrencePatternDto`**

`src/OfficeCal.Core/Dtos/RecurrencePatternDto.cs`：

```csharp
using OfficeCal.Core.Enums;

namespace OfficeCal.Core.Dtos;

/// <summary>
/// 使用者在畫面上看到的結構化重複設定。系統中除了 RruleFormatter 之外，
/// 沒有任何地方會直接處理 RRULE 字串。
/// </summary>
public class RecurrencePatternDto
{
    public RecurrenceFrequency Frequency { get; set; }

    /// <summary>每 N 天／週／月／年。</summary>
    public int Interval { get; set; } = 1;

    /// <summary>FREQ=WEEKLY 時的星期核取方塊，可複選。</summary>
    public List<DayOfWeek> ByWeekDays { get; set; } = new();

    /// <summary>FREQ=MONTHLY 時的兩種模式擇一。</summary>
    public MonthlyMode MonthlyMode { get; set; } = MonthlyMode.DayOfMonth;

    /// <summary>每月 N 日（1–31）；FREQ=YEARLY 時為每年 N 月的 N 日。</summary>
    public int? ByMonthDay { get; set; }

    /// <summary>每月第 N 個（1–4），-1 表示最後一個。</summary>
    public int? BySetPosition { get; set; }

    /// <summary>搭配 BySetPosition 的星期。</summary>
    public DayOfWeek? ByPositionWeekDay { get; set; }

    /// <summary>FREQ=YEARLY 的月份（1–12）。</summary>
    public int? ByMonth { get; set; }

    public RecurrenceEndMode EndMode { get; set; } = RecurrenceEndMode.UntilDate;
    public DateOnly? UntilDate { get; set; }
    public int? Count { get; set; }
}
```

- [ ] **步驟 2：編寫失敗的測試**

`tests/OfficeCal.Tests/Unit/RruleFormatterTests.cs`：

```csharp
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Services;
using Xunit;

namespace OfficeCal.Tests.Unit;

public class RruleFormatterTests
{
    private static RecurrencePatternDto 每週一() => new()
    {
        Frequency = RecurrenceFrequency.Weekly,
        Interval = 1,
        ByWeekDays = new() { DayOfWeek.Monday },
        EndMode = RecurrenceEndMode.UntilDate,
        UntilDate = new DateOnly(2026, 12, 28),
    };

    private static RecurrencePatternDto 每月最後一個週五() => new()
    {
        Frequency = RecurrenceFrequency.Monthly,
        Interval = 1,
        MonthlyMode = MonthlyMode.WeekDayOfMonth,
        BySetPosition = -1,
        ByPositionWeekDay = DayOfWeek.Friday,
        EndMode = RecurrenceEndMode.Count,
        Count = 12,
    };

    private static RecurrencePatternDto 每兩週的週二與週四() => new()
    {
        Frequency = RecurrenceFrequency.Weekly,
        Interval = 2,
        ByWeekDays = new() { DayOfWeek.Tuesday, DayOfWeek.Thursday },
        EndMode = RecurrenceEndMode.Count,
        Count = 10,
    };

    private static RecurrencePatternDto 每年九月十五日() => new()
    {
        Frequency = RecurrenceFrequency.Yearly,
        Interval = 1,
        ByMonth = 9,
        ByMonthDay = 15,
        EndMode = RecurrenceEndMode.Count,
        Count = 5,
    };

    [Fact]
    public void 每週一轉出正確的RRULE()
        => Assert.Equal("FREQ=WEEKLY;INTERVAL=1;BYDAY=MO;UNTIL=20261228T235959",
                        RruleFormatter.ToRrule(每週一()));

    [Fact]
    public void 每月最後一個週五轉出正確的RRULE()
        => Assert.Equal("FREQ=MONTHLY;INTERVAL=1;BYDAY=FR;BYSETPOS=-1;COUNT=12",
                        RruleFormatter.ToRrule(每月最後一個週五()));

    [Fact]
    public void 每兩週的週二與週四轉出正確的RRULE()
        => Assert.Equal("FREQ=WEEKLY;INTERVAL=2;BYDAY=TU,TH;COUNT=10",
                        RruleFormatter.ToRrule(每兩週的週二與週四()));

    [Fact]
    public void 每年九月十五日轉出正確的RRULE()
        => Assert.Equal("FREQ=YEARLY;INTERVAL=1;BYMONTH=9;BYMONTHDAY=15;COUNT=5",
                        RruleFormatter.ToRrule(每年九月十五日()));

    [Fact]
    public void 每月十五日轉出正確的RRULE()
    {
        var dto = new RecurrencePatternDto
        {
            Frequency = RecurrenceFrequency.Monthly,
            Interval = 1,
            MonthlyMode = MonthlyMode.DayOfMonth,
            ByMonthDay = 15,
            EndMode = RecurrenceEndMode.Count,
            Count = 6,
        };
        Assert.Equal("FREQ=MONTHLY;INTERVAL=1;BYMONTHDAY=15;COUNT=6", RruleFormatter.ToRrule(dto));
    }

    [Theory]
    [MemberData(nameof(所有樣本))]
    public void 雙向轉換可還原(RecurrencePatternDto original)
    {
        var rrule = RruleFormatter.ToRrule(original);
        var parsed = RruleFormatter.Parse(rrule);
        Assert.Equal(rrule, RruleFormatter.ToRrule(parsed));
        Assert.Equal(original.Frequency, parsed.Frequency);
        Assert.Equal(original.Interval, parsed.Interval);
        Assert.Equal(original.ByWeekDays, parsed.ByWeekDays);
        Assert.Equal(original.MonthlyMode, parsed.MonthlyMode);
        Assert.Equal(original.ByMonthDay, parsed.ByMonthDay);
        Assert.Equal(original.BySetPosition, parsed.BySetPosition);
        Assert.Equal(original.ByPositionWeekDay, parsed.ByPositionWeekDay);
        Assert.Equal(original.ByMonth, parsed.ByMonth);
        Assert.Equal(original.EndMode, parsed.EndMode);
        Assert.Equal(original.UntilDate, parsed.UntilDate);
        Assert.Equal(original.Count, parsed.Count);
    }

    public static TheoryData<RecurrencePatternDto> 所有樣本() => new()
    {
        每週一(), 每月最後一個週五(), 每兩週的週二與週四(), 每年九月十五日(),
    };

    [Fact]
    public void 沒有結束條件的規則被拒絕()
    {
        var dto = 每週一();
        dto.EndMode = RecurrenceEndMode.UntilDate;
        dto.UntilDate = null;
        var ex = Assert.Throws<ValidationException>(() => RruleFormatter.ToRrule(dto));
        Assert.Contains("結束", ex.Message);
    }

    [Fact]
    public void 沒有指定次數的Count模式被拒絕()
    {
        var dto = 每週一();
        dto.EndMode = RecurrenceEndMode.Count;
        dto.Count = null;
        Assert.Throws<ValidationException>(() => RruleFormatter.ToRrule(dto));
    }

    [Fact]
    public void Count超過上限被拒絕()
    {
        var dto = 每週一();
        dto.EndMode = RecurrenceEndMode.Count;
        dto.Count = 731;
        var ex = Assert.Throws<ValidationException>(() => RruleFormatter.ToRrule(dto));
        Assert.Contains("上限", ex.Message);
    }

    [Fact]
    public void 每週規則未勾選任何星期被拒絕()
    {
        var dto = 每週一();
        dto.ByWeekDays = new();
        Assert.Throws<ValidationException>(() => RruleFormatter.ToRrule(dto));
    }

    [Fact]
    public void 間隔小於一被拒絕()
    {
        var dto = 每週一();
        dto.Interval = 0;
        Assert.Throws<ValidationException>(() => RruleFormatter.ToRrule(dto));
    }

    [Fact]
    public void 缺少FREQ的字串解析失敗()
        => Assert.Throws<ValidationException>(() => RruleFormatter.Parse("INTERVAL=1;COUNT=3"));
}
```

- [ ] **步驟 3：運行測試驗證失敗**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~RruleFormatterTests"
```

預期：編譯失敗，`RruleFormatter` 不存在。

- [ ] **步驟 4：實作 `RruleFormatter`**

`src/OfficeCal.Services/RruleFormatter.cs`：

```csharp
using System.Globalization;
using System.Text;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;

namespace OfficeCal.Services;

/// <summary>
/// 結構化重複設定 ↔ RRULE 字串的純函式轉換。
/// 這是資料庫 Event.RecurrenceRule 欄位的唯一寫入者，所以 Parse 只需認得 ToRrule 寫得出來的子集。
/// </summary>
public static class RruleFormatter
{
    public const int MaxOccurrences = 730;

    private static readonly string[] DayCodes = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

    private static string Code(DayOfWeek d) => DayCodes[(int)d];

    private static DayOfWeek Day(string code)
    {
        var i = Array.IndexOf(DayCodes, code.ToUpperInvariant());
        if (i < 0) throw new ValidationException($"無法辨識的星期代碼 '{code}'");
        return (DayOfWeek)i;
    }

    public static string ToRrule(RecurrencePatternDto p)
    {
        Validate(p);

        var sb = new StringBuilder();
        sb.Append("FREQ=").Append(p.Frequency.ToString().ToUpperInvariant());
        sb.Append(";INTERVAL=").Append(p.Interval);

        if (p.Frequency == RecurrenceFrequency.Yearly)
            sb.Append(";BYMONTH=").Append(p.ByMonth!.Value);

        if (p.Frequency == RecurrenceFrequency.Yearly ||
            (p.Frequency == RecurrenceFrequency.Monthly && p.MonthlyMode == MonthlyMode.DayOfMonth))
            sb.Append(";BYMONTHDAY=").Append(p.ByMonthDay!.Value);

        if (p.Frequency == RecurrenceFrequency.Weekly)
            sb.Append(";BYDAY=").Append(string.Join(",", p.ByWeekDays.OrderBy(d => (int)d).Select(Code)));

        if (p.Frequency == RecurrenceFrequency.Monthly && p.MonthlyMode == MonthlyMode.WeekDayOfMonth)
        {
            sb.Append(";BYDAY=").Append(Code(p.ByPositionWeekDay!.Value));
            sb.Append(";BYSETPOS=").Append(p.BySetPosition!.Value);
        }

        if (p.EndMode == RecurrenceEndMode.UntilDate)
            sb.Append(";UNTIL=").Append(p.UntilDate!.Value.ToString("yyyyMMdd")).Append("T235959");
        else
            sb.Append(";COUNT=").Append(p.Count!.Value);

        return sb.ToString();
    }

    public static RecurrencePatternDto Parse(string rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule)) throw new ValidationException("重複規則為空字串");

        var parts = rrule.Split(';', StringSplitOptions.RemoveEmptyEntries)
                         .Select(x => x.Split('=', 2))
                         .ToDictionary(
                             x => x[0].Trim().ToUpperInvariant(),
                             x => x.Length > 1 ? x[1].Trim() : "");

        if (!parts.TryGetValue("FREQ", out var freq))
            throw new ValidationException("重複規則缺少 FREQ");

        var p = new RecurrencePatternDto
        {
            Frequency = freq.ToUpperInvariant() switch
            {
                "DAILY" => RecurrenceFrequency.Daily,
                "WEEKLY" => RecurrenceFrequency.Weekly,
                "MONTHLY" => RecurrenceFrequency.Monthly,
                "YEARLY" => RecurrenceFrequency.Yearly,
                _ => throw new ValidationException($"不支援的 FREQ '{freq}'"),
            },
            Interval = parts.TryGetValue("INTERVAL", out var iv)
                ? int.Parse(iv, CultureInfo.InvariantCulture) : 1,
        };

        if (parts.TryGetValue("BYMONTH", out var bm))
            p.ByMonth = int.Parse(bm, CultureInfo.InvariantCulture);

        if (parts.TryGetValue("BYMONTHDAY", out var bmd))
            p.ByMonthDay = int.Parse(bmd, CultureInfo.InvariantCulture);

        var byDays = parts.TryGetValue("BYDAY", out var bd) && bd.Length > 0
            ? bd.Split(',').Select(Day).ToList()
            : new List<DayOfWeek>();

        if (parts.TryGetValue("BYSETPOS", out var bsp))
        {
            p.MonthlyMode = MonthlyMode.WeekDayOfMonth;
            p.BySetPosition = int.Parse(bsp, CultureInfo.InvariantCulture);
            p.ByPositionWeekDay = byDays.Count > 0
                ? byDays[0]
                : throw new ValidationException("BYSETPOS 必須搭配 BYDAY");
        }
        else if (p.Frequency == RecurrenceFrequency.Weekly)
        {
            p.ByWeekDays = byDays;
        }

        if (parts.TryGetValue("UNTIL", out var until))
        {
            p.EndMode = RecurrenceEndMode.UntilDate;
            p.UntilDate = DateOnly.ParseExact(until[..8], "yyyyMMdd", CultureInfo.InvariantCulture);
        }
        else if (parts.TryGetValue("COUNT", out var cnt))
        {
            p.EndMode = RecurrenceEndMode.Count;
            p.Count = int.Parse(cnt, CultureInfo.InvariantCulture);
        }
        else
        {
            throw new ValidationException("重複規則必須有結束條件（UNTIL 或 COUNT）");
        }

        Validate(p);
        return p;
    }

    private static void Validate(RecurrencePatternDto p)
    {
        if (p.Interval < 1 || p.Interval > 999)
            throw new ValidationException("重複間隔必須介於 1 到 999 之間");

        switch (p.EndMode)
        {
            case RecurrenceEndMode.UntilDate when p.UntilDate is null:
                throw new ValidationException("重複事件必須指定結束日期或重複次數");
            case RecurrenceEndMode.Count when p.Count is null:
                throw new ValidationException("重複事件必須指定結束日期或重複次數");
            case RecurrenceEndMode.Count when p.Count < 1:
                throw new ValidationException("重複次數必須至少為 1");
            case RecurrenceEndMode.Count when p.Count > MaxOccurrences:
                throw new ValidationException($"重複次數超過上限（{MaxOccurrences} 次），請縮短結束日期");
        }

        switch (p.Frequency)
        {
            case RecurrenceFrequency.Weekly when p.ByWeekDays.Count == 0:
                throw new ValidationException("每週重複必須至少勾選一個星期");
            case RecurrenceFrequency.Weekly when p.ByWeekDays.Distinct().Count() != p.ByWeekDays.Count:
                throw new ValidationException("星期不可重複勾選");

            case RecurrenceFrequency.Monthly when p.MonthlyMode == MonthlyMode.DayOfMonth
                                               && p.ByMonthDay is not (>= 1 and <= 31):
                throw new ValidationException("每月 N 日必須介於 1 到 31 之間");
            case RecurrenceFrequency.Monthly when p.MonthlyMode == MonthlyMode.WeekDayOfMonth
                                               && (p.ByPositionWeekDay is null
                                                   || p.BySetPosition is not (1 or 2 or 3 or 4 or -1)):
                throw new ValidationException("每月第 N 個星期 X 的設定不完整");

            case RecurrenceFrequency.Yearly when p.ByMonth is not (>= 1 and <= 12):
                throw new ValidationException("每年重複的月份必須介於 1 到 12 之間");
            case RecurrenceFrequency.Yearly when p.ByMonthDay is not (>= 1 and <= 31):
                throw new ValidationException("每年重複的日期必須介於 1 到 31 之間");
        }
    }
}
```

- [ ] **步驟 5：運行測試驗證通過**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~RruleFormatterTests"
```

預期：Passed! 14 passed。

- [ ] **步驟 6：Commit**

```bash
git add -A
git commit -m "feat: 新增結構化重複設定與 RRULE 字串的雙向轉換"
```

---

### 任務 3：`RecurrenceService` —— Ical.Net 展開與 730 上限

**文件：**
- 創建：`src/OfficeCal.Services/IRecurrenceService.cs`
- 創建：`src/OfficeCal.Services/RecurrenceService.cs`
- 測試：`tests/OfficeCal.Tests/Unit/IcalNetSmokeTests.cs`
- 測試：`tests/OfficeCal.Tests/Unit/RecurrenceServiceTests.cs`

- [ ] **步驟 1：先驗證 Ical.Net 5.2.3 的實際 API（verify-then-adapt）**

Ical.Net 5 相對於 4 有破壞性變更，且本計劃撰寫時未實際編譯過該套件。**先寫這支冒煙測試並讓它通過，再往下寫任何 `RecurrenceService` 程式碼。**

`tests/OfficeCal.Tests/Unit/IcalNetSmokeTests.cs`：

```csharp
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Xunit;

namespace OfficeCal.Tests.Unit;

/// <summary>
/// 固定住本專案實際用到的那一小塊 Ical.Net API。
/// 升級套件時若這支測試壞掉，就知道要調整 RecurrenceService。
/// </summary>
public class IcalNetSmokeTests
{
    [Fact]
    public void 每週一展開三次()
    {
        var pattern = new RecurrencePattern
        {
            Frequency = FrequencyType.Weekly,
            Interval = 1,
            ByDay = new List<WeekDay> { new(DayOfWeek.Monday) },
            Count = 3,
        };

        var ev = new CalendarEvent
        {
            // 刻意使用不帶時區的 floating time：台灣無日光節約，浮動時間即台北當地時間。
            DtStart = new CalDateTime(2026, 9, 7, 10, 0, 0),
            DtEnd = new CalDateTime(2026, 9, 7, 11, 0, 0),
            RecurrenceRules = new List<RecurrencePattern> { pattern },
        };

        var starts = ev.GetOccurrences()
                       .Take(10)
                       .Select(o => o.Period.StartTime.Value)   // 若編譯失敗見下方註記
                       .ToList();

        Assert.Equal(3, starts.Count);
        Assert.Equal(new DateTime(2026, 9, 7, 10, 0, 0), starts[0]);
        Assert.Equal(new DateTime(2026, 9, 14, 10, 0, 0), starts[1]);
        Assert.Equal(new DateTime(2026, 9, 21, 10, 0, 0), starts[2]);
    }

    [Fact]
    public void 每月最後一個週五展開三次()
    {
        var pattern = new RecurrencePattern
        {
            Frequency = FrequencyType.Monthly,
            Interval = 1,
            ByDay = new List<WeekDay> { new(DayOfWeek.Friday) },
            BySetPosition = new List<int> { -1 },
            Count = 3,
        };

        var ev = new CalendarEvent
        {
            DtStart = new CalDateTime(2026, 9, 25, 15, 0, 0),
            DtEnd = new CalDateTime(2026, 9, 25, 16, 0, 0),
            RecurrenceRules = new List<RecurrencePattern> { pattern },
        };

        var starts = ev.GetOccurrences().Take(10).Select(o => o.Period.StartTime.Value).ToList();

        Assert.Equal(3, starts.Count);
        Assert.Equal(new DateTime(2026, 9, 25, 15, 0, 0), starts[0]);
        Assert.Equal(new DateTime(2026, 10, 30, 15, 0, 0), starts[1]);
        Assert.Equal(new DateTime(2026, 11, 27, 15, 0, 0), starts[2]);
    }
}
```

**若編譯或執行失敗，依下列順序調整（不要改測試想驗證的行為）：**

| 症狀 | 處理 |
|---|---|
| `o.Period.StartTime.Value` 無此成員 | 用 IDE 或 `dotnet build` 的錯誤訊息看 `CalDateTime` 實際暴露的取值成員（候選：`.Value`、`.Date` + `.Time`、`.AsSystemLocal`），選一個能取回 `DateTime` 的，**並把同樣的寫法套用到 `RecurrenceService`**。 |
| `GetOccurrences()` 無無參數多載 | 改用 `GetOccurrences(new CalDateTime(<DtStart>))`。 |
| `ByDay` / `BySetPosition` 型別不符 | 依編譯錯誤調整集合型別（`List<WeekDay>` / `List<int>`）。 |
| `FrequencyType` 找不到 | 檢查命名空間是否為 `Ical.Net`（v5 曾在 `Ical.Net.FrequencyType`）。 |

執行：`dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~IcalNetSmokeTests"`
預期：先 FAIL（未安裝或 API 不符），調整後 2 passed。

- [ ] **步驟 2：編寫失敗的 `RecurrenceService` 測試**

`tests/OfficeCal.Tests/Unit/RecurrenceServiceTests.cs`：

```csharp
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Services;
using Xunit;

namespace OfficeCal.Tests.Unit;

public class RecurrenceServiceTests
{
    private readonly IRecurrenceService _svc = new RecurrenceService();

    [Fact]
    public void 單次事件展開為一筆()
    {
        var slots = _svc.Expand(null,
            new DateTime(2026, 9, 7, 10, 0, 0), new DateTime(2026, 9, 7, 11, 0, 0));

        Assert.Single(slots);
        Assert.Equal(new DateTime(2026, 9, 7, 10, 0, 0), slots[0].Start);
        Assert.Equal(new DateTime(2026, 9, 7, 11, 0, 0), slots[0].End);
    }

    [Fact]
    public void 每週一展開時每筆長度都等於首次長度()
    {
        var rrule = "FREQ=WEEKLY;INTERVAL=1;BYDAY=MO;COUNT=3";
        var slots = _svc.Expand(rrule,
            new DateTime(2026, 9, 7, 10, 0, 0), new DateTime(2026, 9, 7, 11, 30, 0));

        Assert.Equal(3, slots.Count);
        Assert.All(slots, s => Assert.Equal(TimeSpan.FromMinutes(90), s.End - s.Start));
        Assert.Equal(new DateTime(2026, 9, 21, 10, 0, 0), slots[2].Start);
    }

    [Fact]
    public void 跨年展開正確()
    {
        var rrule = "FREQ=MONTHLY;INTERVAL=1;BYMONTHDAY=15;COUNT=4";
        var slots = _svc.Expand(rrule,
            new DateTime(2026, 11, 15, 9, 0, 0), new DateTime(2026, 11, 15, 10, 0, 0));

        Assert.Equal(4, slots.Count);
        Assert.Equal(new DateTime(2026, 12, 15, 9, 0, 0), slots[1].Start);
        Assert.Equal(new DateTime(2027, 1, 15, 9, 0, 0), slots[2].Start);
        Assert.Equal(new DateTime(2027, 2, 15, 9, 0, 0), slots[3].Start);
    }

    [Fact]
    public void UNTIL為含當日的邊界()
    {
        // 2026-09-07 是週一；UNTIL=2026-09-21 應含 9/21 當天那一次。
        var rrule = "FREQ=WEEKLY;INTERVAL=1;BYDAY=MO;UNTIL=20260921T235959";
        var slots = _svc.Expand(rrule,
            new DateTime(2026, 9, 7, 10, 0, 0), new DateTime(2026, 9, 7, 11, 0, 0));

        Assert.Equal(3, slots.Count);
        Assert.Equal(new DateTime(2026, 9, 21, 10, 0, 0), slots[^1].Start);
    }

    [Fact]
    public void 展開超過上限被拒絕()
    {
        // 每天一次、UNTIL 在三年後 → 遠超過 730 筆
        var rrule = "FREQ=DAILY;INTERVAL=1;UNTIL=20291231T235959";
        var ex = Assert.Throws<ValidationException>(() => _svc.Expand(rrule,
            new DateTime(2026, 9, 7, 10, 0, 0), new DateTime(2026, 9, 7, 11, 0, 0)));

        Assert.Contains("上限", ex.Message);
    }

    [Fact]
    public void 沒有結束條件的規則被拒絕()
        => Assert.Throws<ValidationException>(() => _svc.Expand("FREQ=DAILY;INTERVAL=1",
            new DateTime(2026, 9, 7, 10, 0, 0), new DateTime(2026, 9, 7, 11, 0, 0)));

    [Fact]
    public void 起始日與每週規則不符時被拒絕()
    {
        // 2026-09-08 是週二，規則卻是每週一
        var p = new RecurrencePatternDto
        {
            Frequency = RecurrenceFrequency.Weekly,
            Interval = 1,
            ByWeekDays = new() { DayOfWeek.Monday },
            EndMode = RecurrenceEndMode.Count,
            Count = 3,
        };
        var ex = Assert.Throws<ValidationException>(
            () => _svc.ValidateStartMatches(p, new DateTime(2026, 9, 8, 10, 0, 0)));
        Assert.Contains("起始日", ex.Message);
    }

    [Fact]
    public void 起始日為每月最後一個週五時通過驗證()
    {
        var p = new RecurrencePatternDto
        {
            Frequency = RecurrenceFrequency.Monthly,
            Interval = 1,
            MonthlyMode = MonthlyMode.WeekDayOfMonth,
            BySetPosition = -1,
            ByPositionWeekDay = DayOfWeek.Friday,
            EndMode = RecurrenceEndMode.Count,
            Count = 3,
        };
        // 2026-09-25 是九月的最後一個週五
        _svc.ValidateStartMatches(p, new DateTime(2026, 9, 25, 15, 0, 0));
    }

    [Fact]
    public void 結構化設定經由服務也能轉出RRULE()
    {
        var p = new RecurrencePatternDto
        {
            Frequency = RecurrenceFrequency.Daily,
            Interval = 3,
            EndMode = RecurrenceEndMode.Count,
            Count = 5,
        };
        Assert.Equal("FREQ=DAILY;INTERVAL=3;COUNT=5", _svc.ToRrule(p));
        Assert.Equal(RecurrenceFrequency.Daily, _svc.ParseRrule("FREQ=DAILY;INTERVAL=3;COUNT=5").Frequency);
    }
}
```

- [ ] **步驟 3：運行測試驗證失敗**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~RecurrenceServiceTests"
```

預期：編譯失敗，`IRecurrenceService` / `RecurrenceService` 不存在。

- [ ] **步驟 4：實作 `IRecurrenceService` 與 `RecurrenceService`**

`src/OfficeCal.Services/IRecurrenceService.cs`：

```csharp
using OfficeCal.Core.Dtos;

namespace OfficeCal.Services;

/// <summary>
/// 唯一 using Ical.Net 的服務。其他層只認識 RecurrencePatternDto 與 TimeSlot。
/// </summary>
public interface IRecurrenceService
{
    /// <summary>結構化設定 → RRULE 字串（含驗證）。</summary>
    string ToRrule(RecurrencePatternDto pattern);

    /// <summary>RRULE 字串 → 結構化設定（含驗證）。</summary>
    RecurrencePatternDto ParseRrule(string rrule);

    /// <summary>
    /// 展開重複規則。rrule 為 null 時回傳單一 TimeSlot。
    /// 每次發生的長度一律等於 (endAt - startAt)。
    /// 展開超過 730 筆或規則無結束條件時丟 ValidationException。
    /// </summary>
    IReadOnlyList<TimeSlot> Expand(string? rrule, DateTime startAt, DateTime endAt);

    /// <summary>
    /// 驗證事件起始日符合重複規則。不符時丟 ValidationException。
    /// 前端的重複設定器預設會以起始日填入星期／日期，所以正常操作不會踩到。
    /// </summary>
    void ValidateStartMatches(RecurrencePatternDto pattern, DateTime startAt);
}
```

`src/OfficeCal.Services/RecurrenceService.cs`：

```csharp
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;

namespace OfficeCal.Services;

public class RecurrenceService : IRecurrenceService
{
    public const int MaxOccurrences = RruleFormatter.MaxOccurrences;   // 730

    public string ToRrule(RecurrencePatternDto pattern) => RruleFormatter.ToRrule(pattern);

    public RecurrencePatternDto ParseRrule(string rrule) => RruleFormatter.Parse(rrule);

    public IReadOnlyList<TimeSlot> Expand(string? rrule, DateTime startAt, DateTime endAt)
    {
        var duration = endAt - startAt;
        if (duration <= TimeSpan.Zero)
            throw new ValidationException("結束時間必須晚於開始時間");

        if (string.IsNullOrWhiteSpace(rrule))
            return new[] { new TimeSlot(startAt, endAt) };

        var pattern = RruleFormatter.Parse(rrule);   // 同時驗證結束條件必填
        ValidateStartMatches(pattern, startAt);

        var ev = new CalendarEvent
        {
            DtStart = ToCal(startAt),
            DtEnd = ToCal(endAt),
            RecurrenceRules = new List<RecurrencePattern> { ToIcal(pattern) },
        };

        // 規則一定有 UNTIL 或 COUNT，所以列舉必然終止；Take 只是防呆上限。
        var starts = ev.GetOccurrences()
                       .Take(MaxOccurrences + 1)
                       .Select(o => o.Period.StartTime.Value)
                       .ToList();

        if (starts.Count > MaxOccurrences)
            throw new ValidationException($"重複次數超過上限（{MaxOccurrences} 次），請縮短結束日期");

        if (starts.Count == 0)
            throw new ValidationException("此重複規則不會產生任何發生時間，請檢查設定");

        return starts
            .Select(s => new TimeSlot(DateTime.SpecifyKind(s, DateTimeKind.Unspecified),
                                      DateTime.SpecifyKind(s, DateTimeKind.Unspecified) + duration))
            .ToList();
    }

    public void ValidateStartMatches(RecurrencePatternDto p, DateTime startAt)
    {
        const string message = "重複規則與事件起始日不一致，請調整起始日或重複設定";

        switch (p.Frequency)
        {
            case RecurrenceFrequency.Daily:
                return;

            case RecurrenceFrequency.Weekly:
                if (!p.ByWeekDays.Contains(startAt.DayOfWeek)) throw new ValidationException(message);
                return;

            case RecurrenceFrequency.Monthly when p.MonthlyMode == MonthlyMode.DayOfMonth:
                if (p.ByMonthDay != startAt.Day) throw new ValidationException(message);
                return;

            case RecurrenceFrequency.Monthly:
                if (p.ByPositionWeekDay != startAt.DayOfWeek) throw new ValidationException(message);
                if (p.BySetPosition == -1)
                {
                    var daysInMonth = DateTime.DaysInMonth(startAt.Year, startAt.Month);
                    if (startAt.Day + 7 <= daysInMonth) throw new ValidationException(message);
                }
                else if (p.BySetPosition != (startAt.Day - 1) / 7 + 1)
                {
                    throw new ValidationException(message);
                }
                return;

            case RecurrenceFrequency.Yearly:
                if (p.ByMonth != startAt.Month || p.ByMonthDay != startAt.Day)
                    throw new ValidationException(message);
                return;
        }
    }

    /// <summary>不帶時區的 floating time。台灣無日光節約，浮動時間即台北當地時間。</summary>
    private static CalDateTime ToCal(DateTime dt)
        => new(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second);

    private static RecurrencePattern ToIcal(RecurrencePatternDto p)
    {
        var r = new RecurrencePattern
        {
            Frequency = p.Frequency switch
            {
                RecurrenceFrequency.Daily => FrequencyType.Daily,
                RecurrenceFrequency.Weekly => FrequencyType.Weekly,
                RecurrenceFrequency.Monthly => FrequencyType.Monthly,
                _ => FrequencyType.Yearly,
            },
            Interval = p.Interval,
        };

        if (p.Frequency == RecurrenceFrequency.Weekly)
            r.ByDay = p.ByWeekDays.Select(d => new WeekDay(d)).ToList();

        if (p.Frequency == RecurrenceFrequency.Monthly && p.MonthlyMode == MonthlyMode.WeekDayOfMonth)
        {
            r.ByDay = new List<WeekDay> { new(p.ByPositionWeekDay!.Value) };
            r.BySetPosition = new List<int> { p.BySetPosition!.Value };
        }

        if (p.Frequency == RecurrenceFrequency.Monthly && p.MonthlyMode == MonthlyMode.DayOfMonth)
            r.ByMonthDay = new List<int> { p.ByMonthDay!.Value };

        if (p.Frequency == RecurrenceFrequency.Yearly)
        {
            r.ByMonth = new List<int> { p.ByMonth!.Value };
            r.ByMonthDay = new List<int> { p.ByMonthDay!.Value };
        }

        if (p.EndMode == RecurrenceEndMode.Count)
            r.Count = p.Count!.Value;
        else
            r.Until = new CalDateTime(p.UntilDate!.Value.Year, p.UntilDate.Value.Month,
                                      p.UntilDate.Value.Day, 23, 59, 59);

        return r;
    }
}
```

- [ ] **步驟 5：運行測試驗證通過**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~Recurrence|FullyQualifiedName~IcalNet"
```

預期：Passed! 11 passed。若某個展開結果與斷言差一天／差一次，**先確認斷言本身的日曆事實正確**，再調整 `ToIcal`。

- [ ] **步驟 6：Commit**

```bash
git add -A
git commit -m "feat: 新增 RecurrenceService，以 Ical.Net 展開重複規則並套用 730 筆上限"
```

---

### 任務 4：Repository 層與重疊判定

**文件：**
- 創建：`src/OfficeCal.Services/OverlapChecker.cs`
- 創建：`src/OfficeCal.Infrastructure/Repositories/IRoomRepository.cs`
- 創建：`src/OfficeCal.Infrastructure/Repositories/RoomRepository.cs`
- 創建：`src/OfficeCal.Infrastructure/Repositories/IEventOccurrenceRepository.cs`
- 創建：`src/OfficeCal.Infrastructure/Repositories/EventOccurrenceRepository.cs`
- 測試：`tests/OfficeCal.Tests/Unit/OverlapCheckerTests.cs`
- 測試：`tests/OfficeCal.Tests/Fixtures/TestData.cs`
- 測試：`tests/OfficeCal.Tests/Integration/EventOccurrenceRepositoryTests.cs`

- [ ] **步驟 1：編寫失敗的重疊判定測試**

`tests/OfficeCal.Tests/Unit/OverlapCheckerTests.cs`：

```csharp
using OfficeCal.Services;
using Xunit;

namespace OfficeCal.Tests.Unit;

public class OverlapCheckerTests
{
    private static DateTime T(int hour, int minute = 0) => new(2026, 9, 7, hour, minute, 0);

    [Theory]
    // 完全重疊
    [InlineData(9, 10, 9, 10, true)]
    // 部分重疊（新的較早開始）
    [InlineData(9, 11, 10, 12, true)]
    // 部分重疊（新的較晚開始）
    [InlineData(10, 12, 9, 11, true)]
    // 包含
    [InlineData(9, 12, 10, 11, true)]
    // 被包含
    [InlineData(10, 11, 9, 12, true)]
    // 頭尾相接 —— 規格 5.1 明訂不算衝突
    [InlineData(9, 10, 10, 11, false)]
    [InlineData(10, 11, 9, 10, false)]
    // 完全分離
    [InlineData(9, 10, 14, 15, false)]
    public void 重疊判定(int aStart, int aEnd, int bStart, int bEnd, bool expected)
        => Assert.Equal(expected, OverlapChecker.Overlaps(T(aStart), T(aEnd), T(bStart), T(bEnd)));
}
```

- [ ] **步驟 2：運行測試驗證失敗**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~OverlapCheckerTests"
```

預期：編譯失敗，`OverlapChecker` 不存在。

- [ ] **步驟 3：實作 `OverlapChecker`**

`src/OfficeCal.Services/OverlapChecker.cs`：

```csharp
namespace OfficeCal.Services;

/// <summary>
/// 規格 5.1 的重疊判定：新起 &lt; 舊迄 AND 新迄 &gt; 舊起。
/// 頭尾相接（09:00–10:00 與 10:00–11:00）不算衝突。
/// </summary>
public static class OverlapChecker
{
    public static bool Overlaps(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd)
        => aStart < bEnd && aEnd > bStart;
}
```

- [ ] **步驟 4：運行測試驗證通過**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~OverlapCheckerTests"
```

預期：Passed! 8 passed。

- [ ] **步驟 5：定義 Repository 介面**

`src/OfficeCal.Infrastructure/Repositories/IRoomRepository.cs`：

```csharp
using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure.Repositories;

public interface IRoomRepository
{
    Task<Room?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<List<Room>> ListAsync(bool activeOnly, CancellationToken ct = default);

    /// <summary>
    /// 於呼叫端已開啟的交易內，對 Rooms 中該列下 UPDLOCK, HOLDLOCK 並回傳它。
    /// 這是全系統防止雙重預約的關鍵：所有寫入 EventOccurrence 的路徑都必須先經過這裡。
    /// 會議廳不存在時回傳 null。
    /// </summary>
    Task<Room?> LockAndGetAsync(int roomId, CancellationToken ct = default);

    void Add(Room room);
}
```

`src/OfficeCal.Infrastructure/Repositories/IEventOccurrenceRepository.cs`：

```csharp
using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure.Repositories;

public interface IEventOccurrenceRepository
{
    /// <summary>
    /// 取得某會議廳在 [from, to) 區間內、未取消的 occurrence，供衝突偵測比對。
    /// excludeEventId 不為 null 時排除該事件自己的 occurrence（編輯既有事件時使用）。
    /// 已 Include Event、Event.Owner、Room，供組裝 409 明細。
    /// </summary>
    Task<List<EventOccurrence>> GetRoomOccurrencesAsync(
        int roomId, DateTime from, DateTime to, int? excludeEventId, CancellationToken ct = default);

    /// <summary>scope=me：使用者擁有或被邀請的 occurrence。</summary>
    Task<List<EventOccurrence>> GetRangeForUserAsync(
        int userId, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>scope=room：指定會議廳的所有 occurrence，不分擁有者。</summary>
    Task<List<EventOccurrence>> GetRangeForRoomAsync(
        int roomId, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>scope=all：所有已掛會議廳的 occurrence（不含他人的純個人事件）。</summary>
    Task<List<EventOccurrence>> GetRangeAllRoomsAsync(
        DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>與會者行程衝突警示用：這批使用者擁有或被邀請的 occurrence。</summary>
    Task<List<EventOccurrence>> GetRangeForUsersAsync(
        IReadOnlyCollection<int> userIds, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>單筆編輯用，回傳受追蹤的實體（要寫回）。</summary>
    Task<EventOccurrence?> GetTrackedByIdAsync(int occurrenceId, CancellationToken ct = default);

    /// <summary>系列重新展開用，回傳受追蹤的整串 occurrence。</summary>
    Task<List<EventOccurrence>> GetTrackedByEventAsync(int eventId, CancellationToken ct = default);
}
```

- [ ] **步驟 6：實作 Repository**

`src/OfficeCal.Infrastructure/Repositories/RoomRepository.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure.Repositories;

public class RoomRepository : IRoomRepository
{
    private readonly OfficeCalDbContext _db;
    public RoomRepository(OfficeCalDbContext db) => _db = db;

    public Task<Room?> GetByIdAsync(int id, CancellationToken ct = default)
        => _db.Rooms.FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<List<Room>> ListAsync(bool activeOnly, CancellationToken ct = default)
        => _db.Rooms.AsNoTracking()
                    .Where(r => !activeOnly || r.IsActive)
                    .OrderBy(r => r.Name)
                    .ToListAsync(ct);

    public async Task<Room?> LockAndGetAsync(int roomId, CancellationToken ct = default)
    {
        // 不可在此加上任何 LINQ 運算子：一旦組合，EF 會把這段 SQL 包成子查詢，
        // 資料表提示就未必落在正確的位置。ToListAsync 會原樣送出這段 SQL。
        var rows = await _db.Rooms
            .FromSqlInterpolated($"SELECT * FROM Rooms WITH (UPDLOCK, HOLDLOCK) WHERE Id = {roomId}")
            .ToListAsync(ct);
        return rows.FirstOrDefault();
    }

    public void Add(Room room) => _db.Rooms.Add(room);
}
```

`src/OfficeCal.Infrastructure/Repositories/EventOccurrenceRepository.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;

namespace OfficeCal.Infrastructure.Repositories;

public class EventOccurrenceRepository : IEventOccurrenceRepository
{
    private readonly OfficeCalDbContext _db;
    public EventOccurrenceRepository(OfficeCalDbContext db) => _db = db;

    private IQueryable<EventOccurrence> WithDetails()
        => _db.EventOccurrences.AsNoTracking()
              .Include(o => o.Event!).ThenInclude(e => e.Owner)
              .Include(o => o.Room);

    public Task<List<EventOccurrence>> GetRoomOccurrencesAsync(
        int roomId, DateTime from, DateTime to, int? excludeEventId, CancellationToken ct = default)
        => WithDetails()
            .Where(o => o.RoomId == roomId && !o.IsCancelled
                        && o.StartAt < to && o.EndAt > from
                        && (excludeEventId == null || o.EventId != excludeEventId))
            .ToListAsync(ct);

    public Task<List<EventOccurrence>> GetRangeForUserAsync(
        int userId, DateTime from, DateTime to, CancellationToken ct = default)
        => WithDetails()
            .Where(o => !o.IsCancelled && o.StartAt < to && o.EndAt > from
                        && o.Event!.Status == EventStatus.Active
                        && (o.Event.OwnerId == userId
                            || o.Event.Attendees.Any(a => a.UserId == userId)))
            .OrderBy(o => o.StartAt)
            .ToListAsync(ct);

    public Task<List<EventOccurrence>> GetRangeForRoomAsync(
        int roomId, DateTime from, DateTime to, CancellationToken ct = default)
        => WithDetails()
            .Where(o => !o.IsCancelled && o.RoomId == roomId && o.StartAt < to && o.EndAt > from
                        && o.Event!.Status == EventStatus.Active)
            .OrderBy(o => o.StartAt)
            .ToListAsync(ct);

    public Task<List<EventOccurrence>> GetRangeAllRoomsAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
        => WithDetails()
            .Where(o => !o.IsCancelled && o.RoomId != null && o.StartAt < to && o.EndAt > from
                        && o.Event!.Status == EventStatus.Active)
            .OrderBy(o => o.StartAt)
            .ToListAsync(ct);

    // 這個查詢刻意不用 WithDetails()：呼叫端（CheckAttendeesAsync）要在記憶體裡
    // 判斷每一筆是「誰的」行程，因此必須連 Attendees 一起載入。
    // 少了這個 Include，AsNoTracking 查詢回來的 Attendees 會是空清單，
    // 「被邀請」而忙碌的與會者就會被算成 0 次衝突。
    public Task<List<EventOccurrence>> GetRangeForUsersAsync(
        IReadOnlyCollection<int> userIds, DateTime from, DateTime to, CancellationToken ct = default)
        => _db.EventOccurrences.AsNoTracking()
            .Include(o => o.Event!).ThenInclude(e => e.Owner)
            .Include(o => o.Event!).ThenInclude(e => e.Attendees)
            .Include(o => o.Room)
            .Where(o => !o.IsCancelled && o.StartAt < to && o.EndAt > from
                        && o.Event!.Status == EventStatus.Active
                        && (userIds.Contains(o.Event.OwnerId)
                            || o.Event.Attendees.Any(a => userIds.Contains(a.UserId))))
            .ToListAsync(ct);

    public Task<EventOccurrence?> GetTrackedByIdAsync(int occurrenceId, CancellationToken ct = default)
        => _db.EventOccurrences
              .Include(o => o.Event)
              .FirstOrDefaultAsync(o => o.Id == occurrenceId, ct);

    public Task<List<EventOccurrence>> GetTrackedByEventAsync(int eventId, CancellationToken ct = default)
        => _db.EventOccurrences.Where(o => o.EventId == eventId)
                               .OrderBy(o => o.OriginalStartAt)
                               .ToListAsync(ct);
}
```

- [ ] **步驟 7：寫測試資料輔助**

`tests/OfficeCal.Tests/Fixtures/TestData.cs`：

```csharp
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;
using OfficeCal.Infrastructure;

namespace OfficeCal.Tests.Fixtures;

public static class TestData
{
    public static async Task<User> AddUserAsync(OfficeCalDbContext db, string employeeNo,
                                                string name, UserRole role = UserRole.Employee)
    {
        var u = new User
        {
            EmployeeNo = employeeNo,
            DisplayName = name,
            Email = $"{employeeNo.ToLowerInvariant()}@corp.local",
            PasswordHash = "not-a-real-hash",
            Role = role,
            IcsFeedToken = Guid.NewGuid().ToString("N"),
        };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    public static async Task<Room> AddRoomAsync(OfficeCalDbContext db, string name,
                                                int capacity = 10, bool isActive = true)
    {
        var r = new Room { Name = name, Capacity = capacity, IsActive = isActive, Location = "A 棟 3F" };
        db.Rooms.Add(r);
        await db.SaveChangesAsync();
        return r;
    }

    /// <summary>建立一個已占用某會議廳的單次事件（直接寫庫，不經過 BookingService）。</summary>
    public static async Task<Event> AddBookedEventAsync(OfficeCalDbContext db, User owner, Room? room,
                                                        DateTime start, DateTime end,
                                                        string title = "既有會議",
                                                        bool cancelled = false)
    {
        var ev = new Event
        {
            Title = title,
            OwnerId = owner.Id,
            RoomId = room?.Id,
            StartAt = start,
            EndAt = end,
            Status = EventStatus.Active,
            CreatedAt = start,
            UpdatedAt = start,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        db.EventOccurrences.Add(new EventOccurrence
        {
            EventId = ev.Id,
            OriginalStartAt = start,
            StartAt = start,
            EndAt = end,
            RoomId = room?.Id,
            IsCancelled = cancelled,
        });
        await db.SaveChangesAsync();
        return ev;
    }
}
```

- [ ] **步驟 8：編寫失敗的 Repository 整合測試**

`tests/OfficeCal.Tests/Integration/EventOccurrenceRepositoryTests.cs`：

```csharp
using OfficeCal.Infrastructure.Repositories;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("LocalDb")]
public class EventOccurrenceRepositoryTests
{
    private readonly LocalDbFixture _db;
    public EventOccurrenceRepositoryTests(LocalDbFixture db) => _db = db;

    private static DateTime T(int day, int hour) => new(2026, 9, day, hour, 0, 0);

    [Fact]
    public async Task 會議廳查詢排除已取消與頭尾相接的時段()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");

        await TestData.AddBookedEventAsync(ctx, owner, room, T(14, 10), T(14, 11), "重疊的會議");
        await TestData.AddBookedEventAsync(ctx, owner, room, T(14, 11), T(14, 12), "頭尾相接的會議");
        await TestData.AddBookedEventAsync(ctx, owner, room, T(14, 10), T(14, 11), "已取消的會議",
                                           cancelled: true);

        var repo = new EventOccurrenceRepository(ctx);
        var found = await repo.GetRoomOccurrencesAsync(room.Id, T(14, 10), T(14, 11), null);

        Assert.Single(found);
        Assert.Equal("重疊的會議", found[0].Event!.Title);
    }

    [Fact]
    public async Task 會議廳查詢可排除指定事件自己的Occurrence()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var ev = await TestData.AddBookedEventAsync(ctx, owner, room, T(14, 10), T(14, 11));

        var repo = new EventOccurrenceRepository(ctx);

        Assert.Single(await repo.GetRoomOccurrencesAsync(room.Id, T(14, 10), T(14, 11), null));
        Assert.Empty(await repo.GetRoomOccurrencesAsync(room.Id, T(14, 10), T(14, 11), ev.Id));
    }

    [Fact]
    public async Task 個人範圍查詢涵蓋擁有與被邀請但不含他人的私人事件()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var me = await TestData.AddUserAsync(ctx, "E001", "我");
        var other = await TestData.AddUserAsync(ctx, "E002", "別人");

        await TestData.AddBookedEventAsync(ctx, me, null, T(14, 9), T(14, 10), "我的私人事件");
        var invited = await TestData.AddBookedEventAsync(ctx, other, null, T(14, 13), T(14, 14),
                                                         "我被邀請的事件");
        ctx.EventAttendees.Add(new OfficeCal.Core.Entities.EventAttendee
        {
            EventId = invited.Id, UserId = me.Id,
        });
        await ctx.SaveChangesAsync();
        await TestData.AddBookedEventAsync(ctx, other, null, T(14, 15), T(14, 16), "別人的私人事件");

        var repo = new EventOccurrenceRepository(ctx);
        var mine = await repo.GetRangeForUserAsync(me.Id, T(14, 0), T(15, 0));

        Assert.Equal(2, mine.Count);
        Assert.DoesNotContain(mine, o => o.Event!.Title == "別人的私人事件");
    }

    [Fact]
    public async Task 全域範圍查詢只回傳已掛會議廳的Occurrence()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");

        await TestData.AddBookedEventAsync(ctx, owner, room, T(14, 10), T(14, 11), "會議室預約");
        await TestData.AddBookedEventAsync(ctx, owner, null, T(14, 10), T(14, 11), "純個人事件");

        var repo = new EventOccurrenceRepository(ctx);
        var all = await repo.GetRangeAllRoomsAsync(T(14, 0), T(15, 0));

        Assert.Single(all);
        Assert.Equal("會議室預約", all[0].Event!.Title);
    }
}
```

- [ ] **步驟 9：運行測試驗證失敗**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~EventOccurrenceRepositoryTests"
```

預期：編譯失敗（Repository 未建立）或 FAIL。

- [ ] **步驟 10：運行測試驗證通過**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~EventOccurrenceRepositoryTests"
```

預期：Passed! 4 passed。

- [ ] **步驟 11：Commit**

```bash
git add -A
git commit -m "feat: 新增重疊判定與 occurrence／會議廳 Repository"
```

---

### 任務 5：`BookingService` —— 建立事件的交易鎖與衝突偵測

規格 5.2 的核心。**本任務只實作「建立全新 occurrence」與「取消」；系列重新展開與單筆改期在任務 7。**

**文件：**
- 創建：`src/OfficeCal.Services/IBookingService.cs`
- 創建：`src/OfficeCal.Services/BookingService.cs`
- 測試：`tests/OfficeCal.Tests/Integration/BookingServiceTests.cs`

- [ ] **步驟 1：編寫失敗的測試**

`tests/OfficeCal.Tests/Integration/BookingServiceTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;
using OfficeCal.Services;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("LocalDb")]
public class BookingServiceTests
{
    private readonly LocalDbFixture _db;
    public BookingServiceTests(LocalDbFixture db) => _db = db;

    private static DateTime T(int day, int hour) => new(2026, 9, day, hour, 0, 0);

    private static BookingService NewService(OfficeCalDbContext ctx)
        => new(ctx, new RoomRepository(ctx), new EventOccurrenceRepository(ctx));

    private static Event NewEvent(User owner, Room? room, DateTime start, DateTime end,
                                  string title = "新會議")
        => new()
        {
            Title = title, OwnerId = owner.Id, RoomId = room?.Id,
            StartAt = start, EndAt = end,
            CreatedAt = start, UpdatedAt = start,
        };

    /// <summary>模擬 EventService：開交易、存 Event 取得 Id、呼叫 BookingService、提交。</summary>
    private static async Task BookAsync(OfficeCalDbContext ctx, Event ev, IReadOnlyList<TimeSlot> slots)
    {
        await using var tx = await ctx.Database.BeginTransactionAsync();
        ctx.Events.Add(ev);
        await ctx.SaveChangesAsync();
        await NewService(ctx).CreateOccurrencesAsync(ev, slots);
        await tx.CommitAsync();
    }

    [Fact]
    public async Task 未指派會議廳的事件不做衝突檢查()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");

        await TestData.AddBookedEventAsync(ctx, owner, null, T(14, 10), T(14, 11), "既有個人事件");
        var ev = NewEvent(owner, null, T(14, 10), T(14, 11));

        await BookAsync(ctx, ev, new[] { new TimeSlot(T(14, 10), T(14, 11)) });

        Assert.Equal(2, await ctx.EventOccurrences.CountAsync());
    }

    [Fact]
    public async Task 會議廳時段重疊時丟出衝突例外且不寫入任何資料()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        await TestData.AddBookedEventAsync(ctx, owner, room, T(14, 10), T(14, 11), "季度檢討會");

        var ev = NewEvent(owner, room, T(14, 10), T(14, 11));
        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => BookAsync(ctx, ev, new[] { new TimeSlot(T(14, 10), T(14, 11)) }));

        Assert.Single(ex.Conflicts);
        Assert.Equal("季度檢討會", ex.Conflicts[0].Title);
        Assert.Equal("陳大明", ex.Conflicts[0].OwnerName);
        Assert.Equal("A 棟 3F 大會議廳", ex.Conflicts[0].RoomName);

        await using var verify = _db.CreateContext();
        Assert.Equal(1, await verify.EventOccurrences.CountAsync());   // 只有既有那一筆
        Assert.Equal(1, await verify.Events.CountAsync());             // 新 Event 也已回滾
    }

    [Fact]
    public async Task 頭尾相接不算衝突()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        await TestData.AddBookedEventAsync(ctx, owner, room, T(14, 9), T(14, 10));

        var ev = NewEvent(owner, room, T(14, 10), T(14, 11));
        await BookAsync(ctx, ev, new[] { new TimeSlot(T(14, 10), T(14, 11)) });

        Assert.Equal(2, await ctx.EventOccurrences.CountAsync());
    }

    [Fact]
    public async Task 已取消的Occurrence不參與衝突判定()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        await TestData.AddBookedEventAsync(ctx, owner, room, T(14, 10), T(14, 11), cancelled: true);

        var ev = NewEvent(owner, room, T(14, 10), T(14, 11));
        await BookAsync(ctx, ev, new[] { new TimeSlot(T(14, 10), T(14, 11)) });

        Assert.Equal(1, await ctx.EventOccurrences.CountAsync(o => !o.IsCancelled));
    }

    [Fact]
    public async Task 重複事件中任一次衝突就整筆失敗()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        // 只有第三次（9/21）撞到
        await TestData.AddBookedEventAsync(ctx, owner, room, T(21, 10), T(21, 11), "既有會議");

        var ev = NewEvent(owner, room, T(7, 10), T(7, 11), "週一產品例會");
        var slots = new[]
        {
            new TimeSlot(T(7, 10), T(7, 11)),
            new TimeSlot(T(14, 10), T(14, 11)),
            new TimeSlot(T(21, 10), T(21, 11)),
        };

        var ex = await Assert.ThrowsAsync<ConflictException>(() => BookAsync(ctx, ev, slots));
        Assert.Single(ex.Conflicts);

        await using var verify = _db.CreateContext();
        Assert.Equal(1, await verify.EventOccurrences.CountAsync());
    }

    [Fact]
    public async Task 停用的會議廳不可新增預約()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "已停用的會議廳", isActive: false);

        var ev = NewEvent(owner, room, T(14, 10), T(14, 11));
        await Assert.ThrowsAsync<ValidationException>(
            () => BookAsync(ctx, ev, new[] { new TimeSlot(T(14, 10), T(14, 11)) }));
    }

    [Fact]
    public async Task 沒有交易就呼叫會被擋下()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var ev = NewEvent(owner, room, T(14, 10), T(14, 11));
        ctx.Events.Add(ev);
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService(ctx).CreateOccurrencesAsync(ev, new[] { new TimeSlot(T(14, 10), T(14, 11)) }));
    }
}
```

> 註：`T` 的簽章是 `T(int day, int hour)`，全部日期都落在 2026 年 9 月。

- [ ] **步驟 2：運行測試驗證失敗**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~BookingServiceTests"
```

預期：編譯失敗，`IBookingService` / `BookingService` 不存在。

- [ ] **步驟 3：實作 `IBookingService`（本任務版本）**

`src/OfficeCal.Services/IBookingService.cs`：

```csharp
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;

namespace OfficeCal.Services;

/// <summary>
/// 唯一能寫入 EventOccurrence 的地方（規格 6.1）。
/// 所有方法都必須在呼叫端已開啟的交易內執行——本服務自己不開交易、不提交。
/// 任務 7 會再加入 ReExpandSeriesAsync 與 MoveOccurrenceAsync。
/// </summary>
public interface IBookingService
{
    /// <summary>
    /// 鎖定目標會議廳、檢查衝突、寫入全新的 occurrence。
    /// ev.Id 必須已存在（呼叫端先 SaveChanges 取得）。
    /// </summary>
    Task CreateOccurrencesAsync(Event ev, IReadOnlyList<TimeSlot> slots, CancellationToken ct = default);

    /// <summary>取消單一次發生。釋出時段不可能造成雙重預約，因此不需取鎖。</summary>
    Task CancelOccurrenceAsync(EventOccurrence occ, CancellationToken ct = default);

    /// <summary>取消整個系列：Event.Status = Cancelled，所有 occurrence 設 IsCancelled。</summary>
    Task CancelSeriesAsync(Event ev, CancellationToken ct = default);
}
```

- [ ] **步驟 4：實作 `BookingService`**

`src/OfficeCal.Services/BookingService.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;

namespace OfficeCal.Services;

public class BookingService : IBookingService
{
    private readonly OfficeCalDbContext _db;
    private readonly IRoomRepository _rooms;
    private readonly IEventOccurrenceRepository _occurrences;

    public BookingService(OfficeCalDbContext db, IRoomRepository rooms,
                          IEventOccurrenceRepository occurrences)
        => (_db, _rooms, _occurrences) = (db, rooms, occurrences);

    public async Task CreateOccurrencesAsync(Event ev, IReadOnlyList<TimeSlot> slots,
                                             CancellationToken ct = default)
    {
        if (slots.Count == 0) throw new ValidationException("事件至少要有一次發生");

        if (ev.RoomId is int roomId)
        {
            await LockRoomForWriteAsync(roomId, ct);
            var conflicts = await FindConflictsAsync(roomId, slots, excludeEventId: ev.Id, ct);
            if (conflicts.Count > 0) throw new ConflictException("會議廳於下列時段已被預約", conflicts);
        }

        foreach (var s in slots)
        {
            _db.EventOccurrences.Add(new EventOccurrence
            {
                EventId = ev.Id,
                OriginalStartAt = s.Start,
                StartAt = s.Start,
                EndAt = s.End,
                RoomId = ev.RoomId,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task CancelOccurrenceAsync(EventOccurrence occ, CancellationToken ct = default)
    {
        occ.IsCancelled = true;
        await _db.SaveChangesAsync(ct);
    }

    public async Task CancelSeriesAsync(Event ev, CancellationToken ct = default)
    {
        ev.Status = EventStatus.Cancelled;
        var all = await _occurrences.GetTrackedByEventAsync(ev.Id, ct);
        foreach (var o in all) o.IsCancelled = true;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 取得會議廳的寫入鎖。這是整個系統防止雙重預約的唯一機制：
    /// 所有寫入該會議廳 occurrence 的交易都會在這裡序列化，因此後續的衝突查詢
    /// 不需要額外加鎖也不會讀到別的交易正在寫入的資料。
    /// </summary>
    private async Task LockRoomForWriteAsync(int roomId, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "BookingService 必須在呼叫端已開啟的交易內執行，否則 UPDLOCK/HOLDLOCK 會立即釋放。");

        var room = await _rooms.LockAndGetAsync(roomId, ct)
                   ?? throw new NotFoundException($"會議廳不存在（Id={roomId}）");

        if (!room.IsActive)
            throw new ValidationException($"會議廳「{room.Name}」已停用，無法新增預約");
    }

    private async Task<List<ConflictDetail>> FindConflictsAsync(
        int roomId, IReadOnlyList<TimeSlot> slots, int? excludeEventId, CancellationToken ct)
    {
        var from = slots.Min(s => s.Start);
        var to = slots.Max(s => s.End);

        var existing = await _occurrences.GetRoomOccurrencesAsync(roomId, from, to, excludeEventId, ct);

        return existing
            .Where(e => slots.Any(s => OverlapChecker.Overlaps(s.Start, s.End, e.StartAt, e.EndAt)))
            .Select(e => new ConflictDetail
            {
                OccurrenceId = e.Id,
                RoomName = e.Room?.Name ?? "",
                StartAt = e.StartAt,
                EndAt = e.EndAt,
                OwnerName = e.Event?.Owner?.DisplayName ?? "",
                Title = e.TitleOverride ?? e.Event?.Title ?? "",
            })
            .OrderBy(c => c.StartAt)
            .ToList();
    }
}
```

- [ ] **步驟 5：運行測試驗證通過**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~BookingServiceTests"
```

預期：Passed! 7 passed。

- [ ] **步驟 6：Commit**

```bash
git add -A
git commit -m "feat: 新增 BookingService，以會議廳列鎖與交易保證預約不重複"
```

---

### 任務 6：併發整合測試（驗收核心）

規格 10.3 與驗收標準 5。本任務不新增產品程式碼——它的價值在於證明任務 5 的鎖真的有效。

**文件：**
- 測試：`tests/OfficeCal.Tests/Integration/ConcurrentBookingTests.cs`

- [ ] **步驟 1：編寫併發測試**

`tests/OfficeCal.Tests/Integration/ConcurrentBookingTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure.Repositories;
using OfficeCal.Services;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("LocalDb")]
public class ConcurrentBookingTests
{
    private readonly LocalDbFixture _db;
    public ConcurrentBookingTests(LocalDbFixture db) => _db = db;

    /// <summary>
    /// 一次完整的預約嘗試：自己的 DbContext（＝自己的連線與交易）。
    /// 回傳 true 表示成功，false 表示收到 409 衝突。其他例外一律往外丟。
    /// </summary>
    private async Task<bool> TryBookAsync(int ownerId, int roomId, TimeSlot slot, Task gate)
    {
        await using var ctx = _db.CreateContext();
        var booking = new BookingService(ctx, new RoomRepository(ctx),
                                         new EventOccurrenceRepository(ctx));
        await gate;   // 兩個執行緒在此對齊，盡可能同時衝進交易

        await using var tx = await ctx.Database.BeginTransactionAsync();
        try
        {
            var ev = new Event
            {
                Title = "併發測試", OwnerId = ownerId, RoomId = roomId,
                StartAt = slot.Start, EndAt = slot.End,
                CreatedAt = slot.Start, UpdatedAt = slot.Start,
            };
            ctx.Events.Add(ev);
            await ctx.SaveChangesAsync();

            await booking.CreateOccurrencesAsync(ev, new[] { slot });
            await tx.CommitAsync();
            return true;
        }
        catch (ConflictException)
        {
            await tx.RollbackAsync();
            return false;
        }
    }

    [Fact]
    public async Task 同一會議廳同一時段連續五十輪都恰好一個成功()
    {
        await _db.ResetAsync();
        int ownerId, roomId;
        await using (var seed = _db.CreateContext())
        {
            ownerId = (await TestData.AddUserAsync(seed, "E001", "陳大明")).Id;
            roomId = (await TestData.AddRoomAsync(seed, "A 棟 3F 大會議廳")).Id;
        }

        for (var round = 0; round < 50; round++)
        {
            var start = new DateTime(2026, 9, 1, 10, 0, 0).AddDays(round);
            var slot = new TimeSlot(start, start.AddHours(1));

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var a = Task.Run(() => TryBookAsync(ownerId, roomId, slot, gate.Task));
            var b = Task.Run(() => TryBookAsync(ownerId, roomId, slot, gate.Task));
            gate.SetResult();

            var results = await Task.WhenAll(a, b);

            Assert.True(results.Count(x => x) == 1,
                $"第 {round} 輪應恰好一個成功，實際成功 {results.Count(x => x)} 個");

            await using var verify = _db.CreateContext();
            var written = await verify.EventOccurrences
                .CountAsync(o => o.RoomId == roomId && o.StartAt == slot.Start && !o.IsCancelled);
            Assert.Equal(1, written);
        }
    }

    [Fact]
    public async Task 不同會議廳的相同時段兩者都成功()
    {
        await _db.ResetAsync();
        int ownerId, roomA, roomB;
        await using (var seed = _db.CreateContext())
        {
            ownerId = (await TestData.AddUserAsync(seed, "E001", "陳大明")).Id;
            roomA = (await TestData.AddRoomAsync(seed, "A 棟 3F 大會議廳")).Id;
            roomB = (await TestData.AddRoomAsync(seed, "B 棟 2F 小會議室")).Id;
        }

        var start = new DateTime(2026, 9, 7, 10, 0, 0);
        var slot = new TimeSlot(start, start.AddHours(1));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = Task.Run(() => TryBookAsync(ownerId, roomA, slot, gate.Task));
        var b = Task.Run(() => TryBookAsync(ownerId, roomB, slot, gate.Task));
        gate.SetResult();

        var results = await Task.WhenAll(a, b);
        Assert.Equal(2, results.Count(x => x));   // 鎖的粒度是單一會議廳，不同會議廳互不阻塞
    }
}
```

- [ ] **步驟 2：運行測試**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~ConcurrentBookingTests"
```

預期：Passed! 2 passed。此測試會跑 100 次交易，約需 10–30 秒。

- [ ] **步驟 3：若失敗的排查順序**

| 症狀 | 原因與處置 |
|---|---|
| 兩個都成功 | 鎖沒生效。檢查 `RoomRepository.LockAndGetAsync` 的 SQL 是否原樣送出（在 SQL Server Profiler 或 `dotnet ef` 記錄中確認含 `WITH (UPDLOCK, HOLDLOCK)`），以及 `CreateOccurrencesAsync` 是否真的在交易內執行。 |
| 丟出 `SqlException: deadlock` | 檢查是否有人在同一交易內鎖了第二間會議廳（違反 D3）。 |
| 丟出 `InvalidOperationException` 提到執行策略 | 某處啟用了 `EnableRetryOnFailure`，移除它（見 D2）。 |
| 兩個都失敗 | 唯一索引 `(EventId, OriginalStartAt)` 誤擋——它是「同一事件」的約束，不同事件不該互相衝突。檢查任務 1 的索引定義。 |

- [ ] **步驟 4：Commit**

```bash
git add -A
git commit -m "test: 新增同一會議廳併發預約的 50 輪驗收測試"
```

---

### 任務 7：`BookingService` —— 系列重新展開與單筆改期

規格 5.4。**重點：所有會改動時段的編輯都必須走同一條加鎖流程，`mode=single` 也不例外。**

**文件：**
- 修改：`src/OfficeCal.Services/IBookingService.cs`（加入三個方法）
- 修改：`src/OfficeCal.Services/BookingService.cs`（建構式加入 `TimeProvider`，新增三個方法）
- 修改：`tests/OfficeCal.Tests/Integration/BookingServiceTests.cs`（`NewService` 補上時鐘參數）
- 修改：`tests/OfficeCal.Tests/Integration/ConcurrentBookingTests.cs`（同上，建構式多一個參數）
- 測試：`tests/OfficeCal.Tests/Integration/SeriesEditingTests.cs`

**注意：本任務會改動 `BookingService` 的建構式簽章，任務 5 與任務 6 的兩支測試都在建構它。** 步驟 5 會一次改完兩處；漏掉任何一處，步驟 6 的完整測試會編譯失敗。

**本任務採用的兩項明確假設（規格未直接寫明，實作時以此為準）：**

1. **系列更換會議廳時，被保留下來（`IsModified`）的未來 occurrence 也一併搬到新會議廳，並參與衝突檢查。** 另一種讀法是讓它們留在舊會議廳，但那會讓同一個事件同時占用兩間會議廳，而 `mode=single` 又不允許改會議廳——使用者將無法修正這個狀態。
2. **只鎖新的會議廳。** 釋放舊會議廳的時段不可能製造雙重預約，而同時鎖兩間會議廳會造成鎖順序不一致的死鎖（見 D3）。

- [ ] **步驟 1：編寫失敗的測試**

`tests/OfficeCal.Tests/Integration/SeriesEditingTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;
using OfficeCal.Services;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("LocalDb")]
public class SeriesEditingTests
{
    private readonly LocalDbFixture _db;
    public SeriesEditingTests(LocalDbFixture db) => _db = db;

    /// <summary>2026-09-15（週二）12:00 —— 讓 9/7、9/14 落在過去，9/21、9/28 落在未來。</summary>
    private static readonly DateTime Now = new(2026, 9, 15, 12, 0, 0);

    private static DateTime D(int day, int hour) => new(2026, 9, day, hour, 0, 0);

    private static BookingService NewService(OfficeCalDbContext ctx)
        => new(ctx, new RoomRepository(ctx), new EventOccurrenceRepository(ctx),
               new FixedTimeProvider(Now));

    /// <summary>建立每週一 10:00–11:00、共四次（9/7、9/14、9/21、9/28）的系列。</summary>
    private static async Task<Event> AddWeeklySeriesAsync(OfficeCalDbContext db, User owner, Room? room)
    {
        var ev = new Event
        {
            Title = "週一產品例會", OwnerId = owner.Id, RoomId = room?.Id,
            StartAt = D(7, 10), EndAt = D(7, 11),
            RecurrenceRule = "FREQ=WEEKLY;INTERVAL=1;BYDAY=MO;COUNT=4",
            CreatedAt = D(1, 9), UpdatedAt = D(1, 9),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        foreach (var day in new[] { 7, 14, 21, 28 })
        {
            db.EventOccurrences.Add(new EventOccurrence
            {
                EventId = ev.Id, OriginalStartAt = D(day, 10),
                StartAt = D(day, 10), EndAt = D(day, 11), RoomId = room?.Id,
            });
        }
        await db.SaveChangesAsync();
        return ev;
    }

    private static IReadOnlyList<TimeSlot> SlotsAt(int hour)
        => new[] { 7, 14, 21, 28 }.Select(d => new TimeSlot(D(d, hour), D(d, hour + 1))).ToList();

    private static async Task InTransactionAsync(OfficeCalDbContext ctx, Func<Task> body)
    {
        await using var tx = await ctx.Database.BeginTransactionAsync();
        await body();
        await tx.CommitAsync();
    }

    [Fact]
    public async Task 重新展開保留被單獨修改與被單獨取消的發生()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var ev = await AddWeeklySeriesAsync(ctx, owner, null);

        var o21 = await ctx.EventOccurrences.FirstAsync(o => o.OriginalStartAt == D(21, 10));
        o21.StartAt = D(21, 14); o21.EndAt = D(21, 15); o21.IsModified = true;
        var o28 = await ctx.EventOccurrences.FirstAsync(o => o.OriginalStartAt == D(28, 10));
        o28.IsCancelled = true;
        await ctx.SaveChangesAsync();

        await InTransactionAsync(ctx, () => NewService(ctx).ReExpandSeriesAsync(ev, SlotsAt(10)));

        await using var verify = _db.CreateContext();
        var all = await verify.EventOccurrences.OrderBy(o => o.OriginalStartAt).ToListAsync();

        Assert.Equal(4, all.Count);
        Assert.Equal(D(21, 14), all.Single(o => o.OriginalStartAt == D(21, 10)).StartAt);
        Assert.True(all.Single(o => o.OriginalStartAt == D(28, 10)).IsCancelled);
    }

    [Fact]
    public async Task 重新展開不改動已發生過的發生()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var ev = await AddWeeklySeriesAsync(ctx, owner, null);

        // 整個系列改到 14:00
        ev.StartAt = D(7, 14); ev.EndAt = D(7, 15);
        await InTransactionAsync(ctx, () => NewService(ctx).ReExpandSeriesAsync(ev, SlotsAt(14)));

        await using var verify = _db.CreateContext();
        var all = await verify.EventOccurrences.OrderBy(o => o.StartAt).ToListAsync();

        Assert.Equal(4, all.Count);
        Assert.Equal(D(7, 10), all[0].StartAt);    // 已發生，維持 10:00
        Assert.Equal(D(14, 10), all[1].StartAt);   // 已發生，維持 10:00
        Assert.Equal(D(21, 14), all[2].StartAt);   // 未來，改為 14:00
        Assert.Equal(D(28, 14), all[3].StartAt);
    }

    [Fact]
    public async Task 重新展開不會為已保留的發生日期重複插入()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var ev = await AddWeeklySeriesAsync(ctx, owner, null);

        var o21 = await ctx.EventOccurrences.FirstAsync(o => o.OriginalStartAt == D(21, 10));
        o21.IsCancelled = true;
        await ctx.SaveChangesAsync();

        // 用同一組時段重新展開：9/21 已被單獨取消，不得再長回來
        await InTransactionAsync(ctx, () => NewService(ctx).ReExpandSeriesAsync(ev, SlotsAt(10)));

        await using var verify = _db.CreateContext();
        Assert.Equal(1, await verify.EventOccurrences.CountAsync(o => o.OriginalStartAt == D(21, 10)));
        Assert.True(await verify.EventOccurrences
            .Where(o => o.OriginalStartAt == D(21, 10)).Select(o => o.IsCancelled).SingleAsync());
    }

    [Fact]
    public async Task 系列換會議廳時保留的未來發生一併搬過去()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var roomA = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var roomB = await TestData.AddRoomAsync(ctx, "B 棟 2F 小會議室");
        var ev = await AddWeeklySeriesAsync(ctx, owner, roomA);

        var o21 = await ctx.EventOccurrences.FirstAsync(o => o.OriginalStartAt == D(21, 10));
        o21.StartAt = D(21, 14); o21.EndAt = D(21, 15); o21.IsModified = true;
        await ctx.SaveChangesAsync();

        ev.RoomId = roomB.Id;
        await InTransactionAsync(ctx, () => NewService(ctx).ReExpandSeriesAsync(ev, SlotsAt(10)));

        await using var verify = _db.CreateContext();
        var all = await verify.EventOccurrences.OrderBy(o => o.OriginalStartAt).ToListAsync();

        Assert.Equal(roomA.Id, all.Single(o => o.OriginalStartAt == D(7, 10)).RoomId);   // 過去不動
        Assert.Equal(roomB.Id, all.Single(o => o.OriginalStartAt == D(21, 10)).RoomId);  // 保留的未來搬走
        Assert.Equal(roomB.Id, all.Single(o => o.OriginalStartAt == D(28, 10)).RoomId);  // 新產生的
    }

    [Fact]
    public async Task 系列換到已被占用的會議廳時整筆失敗()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var other = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var roomA = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var roomB = await TestData.AddRoomAsync(ctx, "B 棟 2F 小會議室");
        var ev = await AddWeeklySeriesAsync(ctx, owner, roomA);
        await TestData.AddBookedEventAsync(ctx, other, roomB, D(28, 10), D(28, 11), "季度檢討會");

        ev.RoomId = roomB.Id;
        await Assert.ThrowsAsync<ConflictException>(() =>
            InTransactionAsync(ctx, () => NewService(ctx).ReExpandSeriesAsync(ev, SlotsAt(10))));

        await using var verify = _db.CreateContext();
        // 交易已回滾：原系列仍在 roomA
        Assert.Equal(4, await verify.EventOccurrences.CountAsync(o => o.RoomId == roomA.Id));
    }

    [Fact]
    public async Task 單筆改期撞到既有預約時回衝突()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var other = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        await AddWeeklySeriesAsync(ctx, owner, room);
        await TestData.AddBookedEventAsync(ctx, other, room, D(21, 14), D(21, 15), "季度檢討會");

        var o21 = await ctx.EventOccurrences
            .FirstAsync(o => o.OriginalStartAt == D(21, 10) && o.RoomId == room.Id);

        await Assert.ThrowsAsync<ConflictException>(() => InTransactionAsync(ctx,
            () => NewService(ctx).MoveOccurrenceAsync(o21, D(21, 14), D(21, 15))));

        await using var verify = _db.CreateContext();
        Assert.Equal(D(21, 10), (await verify.EventOccurrences.FindAsync(o21.Id))!.StartAt);
    }

    [Fact]
    public async Task 單筆改期到空檔時成功並標記為已修改()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        await AddWeeklySeriesAsync(ctx, owner, room);

        var o21 = await ctx.EventOccurrences.FirstAsync(o => o.OriginalStartAt == D(21, 10));
        await InTransactionAsync(ctx,
            () => NewService(ctx).MoveOccurrenceAsync(o21, D(21, 14), D(21, 15)));

        await using var verify = _db.CreateContext();
        var moved = await verify.EventOccurrences.FindAsync(o21.Id);
        Assert.Equal(D(21, 14), moved!.StartAt);
        Assert.Equal(D(21, 10), moved.OriginalStartAt);   // 身分不變
        Assert.True(moved.IsModified);
    }

    [Fact]
    public async Task 僅改標題不需要交易也不做衝突檢查()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var other = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        await AddWeeklySeriesAsync(ctx, owner, room);
        // 同一時段另有一筆預約也無妨——改標題不碰時段
        var o21 = await ctx.EventOccurrences.FirstAsync(o => o.OriginalStartAt == D(21, 10));

        await NewService(ctx).SetOccurrenceTitleAsync(o21, "改期前的臨時議題");

        await using var verify = _db.CreateContext();
        var updated = await verify.EventOccurrences.FindAsync(o21.Id);
        Assert.Equal("改期前的臨時議題", updated!.TitleOverride);
        Assert.True(updated.IsModified);
        Assert.Equal(D(21, 10), updated.StartAt);
    }
}
```

- [ ] **步驟 2：運行測試驗證失敗**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~SeriesEditingTests"
```

預期：編譯失敗，`ReExpandSeriesAsync` / `MoveOccurrenceAsync` / `SetOccurrenceTitleAsync` 不存在，且 `BookingService` 建構式沒有第四個參數。

- [ ] **步驟 3：擴充 `IBookingService`（完整介面）**

`src/OfficeCal.Services/IBookingService.cs` 全文替換為：

```csharp
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;

namespace OfficeCal.Services;

/// <summary>
/// 唯一能寫入 EventOccurrence 的地方（規格 6.1）。
/// 所有方法都必須在呼叫端已開啟的交易內執行——本服務自己不開交易、不提交。
/// </summary>
public interface IBookingService
{
    /// <summary>鎖定目標會議廳、檢查衝突、寫入全新的 occurrence。ev.Id 必須已存在。</summary>
    Task CreateOccurrencesAsync(Event ev, IReadOnlyList<TimeSlot> slots, CancellationToken ct = default);

    /// <summary>
    /// 系列重新展開。保留「已發生過的」「被單獨修改的」「被單獨取消的」occurrence，
    /// 刪除其餘未來的，再依 slots 產生新的（跳過已保留的 OriginalStartAt）。
    /// 系列若換了會議廳，保留下來的未來 occurrence 一併搬到新會議廳並參與衝突檢查。
    /// </summary>
    Task ReExpandSeriesAsync(Event ev, IReadOnlyList<TimeSlot> slots, CancellationToken ct = default);

    /// <summary>
    /// 單筆改期（mode=single）。同樣要鎖會議廳並檢查衝突——
    /// 把一次發生移到別的時段一樣可能撞上既有預約。
    /// </summary>
    Task MoveOccurrenceAsync(EventOccurrence occ, DateTime newStart, DateTime newEnd,
                             CancellationToken ct = default);

    /// <summary>單筆僅改標題。不涉及時段，不取鎖、不檢查衝突、不需要交易。</summary>
    Task SetOccurrenceTitleAsync(EventOccurrence occ, string? title, CancellationToken ct = default);

    /// <summary>取消單一次發生。釋出時段不可能造成雙重預約，因此不需取鎖。</summary>
    Task CancelOccurrenceAsync(EventOccurrence occ, CancellationToken ct = default);

    /// <summary>取消整個系列：Event.Status = Cancelled，所有 occurrence 設 IsCancelled。</summary>
    Task CancelSeriesAsync(Event ev, CancellationToken ct = default);
}
```

- [ ] **步驟 4：修改 `BookingService` 的建構式並實作三個新方法**

`src/OfficeCal.Services/BookingService.cs` 的建構式替換為（其餘既有方法不動）：

```csharp
    private readonly OfficeCalDbContext _db;
    private readonly IRoomRepository _rooms;
    private readonly IEventOccurrenceRepository _occurrences;
    private readonly TimeProvider _clock;

    public BookingService(OfficeCalDbContext db, IRoomRepository rooms,
                          IEventOccurrenceRepository occurrences, TimeProvider clock)
        => (_db, _rooms, _occurrences, _clock) = (db, rooms, occurrences, clock);
```

在同一個類別中加入：

```csharp
    public async Task ReExpandSeriesAsync(Event ev, IReadOnlyList<TimeSlot> slots,
                                          CancellationToken ct = default)
    {
        var now = TaipeiTime.Now(_clock);
        var existing = await _occurrences.GetTrackedByEventAsync(ev.Id, ct);

        // 保留：已發生過的（不回頭改寫歷史）、被單獨修改的、被單獨取消的
        var survivors = existing
            .Where(o => o.StartAt <= now || o.IsModified || o.IsCancelled)
            .ToList();
        var toDelete = existing.Except(survivors).ToList();

        // 去重時要比對「全部」保留列的 OriginalStartAt，不只是被修改／取消的那些：
        // 已發生過的那幾次同樣是一次發生，不可以再長出第二列。
        var keptOriginalStarts = survivors.Select(o => o.OriginalStartAt).ToHashSet();

        var newSlots = slots
            .Where(s => s.Start > now && !keptOriginalStarts.Contains(s.Start))
            .ToList();

        // 系列換會議廳時，保留下來的未來 occurrence 也要搬過去（見任務 7 假設 1）
        var movedSurvivors = survivors
            .Where(o => o.StartAt > now && !o.IsCancelled && o.RoomId != ev.RoomId)
            .ToList();

        if (ev.RoomId is int roomId)
        {
            await LockRoomForWriteAsync(roomId, ct);

            var toCheck = newSlots
                .Concat(movedSurvivors.Select(o => new TimeSlot(o.StartAt, o.EndAt)))
                .ToList();

            if (toCheck.Count > 0)
            {
                var conflicts = await FindConflictsAsync(roomId, toCheck, excludeEventId: ev.Id, ct);
                if (conflicts.Count > 0)
                    throw new ConflictException("會議廳於下列時段已被預約", conflicts);
            }
        }

        _db.EventOccurrences.RemoveRange(toDelete);
        foreach (var o in movedSurvivors) o.RoomId = ev.RoomId;

        foreach (var s in newSlots)
        {
            _db.EventOccurrences.Add(new EventOccurrence
            {
                EventId = ev.Id,
                OriginalStartAt = s.Start,
                StartAt = s.Start,
                EndAt = s.End,
                RoomId = ev.RoomId,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task MoveOccurrenceAsync(EventOccurrence occ, DateTime newStart, DateTime newEnd,
                                          CancellationToken ct = default)
    {
        if (newEnd <= newStart) throw new ValidationException("結束時間必須晚於開始時間");

        if (occ.RoomId is int roomId)
        {
            await LockRoomForWriteAsync(roomId, ct);
            var conflicts = await FindConflictsAsync(
                roomId, new[] { new TimeSlot(newStart, newEnd) }, excludeEventId: occ.EventId, ct);
            if (conflicts.Count > 0) throw new ConflictException("會議廳於下列時段已被預約", conflicts);
        }

        occ.StartAt = newStart;
        occ.EndAt = newEnd;
        occ.IsModified = true;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetOccurrenceTitleAsync(EventOccurrence occ, string? title,
                                              CancellationToken ct = default)
    {
        occ.TitleOverride = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        occ.IsModified = true;
        await _db.SaveChangesAsync(ct);
    }
```

檔案頂端補上 `using OfficeCal.Core.Common;`（`TaipeiTime`）。

- [ ] **步驟 5：更新任務 5 與任務 6 的測試（建構式多了一個參數）**

`tests/OfficeCal.Tests/Integration/BookingServiceTests.cs` 中的 `NewService` 改為：

```csharp
    private static BookingService NewService(OfficeCalDbContext ctx)
        => new(ctx, new RoomRepository(ctx), new EventOccurrenceRepository(ctx),
               new FixedTimeProvider(new DateTime(2026, 9, 1, 0, 0, 0)));
```

`tests/OfficeCal.Tests/Integration/ConcurrentBookingTests.cs` 中 `TryBookAsync` 的那一行改為：

```csharp
        var booking = new BookingService(ctx, new RoomRepository(ctx),
                                         new EventOccurrenceRepository(ctx), TimeProvider.System);
```

併發測試只走 `CreateOccurrencesAsync`，不碰時鐘，所以用真實時鐘即可。兩個檔案頂端都要有 `using OfficeCal.Tests.Fixtures;`。

- [ ] **步驟 6：運行測試驗證通過**

```bash
dotnet test tests/OfficeCal.Tests
```

預期：全部通過（此時應有 40 上下的測試）。

- [ ] **步驟 7：Commit**

```bash
git add -A
git commit -m "feat: 系列重新展開與單筆改期，兩者皆走同一條加鎖流程"
```

---

## 階段二：應用服務與 API

### 任務 8：Web 骨架、Cookie 驗證、全域例外處理與種子資料

**文件：**
- 創建：`src/OfficeCal.Services/IUserContext.cs`
- 創建：`src/OfficeCal.Services/IPasswordService.cs` + `PasswordService.cs`
- 創建：`src/OfficeCal.Core/Dtos/AuthDtos.cs`
- 創建：`src/OfficeCal.Infrastructure/Repositories/IUserRepository.cs` + `UserRepository.cs`
- 創建：`src/OfficeCal.Infrastructure/DbSeeder.cs`
- 創建：`src/OfficeCal.Web/Infrastructure/HttpUserContext.cs`
- 創建：`src/OfficeCal.Web/Middleware/GlobalExceptionMiddleware.cs`
- 創建：`src/OfficeCal.Web/Controllers/AuthController.cs`
- 創建：`src/OfficeCal.Web/Controllers/MeController.cs`
- 修改：`src/OfficeCal.Web/Program.cs`（全文替換）
- 修改：`src/OfficeCal.Web/appsettings.json`
- 測試：`tests/OfficeCal.Tests/Fixtures/ApiFactory.cs`
- 測試：`tests/OfficeCal.Tests/Unit/GlobalExceptionMiddlewareTests.cs`
- 測試：`tests/OfficeCal.Tests/Integration/AuthApiTests.cs`

**決策：登入失敗回 400 而非 401。** 401 專門代表「未登入」，前端 Axios 攔截器收到 401 會導向登入頁；若登入端點自己回 401 會造成登入頁自我跳轉。登入失敗屬於輸入錯誤，走 `ValidationException` → 400。

- [ ] **步驟 1：編寫失敗的例外中介軟體單元測試**

`tests/OfficeCal.Tests/Unit/GlobalExceptionMiddlewareTests.cs`：

```csharp
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
```

- [ ] **步驟 2：運行測試驗證失敗**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~GlobalExceptionMiddlewareTests"
```

預期：編譯失敗，`GlobalExceptionMiddleware` 不存在。

- [ ] **步驟 3：實作中介軟體**

`src/OfficeCal.Web/Middleware/GlobalExceptionMiddleware.cs`：

```csharp
using System.Text.Json;
using OfficeCal.Core.Common;
using OfficeCal.Core.Exceptions;

namespace OfficeCal.Web.Middleware;

/// <summary>
/// 領域例外 → HTTP 狀態碼 + 統一回傳信封。Controller 內一律不寫 try/catch（規格 9）。
/// </summary>
public class GlobalExceptionMiddleware
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
        => (_next, _logger) = (next, logger);

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await _next(ctx);
        }
        catch (Exception ex)
        {
            if (ctx.Response.HasStarted) throw;

            var (status, payload) = Map(ex);

            if (status == StatusCodes.Status500InternalServerError)
                _logger.LogError(ex, "未預期例外：{Path}", ctx.Request.Path);

            ctx.Response.Clear();
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(payload, Json));
        }
    }

    private static (int, ApiResponse<object?>) Map(Exception ex) => ex switch
    {
        ValidationException v => (StatusCodes.Status400BadRequest,
            ApiResponse.Fail(v.Message, v.Errors)),

        NotFoundException n => (StatusCodes.Status404NotFound,
            ApiResponse.Fail(n.Message)),

        ForbiddenException f => (StatusCodes.Status403Forbidden,
            ApiResponse.Fail(f.Message)),

        ConflictException c => (StatusCodes.Status409Conflict,
            ApiResponse.Fail(c.Message, null, new { conflicts = c.Conflicts })),

        _ => (StatusCodes.Status500InternalServerError,
            ApiResponse.Fail("系統發生未預期的錯誤，請稍後再試")),
    };
}
```

- [ ] **步驟 4：寫使用者情境、密碼服務、使用者 Repository 與 DTO**

`src/OfficeCal.Services/IUserContext.cs`：

```csharp
namespace OfficeCal.Services;

/// <summary>Service 層取得目前登入者的唯一管道，避免直接依賴 HttpContext。</summary>
public interface IUserContext
{
    bool IsAuthenticated { get; }
    /// <summary>未登入時存取會丟 InvalidOperationException。</summary>
    int UserId { get; }
    string DisplayName { get; }
    bool IsAdmin { get; }
}
```

`src/OfficeCal.Services/IPasswordService.cs`：

```csharp
using Microsoft.AspNetCore.Identity;
using OfficeCal.Core.Entities;

namespace OfficeCal.Services;

public interface IPasswordService
{
    string Hash(User user, string plainPassword);
    bool Verify(User user, string plainPassword);
    /// <summary>產生訂閱 feed 的隨機 token（URL 安全，43 字元）。</summary>
    string NewFeedToken();
}

public class PasswordService : IPasswordService
{
    // 規格 6.2：不引入完整 ASP.NET Core Identity，只借用它的密碼雜湊器。
    private readonly PasswordHasher<User> _hasher = new();

    public string Hash(User user, string plainPassword) => _hasher.HashPassword(user, plainPassword);

    public bool Verify(User user, string plainPassword)
        => _hasher.VerifyHashedPassword(user, user.PasswordHash, plainPassword)
           != PasswordVerificationResult.Failed;

    public string NewFeedToken()
        => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                  .Replace("+", "-").Replace("/", "_").TrimEnd('=');
}
```

`src/OfficeCal.Infrastructure/Repositories/IUserRepository.cs`：

```csharp
using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure.Repositories;

public interface IUserRepository
{
    Task<User?> GetByEmployeeNoAsync(string employeeNo, CancellationToken ct = default);
    Task<User?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<User?> GetByFeedTokenAsync(string token, CancellationToken ct = default);
    Task<List<User>> ListAsync(CancellationToken ct = default);
    Task<List<User>> GetByIdsAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default);
    void Add(User user);
}
```

`src/OfficeCal.Infrastructure/Repositories/UserRepository.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure.Repositories;

public class UserRepository : IUserRepository
{
    private readonly OfficeCalDbContext _db;
    public UserRepository(OfficeCalDbContext db) => _db = db;

    public Task<User?> GetByEmployeeNoAsync(string employeeNo, CancellationToken ct = default)
        => _db.Users.FirstOrDefaultAsync(u => u.EmployeeNo == employeeNo, ct);

    public Task<User?> GetByIdAsync(int id, CancellationToken ct = default)
        => _db.Users.Include(u => u.Department).FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByFeedTokenAsync(string token, CancellationToken ct = default)
        => _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.IcsFeedToken == token && u.IsActive, ct);

    public Task<List<User>> ListAsync(CancellationToken ct = default)
        => _db.Users.AsNoTracking().Include(u => u.Department)
                    .OrderBy(u => u.EmployeeNo).ToListAsync(ct);

    public Task<List<User>> GetByIdsAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default)
        => _db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(ct);

    public void Add(User user) => _db.Users.Add(user);
}
```

`src/OfficeCal.Core/Dtos/AuthDtos.cs`：

```csharp
using System.ComponentModel.DataAnnotations;

namespace OfficeCal.Core.Dtos;

public class LoginRequest
{
    [Required(ErrorMessage = "請輸入員工編號")]
    [StringLength(20)]
    public string EmployeeNo { get; set; } = "";

    [Required(ErrorMessage = "請輸入密碼")]
    [StringLength(100)]
    public string Password { get; set; } = "";
}

public class MeDto
{
    public int Id { get; set; }
    public string EmployeeNo { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public string? DepartmentName { get; set; }
    public string Role { get; set; } = "";
    public bool IsAdmin { get; set; }
    /// <summary>個人訂閱 feed 的完整網址，供個人設定頁顯示與複製。</summary>
    public string FeedUrl { get; set; } = "";
}

public class ChangePasswordRequest
{
    [Required] public string CurrentPassword { get; set; } = "";
    [Required][StringLength(100, MinimumLength = 8, ErrorMessage = "新密碼至少 8 個字元")]
    public string NewPassword { get; set; } = "";
}
```

- [ ] **步驟 5：寫種子資料**

`src/OfficeCal.Infrastructure/DbSeeder.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;

namespace OfficeCal.Infrastructure;

public static class DbSeeder
{
    public const string AdminEmployeeNo = "A0001";
    public const string AdminInitialPassword = "Admin@12345";

    /// <summary>可重複執行。hashPassword 由呼叫端提供，避免 Infrastructure 依賴 Services。</summary>
    public static async Task SeedAsync(OfficeCalDbContext db, Func<User, string, string> hashPassword,
                                       Func<string> newFeedToken)
    {
        await db.Database.MigrateAsync();

        if (!await db.Departments.AnyAsync())
        {
            db.Departments.AddRange(
                new Department { Name = "資訊部" },
                new Department { Name = "業務部" },
                new Department { Name = "管理部" });
            await db.SaveChangesAsync();
        }

        if (!await db.Users.AnyAsync(u => u.EmployeeNo == AdminEmployeeNo))
        {
            var it = await db.Departments.FirstAsync(d => d.Name == "資訊部");
            var admin = new User
            {
                EmployeeNo = AdminEmployeeNo,
                DisplayName = "系統管理員",
                Email = "admin@corp.local",
                DepartmentId = it.Id,
                Role = UserRole.Admin,
                IcsFeedToken = newFeedToken(),
                IsActive = true,
            };
            admin.PasswordHash = hashPassword(admin, AdminInitialPassword);
            db.Users.Add(admin);
            await db.SaveChangesAsync();
        }

        if (!await db.Rooms.AnyAsync())
        {
            db.Rooms.AddRange(
                new Room { Name = "A 棟 3F 大會議廳", Location = "A 棟 3 樓", Capacity = 40,
                           Equipment = "投影機、視訊設備、白板" },
                new Room { Name = "A 棟 3F 小會議室", Location = "A 棟 3 樓", Capacity = 8,
                           Equipment = "電視螢幕" },
                new Room { Name = "B 棟 2F 討論室", Location = "B 棟 2 樓", Capacity = 6,
                           Equipment = "白板" });
            await db.SaveChangesAsync();
        }
    }
}
```

- [ ] **步驟 6：寫 `HttpUserContext` 與 `Program.cs`**

`src/OfficeCal.Web/Infrastructure/HttpUserContext.cs`：

```csharp
using System.Security.Claims;
using OfficeCal.Core.Enums;
using OfficeCal.Services;

namespace OfficeCal.Web.Infrastructure;

public class HttpUserContext : IUserContext
{
    private readonly IHttpContextAccessor _accessor;
    public HttpUserContext(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public int UserId => int.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id
        : throw new InvalidOperationException("目前沒有登入的使用者");

    public string DisplayName => Principal?.FindFirstValue(ClaimTypes.Name) ?? "";

    public bool IsAdmin => Principal?.IsInRole(nameof(UserRole.Admin)) == true;
}
```

`src/OfficeCal.Web/Program.cs`（全文替換）：

```csharp
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
```

`src/OfficeCal.Web/appsettings.json`：

```json
{
  "ConnectionStrings": {
    "Default": "Server=(localdb)\\MSSQLLocalDB;Database=OfficeCal;Integrated Security=true;MultipleActiveResultSets=true;TrustServerCertificate=true"
  },
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" }
  },
  "AllowedHosts": "*"
}
```

- [ ] **步驟 7：寫 `AuthController` 與 `MeController`**

`src/OfficeCal.Web/Controllers/AuthController.cs`：

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure.Repositories;
using OfficeCal.Services;

namespace OfficeCal.Web.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly IUserRepository _users;
    private readonly IPasswordService _passwords;

    public AuthController(IUserRepository users, IPasswordService passwords)
        => (_users, _passwords) = (users, passwords);

    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<MeDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> LoginAsync([FromBody] LoginRequest req, CancellationToken ct)
    {
        var user = await _users.GetByEmployeeNoAsync(req.EmployeeNo.Trim(), ct);

        if (user is null || !user.IsActive || !_passwords.Verify(user, req.Password))
            throw new ValidationException("員工編號或密碼錯誤");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Role.ToString()),
        };

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims,
                CookieAuthenticationDefaults.AuthenticationScheme)));

        return Ok(ApiResponse.Ok(MeController.ToDto(user, Request), "登入成功"));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> LogoutAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(ApiResponse.Ok("已登出"));
    }
}
```

`src/OfficeCal.Web/Controllers/MeController.cs`（任務 14 會再加入改密碼與重設 token）：

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure.Repositories;
using OfficeCal.Services;

namespace OfficeCal.Web.Controllers;

[ApiController]
[Route("api/v1/me")]
[Authorize]
public class MeController : ControllerBase
{
    private readonly IUserRepository _users;
    private readonly IUserContext _me;

    public MeController(IUserRepository users, IUserContext me) => (_users, _me) = (users, me);

    [HttpGet("")]
    [ProducesResponseType(typeof(ApiResponse<MeDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(_me.UserId, ct)
                   ?? throw new NotFoundException("找不到使用者");
        return Ok(ApiResponse.Ok(ToDto(user, Request)));
    }

    public static MeDto ToDto(User user, HttpRequest request) => new()
    {
        Id = user.Id,
        EmployeeNo = user.EmployeeNo,
        DisplayName = user.DisplayName,
        Email = user.Email,
        DepartmentName = user.Department?.Name,
        Role = user.Role.ToString(),
        IsAdmin = user.Role == UserRole.Admin,
        FeedUrl = $"{request.Scheme}://{request.Host}/feeds/{user.IcsFeedToken}.ics",
    };
}
```

- [ ] **步驟 8：寫 API 測試工廠與登入測試**

`tests/OfficeCal.Tests/Fixtures/ApiFactory.cs`：

```csharp
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
```

`tests/OfficeCal.Tests/Integration/AuthApiTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Infrastructure;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("Api")]
public class AuthApiTests
{
    private readonly ApiFactory _api;
    public AuthApiTests(ApiFactory api) => _api = api;

    [Fact]
    public async Task 正確帳密可登入並取得個人資料()
    {
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);

        var res = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<MeDto>>(ApiFactory.Json);
        Assert.True(body!.Success);
        Assert.Equal(DbSeeder.AdminEmployeeNo, body.Data!.EmployeeNo);
        Assert.True(body.Data.IsAdmin);
        Assert.Contains("/feeds/", body.Data.FeedUrl);
        Assert.EndsWith(".ics", body.Data.FeedUrl);
    }

    [Fact]
    public async Task 密碼錯誤回四百且信封標示失敗()
    {
        var client = _api.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest { EmployeeNo = DbSeeder.AdminEmployeeNo, Password = "wrong-password" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>(ApiFactory.Json);
        Assert.False(body!.Success);
        Assert.Equal("員工編號或密碼錯誤", body.Message);
    }

    [Fact]
    public async Task 未登入呼叫受保護端點回四百零一()
    {
        var client = _api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing
            .WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var res = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task 停用帳號不能登入()
    {
        await using (var db = _api.CreateContext())
        {
            var u = await db.Users.FirstAsync(x => x.EmployeeNo == DbSeeder.AdminEmployeeNo);
            u.IsActive = false;
            await db.SaveChangesAsync();
        }

        var client = _api.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest
            {
                EmployeeNo = DbSeeder.AdminEmployeeNo,
                Password = DbSeeder.AdminInitialPassword,
            });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        await using (var db = _api.CreateContext())
        {
            var u = await db.Users.FirstAsync(x => x.EmployeeNo == DbSeeder.AdminEmployeeNo);
            u.IsActive = true;   // 還原，避免影響同集合的其他測試
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task 登出後Cookie失效()
    {
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        var logout = await client.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        var after = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }
}
```

- [ ] **步驟 9：運行測試驗證通過**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~AuthApiTests|FullyQualifiedName~GlobalExceptionMiddlewareTests"
```

預期：Passed! 10 passed。

- [ ] **步驟 10：Commit**

```bash
git add -A
git commit -m "feat: 新增 Cookie 驗證、全域例外處理、統一回傳信封與種子資料"
```

---

### 任務 9：`NotificationService` 與通知 API

規格 5.5。**通知訊息在產生當下就寫成完整句子**，之後渲染不再依賴可能已變動的事件資料。

**文件：**
- 創建：`src/OfficeCal.Core/Dtos/NotificationDtos.cs`
- 創建：`src/OfficeCal.Infrastructure/Repositories/INotificationRepository.cs` + `NotificationRepository.cs`
- 創建：`src/OfficeCal.Services/INotificationService.cs` + `NotificationService.cs`
- 創建：`src/OfficeCal.Web/Controllers/NotificationsController.cs`
- 修改：`src/OfficeCal.Web/Program.cs`（註冊 `INotificationRepository`、`INotificationService`）
- 測試：`tests/OfficeCal.Tests/Integration/NotificationServiceTests.cs`

- [ ] **步驟 1：編寫失敗的測試**

`tests/OfficeCal.Tests/Integration/NotificationServiceTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;
using OfficeCal.Services;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("LocalDb")]
public class NotificationServiceTests
{
    private readonly LocalDbFixture _db;
    public NotificationServiceTests(LocalDbFixture db) => _db = db;

    private static readonly DateTime Now = new(2026, 9, 1, 9, 0, 0);

    private static NotificationService NewService(OfficeCalDbContext ctx)
        => new(ctx, new NotificationRepository(ctx), new FixedTimeProvider(Now));

    [Fact]
    public async Task 建立事件通知全體與會者但不含擁有者()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var a = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var b = await TestData.AddUserAsync(ctx, "E003", "李小華");
        var ev = await TestData.AddBookedEventAsync(ctx, owner, null,
            new DateTime(2026, 9, 7, 10, 0, 0), new DateTime(2026, 9, 7, 11, 0, 0), "週一產品例會");

        await NewService(ctx).AddedToEventAsync(ev, new DateTime(2026, 9, 7, 10, 0, 0),
                                                new[] { owner.Id, a.Id, b.Id }, "陳大明");

        await using var verify = _db.CreateContext();
        var all = await verify.Notifications.ToListAsync();

        Assert.Equal(2, all.Count);
        Assert.DoesNotContain(all, n => n.UserId == owner.Id);
        Assert.All(all, n => Assert.Equal(NotificationType.AddedToEvent, n.Type));
        Assert.All(all, n => Assert.Contains("週一產品例會", n.Message));
        Assert.All(all, n => Assert.Contains("9/7 10:00", n.Message));
    }

    [Fact]
    public async Task 單筆改期的通知標明是哪一次發生()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var a = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var ev = await TestData.AddBookedEventAsync(ctx, owner, null,
            new DateTime(2026, 9, 7, 10, 0, 0), new DateTime(2026, 9, 7, 11, 0, 0), "週一產品例會");

        await NewService(ctx).EventUpdatedAsync(ev, new[] { a.Id },
            occurrenceOriginalStart: new DateTime(2026, 9, 14, 10, 0, 0),
            newStart: new DateTime(2026, 9, 14, 14, 0, 0),
            roomName: null);

        await using var verify = _db.CreateContext();
        var n = await verify.Notifications.SingleAsync();

        Assert.Equal(NotificationType.EventUpdated, n.Type);
        Assert.Contains("9/14", n.Message);
        Assert.Contains("週一產品例會", n.Message);
        Assert.Contains("14:00", n.Message);
    }

    [Fact]
    public async Task 強制取消同時通知擁有者與與會者()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var a = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var ev = await TestData.AddBookedEventAsync(ctx, owner, null,
            new DateTime(2026, 9, 7, 10, 0, 0), new DateTime(2026, 9, 7, 11, 0, 0), "季度檢討會");

        await NewService(ctx).ForcedCancellationAsync(ev, new[] { owner.Id, a.Id },
                                                      occurrenceStart: null, adminName: "系統管理員");

        await using var verify = _db.CreateContext();
        var all = await verify.Notifications.ToListAsync();

        Assert.Equal(2, all.Count);
        Assert.All(all, n => Assert.Equal(NotificationType.ForcedCancellation, n.Type));
        Assert.All(all, n => Assert.Contains("系統管理員", n.Message));
    }

    [Fact]
    public async Task 清單可只取未讀且標記已讀只有收件者本人可以做()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var a = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var b = await TestData.AddUserAsync(ctx, "E003", "李小華");
        var ev = await TestData.AddBookedEventAsync(ctx, owner, null,
            new DateTime(2026, 9, 7, 10, 0, 0), new DateTime(2026, 9, 7, 11, 0, 0));

        var svc = NewService(ctx);
        await svc.AddedToEventAsync(ev, new DateTime(2026, 9, 7, 10, 0, 0),
                                    new[] { a.Id }, "陳大明");

        var list = await svc.ListAsync(a.Id, unreadOnly: true, take: 20);
        Assert.Single(list);

        await Assert.ThrowsAsync<ForbiddenException>(() => svc.MarkReadAsync(list[0].Id, b.Id));

        await svc.MarkReadAsync(list[0].Id, a.Id);
        Assert.Empty(await svc.ListAsync(a.Id, unreadOnly: true, take: 20));
        Assert.Single(await svc.ListAsync(a.Id, unreadOnly: false, take: 20));
        Assert.Equal(0, await svc.UnreadCountAsync(a.Id));
    }
}
```

- [ ] **步驟 2：運行測試驗證失敗**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~NotificationServiceTests"
```

預期：編譯失敗，`NotificationService` 不存在。

- [ ] **步驟 3：寫 DTO 與 Repository**

`src/OfficeCal.Core/Dtos/NotificationDtos.cs`：

```csharp
namespace OfficeCal.Core.Dtos;

public class NotificationDto
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public int? EventId { get; set; }
    public string Message { get; set; } = "";
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

`src/OfficeCal.Infrastructure/Repositories/INotificationRepository.cs`：

```csharp
using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure.Repositories;

public interface INotificationRepository
{
    void AddRange(IEnumerable<Notification> notifications);
    Task<List<Notification>> ListAsync(int userId, bool unreadOnly, int take, CancellationToken ct = default);
    Task<int> UnreadCountAsync(int userId, CancellationToken ct = default);
    Task<Notification?> GetTrackedByIdAsync(int id, CancellationToken ct = default);
}
```

`src/OfficeCal.Infrastructure/Repositories/NotificationRepository.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure.Repositories;

public class NotificationRepository : INotificationRepository
{
    private readonly OfficeCalDbContext _db;
    public NotificationRepository(OfficeCalDbContext db) => _db = db;

    public void AddRange(IEnumerable<Notification> notifications)
        => _db.Notifications.AddRange(notifications);

    public Task<List<Notification>> ListAsync(int userId, bool unreadOnly, int take,
                                              CancellationToken ct = default)
        => _db.Notifications.AsNoTracking()
              .Where(n => n.UserId == userId && (!unreadOnly || !n.IsRead))
              .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
              .Take(take)
              .ToListAsync(ct);

    public Task<int> UnreadCountAsync(int userId, CancellationToken ct = default)
        => _db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead, ct);

    public Task<Notification?> GetTrackedByIdAsync(int id, CancellationToken ct = default)
        => _db.Notifications.FirstOrDefaultAsync(n => n.Id == id, ct);
}
```

- [ ] **步驟 4：實作 `INotificationService` 與 `NotificationService`**

`src/OfficeCal.Services/INotificationService.cs`：

```csharp
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;

namespace OfficeCal.Services;

/// <summary>
/// 站內通知。寫入方法都在呼叫端（EventService）的交易內執行，
/// 與 Event、EventOccurrence 的寫入是同一個原子操作。
/// 訊息在產生當下就寫成完整句子（規格 5.5）。
/// </summary>
public interface INotificationService
{
    /// <summary>建立事件並指定與會者。userIds 中若含擁有者會自動略過。</summary>
    Task AddedToEventAsync(Event ev, DateTime firstStart, IReadOnlyCollection<int> userIds,
                           string ownerName, CancellationToken ct = default);

    /// <summary>
    /// 編輯事件的時間或會議廳。
    /// occurrenceOriginalStart 非 null 表示 mode=single，訊息會標明是哪一次發生。
    /// roomName 非 null 表示會議廳有變更。
    /// </summary>
    Task EventUpdatedAsync(Event ev, IReadOnlyCollection<int> userIds,
                           DateTime? occurrenceOriginalStart, DateTime newStart, string? roomName,
                           CancellationToken ct = default);

    /// <summary>取消事件。occurrenceStart 非 null 表示 mode=single。</summary>
    Task EventCancelledAsync(Event ev, IReadOnlyCollection<int> userIds, DateTime? occurrenceStart,
                             CancellationToken ct = default);

    /// <summary>管理員強制取消他人預約。</summary>
    Task ForcedCancellationAsync(Event ev, IReadOnlyCollection<int> userIds, DateTime? occurrenceStart,
                                 string adminName, CancellationToken ct = default);

    Task<List<NotificationDto>> ListAsync(int userId, bool unreadOnly, int take,
                                          CancellationToken ct = default);
    Task<int> UnreadCountAsync(int userId, CancellationToken ct = default);

    /// <summary>只有收件者本人可標記已讀，否則丟 ForbiddenException。</summary>
    Task MarkReadAsync(int notificationId, int userId, CancellationToken ct = default);
}
```

`src/OfficeCal.Services/NotificationService.cs`：

```csharp
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;

namespace OfficeCal.Services;

public class NotificationService : INotificationService
{
    private readonly OfficeCalDbContext _db;
    private readonly INotificationRepository _repo;
    private readonly TimeProvider _clock;

    public NotificationService(OfficeCalDbContext db, INotificationRepository repo, TimeProvider clock)
        => (_db, _repo, _clock) = (db, repo, clock);

    /// <summary>訊息中的日期時間格式，例如 9/14 14:00。</summary>
    private static string F(DateTime d) => $"{d.Month}/{d.Day} {d:HH:mm}";
    private static string D(DateTime d) => $"{d.Month}/{d.Day}";

    public Task AddedToEventAsync(Event ev, DateTime firstStart, IReadOnlyCollection<int> userIds,
                                  string ownerName, CancellationToken ct = default)
        => WriteAsync(userIds.Where(id => id != ev.OwnerId), NotificationType.AddedToEvent, ev.Id,
                      $"{ownerName} 邀請你參加 {F(firstStart)} 的「{ev.Title}」", ct);

    public Task EventUpdatedAsync(Event ev, IReadOnlyCollection<int> userIds,
                                  DateTime? occurrenceOriginalStart, DateTime newStart,
                                  string? roomName, CancellationToken ct = default)
    {
        var prefix = occurrenceOriginalStart is DateTime o ? $"{D(o)} 的" : "";
        var message = roomName is null
            ? $"{prefix}「{ev.Title}」已改期至 {F(newStart)}"
            : $"{prefix}「{ev.Title}」已改至 {F(newStart)}，會議廳改為「{roomName}」";
        return WriteAsync(userIds, NotificationType.EventUpdated, ev.Id, message, ct);
    }

    public Task EventCancelledAsync(Event ev, IReadOnlyCollection<int> userIds,
                                    DateTime? occurrenceStart, CancellationToken ct = default)
    {
        var message = occurrenceStart is DateTime o
            ? $"{D(o)} 的「{ev.Title}」已取消"
            : $"「{ev.Title}」整個系列已取消";
        return WriteAsync(userIds, NotificationType.EventCancelled, ev.Id, message, ct);
    }

    public Task ForcedCancellationAsync(Event ev, IReadOnlyCollection<int> userIds,
                                        DateTime? occurrenceStart, string adminName,
                                        CancellationToken ct = default)
    {
        var what = occurrenceStart is DateTime o ? $"{F(o)} 的「{ev.Title}」" : $"「{ev.Title}」整個系列";
        return WriteAsync(userIds, NotificationType.ForcedCancellation, ev.Id,
                          $"{adminName} 已強制取消 {what}，該時段的會議廳已釋出", ct);
    }

    private async Task WriteAsync(IEnumerable<int> userIds, NotificationType type, int? eventId,
                                  string message, CancellationToken ct)
    {
        var now = TaipeiTime.Now(_clock);
        var rows = userIds.Distinct()
            .Select(id => new Notification
            {
                UserId = id,
                Type = type,
                EventId = eventId,
                Message = message.Length > 300 ? message[..300] : message,
                IsRead = false,
                CreatedAt = now,
            })
            .ToList();

        if (rows.Count == 0) return;

        _repo.AddRange(rows);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<NotificationDto>> ListAsync(int userId, bool unreadOnly, int take,
                                                       CancellationToken ct = default)
        => (await _repo.ListAsync(userId, unreadOnly, take, ct))
            .Select(n => new NotificationDto
            {
                Id = n.Id,
                Type = n.Type.ToString(),
                EventId = n.EventId,
                Message = n.Message,
                IsRead = n.IsRead,
                CreatedAt = n.CreatedAt,
            })
            .ToList();

    public Task<int> UnreadCountAsync(int userId, CancellationToken ct = default)
        => _repo.UnreadCountAsync(userId, ct);

    public async Task MarkReadAsync(int notificationId, int userId, CancellationToken ct = default)
    {
        var n = await _repo.GetTrackedByIdAsync(notificationId, ct)
                ?? throw new NotFoundException("找不到通知");

        if (n.UserId != userId) throw new ForbiddenException("只能標記自己的通知");

        n.IsRead = true;
        await _db.SaveChangesAsync(ct);
    }
}
```

- [ ] **步驟 5：寫 `NotificationsController` 並註冊 DI**

`src/OfficeCal.Web/Controllers/NotificationsController.cs`：

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Services;

namespace OfficeCal.Web.Controllers;

[ApiController]
[Route("api/v1/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notifications;
    private readonly IUserContext _me;

    public NotificationsController(INotificationService notifications, IUserContext me)
        => (_notifications, _me) = (notifications, me);

    [HttpGet("")]
    [ProducesResponseType(typeof(ApiResponse<NotificationListDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAsync([FromQuery] bool unreadOnly = false,
                                               [FromQuery] int take = 30,
                                               CancellationToken ct = default)
    {
        var items = await _notifications.ListAsync(_me.UserId, unreadOnly, Math.Clamp(take, 1, 100), ct);
        var unread = await _notifications.UnreadCountAsync(_me.UserId, ct);
        return Ok(ApiResponse.Ok(new NotificationListDto { Items = items, UnreadCount = unread }));
    }

    [HttpPost("{id:int}/read")]
    public async Task<IActionResult> MarkReadAsync(int id, CancellationToken ct)
    {
        await _notifications.MarkReadAsync(id, _me.UserId, ct);
        return Ok(ApiResponse.Ok("已標記為已讀"));
    }
}
```

在 `src/OfficeCal.Core/Dtos/NotificationDtos.cs` 補上：

```csharp
public class NotificationListDto
{
    public List<NotificationDto> Items { get; set; } = new();
    public int UnreadCount { get; set; }
}
```

在 `Program.cs` 的服務註冊區塊補上：

```csharp
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
builder.Services.AddScoped<INotificationService, NotificationService>();
```

- [ ] **步驟 6：運行測試驗證通過**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~NotificationServiceTests"
```

預期：Passed! 4 passed。

- [ ] **步驟 7：Commit**

```bash
git add -A
git commit -m "feat: 新增站內通知服務與通知 API"
```

---

### 任務 10：`EventService` —— 建立、查詢、編輯、取消與權限

規格 5.4、5.5、7.3、7.4。**`EventService` 是唯一開啟交易的地方（D2）。**

**文件：**
- 創建：`src/OfficeCal.Core/Dtos/EventDtos.cs`
- 創建：`src/OfficeCal.Infrastructure/Repositories/IEventRepository.cs` + `EventRepository.cs`
- 創建：`src/OfficeCal.Services/IEventService.cs` + `EventService.cs`
- 修改：`src/OfficeCal.Web/Program.cs`（註冊 `IEventRepository`、`IEventService`）
- 測試：`tests/OfficeCal.Tests/Fixtures/FakeUserContext.cs`
- 測試：`tests/OfficeCal.Tests/Integration/EventServiceTests.cs`

- [ ] **步驟 1：寫 DTO**

`src/OfficeCal.Core/Dtos/EventDtos.cs`：

```csharp
using System.ComponentModel.DataAnnotations;

namespace OfficeCal.Core.Dtos;

public class CreateEventRequest
{
    [Required(ErrorMessage = "請輸入標題")]
    [StringLength(100, ErrorMessage = "標題最多 100 字")]
    public string Title { get; set; } = "";

    [StringLength(1000, ErrorMessage = "說明最多 1000 字")]
    public string? Description { get; set; }

    /// <summary>null 表示純個人事件，不占用資源。</summary>
    public int? RoomId { get; set; }

    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public bool IsAllDay { get; set; }
    public List<int> AttendeeIds { get; set; } = new();

    /// <summary>null 表示單次事件。</summary>
    public RecurrencePatternDto? Recurrence { get; set; }
}

public class UpdateEventRequest : CreateEventRequest
{
    /// <summary>mode=single 時必填。</summary>
    public int? OccurrenceId { get; set; }
}

/// <summary>行事曆格子上的一筆。所有檢視都只讀 occurrence。</summary>
public class OccurrenceDto
{
    public int OccurrenceId { get; set; }
    public int EventId { get; set; }
    public string Title { get; set; } = "";
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public bool IsAllDay { get; set; }
    public int? RoomId { get; set; }
    public string? RoomName { get; set; }
    public int OwnerId { get; set; }
    public string OwnerName { get; set; } = "";
    public bool IsRecurring { get; set; }
    public bool IsModified { get; set; }
    public bool CanEdit { get; set; }
}

public class AttendeeDto
{
    public int UserId { get; set; }
    public string DisplayName { get; set; } = "";
    public string? DepartmentName { get; set; }
}

public class EventDetailDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public int? RoomId { get; set; }
    public string? RoomName { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public bool IsAllDay { get; set; }
    public string Status { get; set; } = "";
    public int OwnerId { get; set; }
    public string OwnerName { get; set; } = "";
    public RecurrencePatternDto? Recurrence { get; set; }
    public List<AttendeeDto> Attendees { get; set; } = new();
    public bool CanEdit { get; set; }
}

public class TimeSlotDto
{
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
}

public class AttendeeConflictRequest
{
    public List<int> AttendeeIds { get; set; } = new();
    public List<TimeSlotDto> Slots { get; set; } = new();
}

public class AttendeeConflictDto
{
    public int UserId { get; set; }
    public string DisplayName { get; set; } = "";
    public int ConflictCount { get; set; }
    public List<string> Titles { get; set; } = new();
}
```

- [ ] **步驟 2：寫 `IEventRepository`**

`src/OfficeCal.Infrastructure/Repositories/IEventRepository.cs`：

```csharp
using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure.Repositories;

public interface IEventRepository
{
    void Add(Event ev);
    /// <summary>受追蹤，含 Attendees，供編輯使用。</summary>
    Task<Event?> GetTrackedWithAttendeesAsync(int id, CancellationToken ct = default);
    /// <summary>唯讀，含 Owner、Room、Attendees.User，供明細使用。</summary>
    Task<Event?> GetDetailAsync(int id, CancellationToken ct = default);
}
```

`src/OfficeCal.Infrastructure/Repositories/EventRepository.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure.Repositories;

public class EventRepository : IEventRepository
{
    private readonly OfficeCalDbContext _db;
    public EventRepository(OfficeCalDbContext db) => _db = db;

    public void Add(Event ev) => _db.Events.Add(ev);

    public Task<Event?> GetTrackedWithAttendeesAsync(int id, CancellationToken ct = default)
        => _db.Events.Include(e => e.Attendees).FirstOrDefaultAsync(e => e.Id == id, ct);

    public Task<Event?> GetDetailAsync(int id, CancellationToken ct = default)
        => _db.Events.AsNoTracking()
              .Include(e => e.Owner)
              .Include(e => e.Room)
              .Include(e => e.Attendees).ThenInclude(a => a.User!).ThenInclude(u => u.Department)
              .FirstOrDefaultAsync(e => e.Id == id, ct);
}
```

- [ ] **步驟 3：編寫失敗的測試**

`tests/OfficeCal.Tests/Fixtures/FakeUserContext.cs`：

```csharp
using OfficeCal.Services;

namespace OfficeCal.Tests.Fixtures;

public class FakeUserContext : IUserContext
{
    public bool IsAuthenticated { get; set; } = true;
    public int UserId { get; set; }
    public string DisplayName { get; set; } = "";
    public bool IsAdmin { get; set; }
}
```

`tests/OfficeCal.Tests/Integration/EventServiceTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;
using OfficeCal.Services;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("LocalDb")]
public class EventServiceTests
{
    private readonly LocalDbFixture _db;
    public EventServiceTests(LocalDbFixture db) => _db = db;

    /// <summary>2026-09-01 09:00，讓 9 月的所有測試時段都在未來。</summary>
    private static readonly DateTime Now = new(2026, 9, 1, 9, 0, 0);

    private static DateTime D(int day, int hour) => new(2026, 9, day, hour, 0, 0);

    private static (EventService svc, FakeUserContext me) NewService(
        OfficeCalDbContext ctx, User actingAs, bool isAdmin = false)
    {
        var clock = new FixedTimeProvider(Now);
        var me = new FakeUserContext
        {
            UserId = actingAs.Id, DisplayName = actingAs.DisplayName,
            IsAdmin = isAdmin || actingAs.Role == UserRole.Admin,
        };
        var occurrences = new EventOccurrenceRepository(ctx);
        var rooms = new RoomRepository(ctx);
        var booking = new BookingService(ctx, rooms, occurrences, clock);
        var notifications = new NotificationService(ctx, new NotificationRepository(ctx), clock);

        var svc = new EventService(ctx, new EventRepository(ctx), occurrences, rooms,
                                   new UserRepository(ctx), new RecurrenceService(), booking,
                                   notifications, me, clock);
        return (svc, me);
    }

    private static CreateEventRequest Req(string title, int? roomId, int day, int hour,
                                          params int[] attendeeIds) => new()
    {
        Title = title, RoomId = roomId,
        StartAt = D(day, hour), EndAt = D(day, hour + 1),
        AttendeeIds = attendeeIds.ToList(),
    };

    [Fact]
    public async Task 建立單次事件產生一筆Occurrence並通知與會者()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var guest = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var (svc, _) = NewService(ctx, owner);

        var id = await svc.CreateAsync(Req("專案啟動會議", room.Id, 7, 10, guest.Id));

        await using var verify = _db.CreateContext();
        Assert.Equal(1, await verify.EventOccurrences.CountAsync(o => o.EventId == id));
        Assert.Equal(1, await verify.EventAttendees.CountAsync(a => a.EventId == id));
        var n = await verify.Notifications.SingleAsync();
        Assert.Equal(guest.Id, n.UserId);
        Assert.Equal(NotificationType.AddedToEvent, n.Type);
    }

    [Fact]
    public async Task 建立重複事件產生多筆Occurrence()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var (svc, _) = NewService(ctx, owner);

        var req = Req("週一產品例會", room.Id, 7, 10);   // 2026-09-07 是週一
        req.Recurrence = new RecurrencePatternDto
        {
            Frequency = RecurrenceFrequency.Weekly, Interval = 1,
            ByWeekDays = new() { DayOfWeek.Monday },
            EndMode = RecurrenceEndMode.Count, Count = 4,
        };

        var id = await svc.CreateAsync(req);

        await using var verify = _db.CreateContext();
        Assert.Equal(4, await verify.EventOccurrences.CountAsync(o => o.EventId == id));
        Assert.Equal("FREQ=WEEKLY;INTERVAL=1;BYDAY=MO;COUNT=4",
                     (await verify.Events.FindAsync(id))!.RecurrenceRule);
    }

    [Fact]
    public async Task 衝突時整筆失敗且資料庫無任何寫入()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        await TestData.AddBookedEventAsync(ctx, owner, room, D(7, 10), D(7, 11), "季度檢討會");
        var (svc, _) = NewService(ctx, owner);

        await Assert.ThrowsAsync<ConflictException>(() => svc.CreateAsync(Req("撞期會議", room.Id, 7, 10)));

        await using var verify = _db.CreateContext();
        Assert.Equal(1, await verify.Events.CountAsync());
        Assert.Equal(1, await verify.EventOccurrences.CountAsync());
    }

    [Fact]
    public async Task 區間查詢的三種Scope()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var me = await TestData.AddUserAsync(ctx, "E001", "我");
        var other = await TestData.AddUserAsync(ctx, "E002", "別人");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");

        await TestData.AddBookedEventAsync(ctx, me, null, D(7, 9), D(7, 10), "我的私人事件");
        await TestData.AddBookedEventAsync(ctx, other, null, D(7, 15), D(7, 16), "別人的私人事件");
        await TestData.AddBookedEventAsync(ctx, other, room, D(7, 13), D(7, 14), "別人的會議室預約");

        var (svc, _) = NewService(ctx, me);
        var from = D(7, 0); var to = D(8, 0);

        var mine = await svc.GetRangeAsync(from, to, CalendarScope.Me, null);
        Assert.Single(mine);
        Assert.Equal("我的私人事件", mine[0].Title);

        var all = await svc.GetRangeAsync(from, to, CalendarScope.All, null);
        Assert.Single(all);
        Assert.Equal("別人的會議室預約", all[0].Title);

        var byRoom = await svc.GetRangeAsync(from, to, CalendarScope.Room, room.Id);
        Assert.Single(byRoom);

        await Assert.ThrowsAsync<ValidationException>(
            () => svc.GetRangeAsync(from, to, CalendarScope.Room, null));
    }

    [Fact]
    public async Task 純個人事件的明細他人看不到但掛會議廳的任何人可看()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var stranger = await TestData.AddUserAsync(ctx, "E002", "路人");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");

        var personal = await TestData.AddBookedEventAsync(ctx, owner, null, D(7, 9), D(7, 10), "私人");
        var booked = await TestData.AddBookedEventAsync(ctx, owner, room, D(7, 13), D(7, 14), "公開");

        var (svc, _) = NewService(ctx, stranger);

        await Assert.ThrowsAsync<ForbiddenException>(() => svc.GetDetailAsync(personal.Id));
        var detail = await svc.GetDetailAsync(booked.Id);
        Assert.Equal("公開", detail.Title);
        Assert.False(detail.CanEdit);
    }

    [Fact]
    public async Task 一般員工不能編輯他人事件()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var stranger = await TestData.AddUserAsync(ctx, "E002", "路人");
        var ev = await TestData.AddBookedEventAsync(ctx, owner, null, D(7, 9), D(7, 10));

        var (svc, _) = NewService(ctx, stranger);
        var req = new UpdateEventRequest
        {
            Title = "被亂改的標題", StartAt = D(7, 9), EndAt = D(7, 10),
        };

        await Assert.ThrowsAsync<ForbiddenException>(() => svc.UpdateAsync(ev.Id, EditMode.Series, req));
    }

    [Fact]
    public async Task 單筆編輯不可變更會議廳()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var roomA = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var roomB = await TestData.AddRoomAsync(ctx, "B 棟 2F 小會議室");
        var ev = await TestData.AddBookedEventAsync(ctx, owner, roomA, D(7, 10), D(7, 11));
        var occ = await ctx.EventOccurrences.FirstAsync(o => o.EventId == ev.Id);

        var (svc, _) = NewService(ctx, owner);
        var req = new UpdateEventRequest
        {
            Title = ev.Title, RoomId = roomB.Id, OccurrenceId = occ.Id,
            StartAt = D(7, 10), EndAt = D(7, 11),
        };

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => svc.UpdateAsync(ev.Id, EditMode.Single, req));
        Assert.Contains("會議廳", ex.Message);
    }

    [Fact]
    public async Task 管理員強制取消他人預約時擁有者與與會者都收到通知()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var guest = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var admin = await TestData.AddUserAsync(ctx, "A0001", "系統管理員", UserRole.Admin);
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var ev = await TestData.AddBookedEventAsync(ctx, owner, room, D(7, 10), D(7, 11), "季度檢討會");
        ctx.EventAttendees.Add(new EventAttendee { EventId = ev.Id, UserId = guest.Id });
        await ctx.SaveChangesAsync();

        var (svc, _) = NewService(ctx, admin);
        await svc.CancelAsync(ev.Id, EditMode.Series, null);

        await using var verify = _db.CreateContext();
        Assert.Equal(EventStatus.Cancelled, (await verify.Events.FindAsync(ev.Id))!.Status);
        Assert.True(await verify.EventOccurrences.Where(o => o.EventId == ev.Id).AllAsync(o => o.IsCancelled));

        var notes = await verify.Notifications.ToListAsync();
        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.Equal(NotificationType.ForcedCancellation, n.Type));
        Assert.Contains(notes, n => n.UserId == owner.Id);
        Assert.Contains(notes, n => n.UserId == guest.Id);
    }

    [Fact]
    public async Task 與會者衝突警示回傳每人的衝突次數與標題()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var me = await TestData.AddUserAsync(ctx, "E001", "我");
        var busy = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var free = await TestData.AddUserAsync(ctx, "E003", "李小華");

        await TestData.AddBookedEventAsync(ctx, busy, null, D(7, 10), D(7, 11), "客戶拜訪");
        await TestData.AddBookedEventAsync(ctx, busy, null, D(7, 10), D(7, 12), "教育訓練");
        await TestData.AddBookedEventAsync(ctx, busy, null, D(7, 12), D(7, 13), "頭尾相接不算");

        // 規格 7.4：「擁有或被邀請的」都算。這一筆是別人主辦、busy 被邀請。
        var invited = await TestData.AddBookedEventAsync(ctx, me, null, D(7, 10), D(7, 11),
                                                          "被邀請的部門會議");
        ctx.EventAttendees.Add(new EventAttendee { EventId = invited.Id, UserId = busy.Id });
        await ctx.SaveChangesAsync();

        var (svc, _) = NewService(ctx, me);
        var result = await svc.CheckAttendeesAsync(new AttendeeConflictRequest
        {
            AttendeeIds = new() { busy.Id, free.Id },
            Slots = new() { new TimeSlotDto { StartAt = D(7, 10), EndAt = D(7, 12) } },
        });

        var b = result.Single(r => r.UserId == busy.Id);
        Assert.Equal(3, b.ConflictCount);
        Assert.Contains("客戶拜訪", b.Titles);
        Assert.Contains("被邀請的部門會議", b.Titles);
        Assert.DoesNotContain("頭尾相接不算", b.Titles);
        Assert.Equal(0, result.Single(r => r.UserId == free.Id).ConflictCount);
    }
}
```

- [ ] **步驟 4：運行測試驗證失敗**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~EventServiceTests"
```

預期：編譯失敗，`EventService` 不存在。

- [ ] **步驟 5：實作 `IEventService`**

`src/OfficeCal.Services/IEventService.cs`：

```csharp
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Enums;

namespace OfficeCal.Services;

/// <summary>
/// 事件的業務規則與交易邊界。這是全系統唯一開啟／提交交易的地方（D2）。
/// </summary>
public interface IEventService
{
    Task<int> CreateAsync(CreateEventRequest req, CancellationToken ct = default);

    /// <summary>行事曆區間查詢。scope=Room 未附 roomId 時丟 ValidationException。</summary>
    Task<List<OccurrenceDto>> GetRangeAsync(DateTime from, DateTime to, CalendarScope scope,
                                            int? roomId, CancellationToken ct = default);

    Task<EventDetailDto> GetDetailAsync(int eventId, CancellationToken ct = default);

    Task UpdateAsync(int eventId, EditMode mode, UpdateEventRequest req, CancellationToken ct = default);

    Task CancelAsync(int eventId, EditMode mode, int? occurrenceId, CancellationToken ct = default);

    Task<List<AttendeeConflictDto>> CheckAttendeesAsync(AttendeeConflictRequest req,
                                                        CancellationToken ct = default);
}
```

- [ ] **步驟 6：實作 `EventService`**

`src/OfficeCal.Services/EventService.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;

namespace OfficeCal.Services;

public class EventService : IEventService
{
    private readonly OfficeCalDbContext _db;
    private readonly IEventRepository _events;
    private readonly IEventOccurrenceRepository _occurrences;
    private readonly IRoomRepository _rooms;
    private readonly IUserRepository _users;
    private readonly IRecurrenceService _recurrence;
    private readonly IBookingService _booking;
    private readonly INotificationService _notifications;
    private readonly IUserContext _me;
    private readonly TimeProvider _clock;

    // 相依項偏多是編排者的本質：本服務把重複展開、衝突鎖、通知三件事縫在同一個交易裡。
    public EventService(OfficeCalDbContext db, IEventRepository events,
                        IEventOccurrenceRepository occurrences, IRoomRepository rooms,
                        IUserRepository users, IRecurrenceService recurrence,
                        IBookingService booking, INotificationService notifications,
                        IUserContext me, TimeProvider clock)
    {
        _db = db; _events = events; _occurrences = occurrences; _rooms = rooms; _users = users;
        _recurrence = recurrence; _booking = booking; _notifications = notifications;
        _me = me; _clock = clock;
    }

    // ---------- 建立 ----------

    public async Task<int> CreateAsync(CreateEventRequest req, CancellationToken ct = default)
    {
        var (startAt, endAt) = Normalize(req.StartAt, req.EndAt, req.IsAllDay);
        var rrule = BuildRrule(req.Recurrence, startAt);
        var slots = _recurrence.Expand(rrule, startAt, endAt);
        var attendeeIds = await ValidateAttendeesAsync(req.AttendeeIds, ct);
        var now = TaipeiTime.Now(_clock);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var ev = new Event
        {
            Title = req.Title.Trim(),
            Description = req.Description,
            OwnerId = _me.UserId,
            RoomId = req.RoomId,
            StartAt = startAt,
            EndAt = endAt,
            IsAllDay = req.IsAllDay,
            RecurrenceRule = rrule,
            Status = EventStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _events.Add(ev);
        await _db.SaveChangesAsync(ct);

        foreach (var uid in attendeeIds)
            _db.EventAttendees.Add(new EventAttendee { EventId = ev.Id, UserId = uid });
        await _db.SaveChangesAsync(ct);

        await _booking.CreateOccurrencesAsync(ev, slots, ct);
        await _notifications.AddedToEventAsync(ev, slots[0].Start, attendeeIds, _me.DisplayName, ct);

        await tx.CommitAsync(ct);
        return ev.Id;
    }

    // ---------- 查詢 ----------

    public async Task<List<OccurrenceDto>> GetRangeAsync(DateTime from, DateTime to,
                                                          CalendarScope scope, int? roomId,
                                                          CancellationToken ct = default)
    {
        if (to <= from) throw new ValidationException("查詢區間的結束時間必須晚於開始時間");
        if ((to - from).TotalDays > 400) throw new ValidationException("查詢區間不可超過 400 天");

        var rows = scope switch
        {
            CalendarScope.Me => await _occurrences.GetRangeForUserAsync(_me.UserId, from, to, ct),
            CalendarScope.All => await _occurrences.GetRangeAllRoomsAsync(from, to, ct),
            CalendarScope.Room => await _occurrences.GetRangeForRoomAsync(
                roomId ?? throw new ValidationException("scope=room 必須指定 roomId"), from, to, ct),
            _ => throw new ValidationException("不支援的 scope"),
        };

        return rows.Select(ToOccurrenceDto).ToList();
    }

    public async Task<EventDetailDto> GetDetailAsync(int eventId, CancellationToken ct = default)
    {
        var ev = await _events.GetDetailAsync(eventId, ct) ?? throw new NotFoundException("找不到事件");

        var isAttendee = ev.Attendees.Any(a => a.UserId == _me.UserId);
        // 掛了會議廳的事件對所有已登入者可見（資源排程需要透明）；純個人事件僅擁有者與與會者可見。
        if (ev.OwnerId != _me.UserId && !isAttendee && ev.RoomId is null)
            throw new ForbiddenException("沒有權限查看此事件");

        return new EventDetailDto
        {
            Id = ev.Id,
            Title = ev.Title,
            Description = ev.Description,
            RoomId = ev.RoomId,
            RoomName = ev.Room?.Name,
            StartAt = ev.StartAt,
            EndAt = ev.EndAt,
            IsAllDay = ev.IsAllDay,
            Status = ev.Status.ToString(),
            OwnerId = ev.OwnerId,
            OwnerName = ev.Owner?.DisplayName ?? "",
            Recurrence = ev.RecurrenceRule is null ? null : _recurrence.ParseRrule(ev.RecurrenceRule),
            Attendees = ev.Attendees.Select(a => new AttendeeDto
            {
                UserId = a.UserId,
                DisplayName = a.User?.DisplayName ?? "",
                DepartmentName = a.User?.Department?.Name,
            }).OrderBy(a => a.DisplayName).ToList(),
            CanEdit = ev.OwnerId == _me.UserId || _me.IsAdmin,
        };
    }

    public async Task<List<AttendeeConflictDto>> CheckAttendeesAsync(AttendeeConflictRequest req,
                                                                     CancellationToken ct = default)
    {
        if (req.AttendeeIds.Count == 0 || req.Slots.Count == 0) return new();

        var from = req.Slots.Min(s => s.StartAt);
        var to = req.Slots.Max(s => s.EndAt);
        var ids = req.AttendeeIds.Distinct().ToList();

        var users = await _users.GetByIdsAsync(ids, ct);
        var rows = await _occurrences.GetRangeForUsersAsync(ids, from, to, ct);

        return users.Select(u =>
        {
            var hits = rows.Where(o =>
                    (o.Event!.OwnerId == u.Id || o.Event.Attendees.Any(a => a.UserId == u.Id))
                    && req.Slots.Any(s => OverlapChecker.Overlaps(s.StartAt, s.EndAt, o.StartAt, o.EndAt)))
                .ToList();

            return new AttendeeConflictDto
            {
                UserId = u.Id,
                DisplayName = u.DisplayName,
                ConflictCount = hits.Count,
                Titles = hits.Select(o => o.TitleOverride ?? o.Event!.Title).Distinct().ToList(),
            };
        }).ToList();
    }

    // ---------- 編輯 ----------

    public async Task UpdateAsync(int eventId, EditMode mode, UpdateEventRequest req,
                                  CancellationToken ct = default)
    {
        var ev = await _events.GetTrackedWithAttendeesAsync(eventId, ct)
                 ?? throw new NotFoundException("找不到事件");
        RequireEditPermission(ev);
        if (ev.Status == EventStatus.Cancelled) throw new ValidationException("已取消的事件不能編輯");

        if (mode == EditMode.Single) await UpdateSingleAsync(ev, req, ct);
        else await UpdateSeriesAsync(ev, req, ct);
    }

    private async Task UpdateSingleAsync(Event ev, UpdateEventRequest req, CancellationToken ct)
    {
        var occId = req.OccurrenceId
                    ?? throw new ValidationException("mode=single 必須指定 occurrenceId");

        var occ = await _occurrences.GetTrackedByIdAsync(occId, ct)
                  ?? throw new NotFoundException("找不到該次發生");
        if (occ.EventId != ev.Id) throw new ValidationException("該次發生不屬於此事件");
        if (occ.IsCancelled) throw new ValidationException("已取消的該次發生不能編輯");

        if (req.RoomId != ev.RoomId)
            throw new ValidationException("單筆編輯不可變更會議廳，請取消該次發生後另建事件");

        var (start, end) = Normalize(req.StartAt, req.EndAt, ev.IsAllDay);
        var timeChanged = start != occ.StartAt || end != occ.EndAt;

        var newTitle = req.Title.Trim() == ev.Title ? null : req.Title.Trim();
        var titleChanged = newTitle != occ.TitleOverride;

        if (!timeChanged && !titleChanged) return;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        if (timeChanged) await _booking.MoveOccurrenceAsync(occ, start, end, ct);
        if (titleChanged) await _booking.SetOccurrenceTitleAsync(occ, newTitle, ct);

        ev.UpdatedAt = TaipeiTime.Now(_clock);
        await _db.SaveChangesAsync(ct);

        // 僅修改標題不產生通知（規格 5.5）
        if (timeChanged)
            await _notifications.EventUpdatedAsync(ev, ev.Attendees.Select(a => a.UserId).ToList(),
                                                   occ.OriginalStartAt, start, null, ct);

        await tx.CommitAsync(ct);
    }

    private async Task UpdateSeriesAsync(Event ev, UpdateEventRequest req, CancellationToken ct)
    {
        var (start, end) = Normalize(req.StartAt, req.EndAt, req.IsAllDay);
        var rrule = BuildRrule(req.Recurrence, start);
        var slots = _recurrence.Expand(rrule, start, end);
        var attendeeIds = await ValidateAttendeesAsync(req.AttendeeIds, ct);

        var originalAttendees = ev.Attendees.Select(a => a.UserId).ToHashSet();
        var timeChanged = ev.StartAt != start || ev.EndAt != end || ev.RecurrenceRule != rrule;
        var roomChanged = ev.RoomId != req.RoomId;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        ev.Title = req.Title.Trim();
        ev.Description = req.Description;
        ev.RoomId = req.RoomId;
        ev.StartAt = start;
        ev.EndAt = end;
        ev.IsAllDay = req.IsAllDay;
        ev.RecurrenceRule = rrule;
        ev.UpdatedAt = TaipeiTime.Now(_clock);
        await _db.SaveChangesAsync(ct);

        await _booking.ReExpandSeriesAsync(ev, slots, ct);

        foreach (var gone in ev.Attendees.Where(a => !attendeeIds.Contains(a.UserId)).ToList())
            _db.EventAttendees.Remove(gone);
        foreach (var added in attendeeIds.Where(id => !originalAttendees.Contains(id)))
            _db.EventAttendees.Add(new EventAttendee { EventId = ev.Id, UserId = added });
        await _db.SaveChangesAsync(ct);

        if (timeChanged || roomChanged)
        {
            string? roomName = null;
            if (roomChanged && ev.RoomId is int rid)
                roomName = (await _rooms.GetByIdAsync(rid, ct))?.Name;

            var stillThere = attendeeIds.Where(id => originalAttendees.Contains(id)).ToList();
            await _notifications.EventUpdatedAsync(ev, stillThere, null, start, roomName, ct);
        }

        var newcomers = attendeeIds.Where(id => !originalAttendees.Contains(id)).ToList();
        if (newcomers.Count > 0)
            await _notifications.AddedToEventAsync(ev, slots[0].Start, newcomers, _me.DisplayName, ct);

        await tx.CommitAsync(ct);
    }

    // ---------- 取消 ----------

    public async Task CancelAsync(int eventId, EditMode mode, int? occurrenceId,
                                  CancellationToken ct = default)
    {
        var ev = await _events.GetTrackedWithAttendeesAsync(eventId, ct)
                 ?? throw new NotFoundException("找不到事件");
        RequireEditPermission(ev);

        var forced = _me.IsAdmin && ev.OwnerId != _me.UserId;
        var recipients = ev.Attendees.Select(a => a.UserId).ToList();
        if (forced) recipients.Add(ev.OwnerId);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        DateTime? occStart = null;
        if (mode == EditMode.Single)
        {
            var occId = occurrenceId
                        ?? throw new ValidationException("mode=single 必須指定 occurrenceId");
            var occ = await _occurrences.GetTrackedByIdAsync(occId, ct)
                      ?? throw new NotFoundException("找不到該次發生");
            if (occ.EventId != ev.Id) throw new ValidationException("該次發生不屬於此事件");

            occStart = occ.StartAt;
            await _booking.CancelOccurrenceAsync(occ, ct);
        }
        else
        {
            await _booking.CancelSeriesAsync(ev, ct);
        }

        ev.UpdatedAt = TaipeiTime.Now(_clock);
        await _db.SaveChangesAsync(ct);

        if (forced)
            await _notifications.ForcedCancellationAsync(ev, recipients, occStart, _me.DisplayName, ct);
        else
            await _notifications.EventCancelledAsync(ev, recipients, occStart, ct);

        await tx.CommitAsync(ct);
    }

    // ---------- 共用 ----------

    private void RequireEditPermission(Event ev)
    {
        if (ev.OwnerId != _me.UserId && !_me.IsAdmin)
            throw new ForbiddenException("只有事件擁有者或系統管理員可以修改此事件");
    }

    /// <summary>全天事件的時間部分固定為 00:00–23:59（規格 4.4）。</summary>
    private static (DateTime, DateTime) Normalize(DateTime start, DateTime end, bool isAllDay)
    {
        start = DateTime.SpecifyKind(start, DateTimeKind.Unspecified);
        end = DateTime.SpecifyKind(end, DateTimeKind.Unspecified);

        if (isAllDay)
        {
            start = start.Date;
            end = end.Date.AddHours(23).AddMinutes(59);
        }

        if (end <= start) throw new ValidationException("結束時間必須晚於開始時間");
        return (start, end);
    }

    private string? BuildRrule(RecurrencePatternDto? pattern, DateTime startAt)
    {
        if (pattern is null) return null;
        _recurrence.ValidateStartMatches(pattern, startAt);
        return _recurrence.ToRrule(pattern);
    }

    private async Task<List<int>> ValidateAttendeesAsync(List<int> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().Where(id => id != _me.UserId).ToList();
        if (distinct.Count == 0) return distinct;

        var found = await _users.GetByIdsAsync(distinct, ct);
        var inactive = found.Where(u => !u.IsActive).Select(u => u.DisplayName).ToList();

        if (found.Count != distinct.Count) throw new ValidationException("與會者名單中有不存在的使用者");
        if (inactive.Count > 0)
            throw new ValidationException($"與會者名單中有已停用的帳號：{string.Join("、", inactive)}");

        return distinct;
    }

    private OccurrenceDto ToOccurrenceDto(EventOccurrence o) => new()
    {
        OccurrenceId = o.Id,
        EventId = o.EventId,
        Title = o.TitleOverride ?? o.Event?.Title ?? "",
        StartAt = o.StartAt,
        EndAt = o.EndAt,
        IsAllDay = o.Event?.IsAllDay ?? false,
        RoomId = o.RoomId,
        RoomName = o.Room?.Name,
        OwnerId = o.Event?.OwnerId ?? 0,
        OwnerName = o.Event?.Owner?.DisplayName ?? "",
        IsRecurring = o.Event?.RecurrenceRule is not null,
        IsModified = o.IsModified,
        CanEdit = o.Event?.OwnerId == _me.UserId || _me.IsAdmin,
    };
}
```

在 `Program.cs` 補上：

```csharp
builder.Services.AddScoped<IEventRepository, EventRepository>();
builder.Services.AddScoped<IEventService, EventService>();
```

- [ ] **步驟 7：運行測試驗證通過**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~EventServiceTests"
```

預期：Passed! 9 passed。

- [ ] **步驟 8：Commit**

```bash
git add -A
git commit -m "feat: 新增 EventService，統一事件的交易邊界、權限與通知"
```

---

### 任務 11：`EventsController` 與權限整合測試

**文件：**
- 創建：`src/OfficeCal.Web/Controllers/EventsController.cs`
- 修改：`tests/OfficeCal.Tests/Fixtures/ApiFactory.cs`（新增建立員工帳號的輔助方法）
- 測試：`tests/OfficeCal.Tests/Integration/EventsApiTests.cs`

- [ ] **步驟 1：寫 `EventsController`**

`src/OfficeCal.Web/Controllers/EventsController.cs`：

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Services;

namespace OfficeCal.Web.Controllers;

[ApiController]
[Route("api/v1/events")]
[Authorize]
public class EventsController : ControllerBase
{
    private readonly IEventService _events;
    public EventsController(IEventService events) => _events = events;

    /// <summary>把查詢字串的 mode 轉成列舉。未指定時視為 series。</summary>
    private static EditMode ParseMode(string? mode) => (mode ?? "series").ToLowerInvariant() switch
    {
        "single" => EditMode.Single,
        "series" => EditMode.Series,
        _ => throw new ValidationException("mode 必須是 single 或 series"),
    };

    private static CalendarScope ParseScope(string? scope) => (scope ?? "me").ToLowerInvariant() switch
    {
        "me" => CalendarScope.Me,
        "room" => CalendarScope.Room,
        "all" => CalendarScope.All,
        _ => throw new ValidationException("scope 必須是 me、room 或 all"),
    };

    [HttpGet("")]
    [ProducesResponseType(typeof(ApiResponse<List<OccurrenceDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRangeAsync([FromQuery] DateTime from, [FromQuery] DateTime to,
                                                    [FromQuery] string? scope, [FromQuery] int? roomId,
                                                    CancellationToken ct)
        => Ok(ApiResponse.Ok(await _events.GetRangeAsync(from, to, ParseScope(scope), roomId, ct)));

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ApiResponse<EventDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDetailAsync(int id, CancellationToken ct)
        => Ok(ApiResponse.Ok(await _events.GetDetailAsync(id, ct)));

    [HttpPost("")]
    [ProducesResponseType(typeof(ApiResponse<int>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateAsync([FromBody] CreateEventRequest req, CancellationToken ct)
        => Ok(ApiResponse.Ok(await _events.CreateAsync(req, ct), "已建立事件"));

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateAsync(int id, [FromQuery] string? mode,
                                                  [FromBody] UpdateEventRequest req,
                                                  CancellationToken ct)
    {
        await _events.UpdateAsync(id, ParseMode(mode), req, ct);
        return Ok(ApiResponse.Ok("已更新事件"));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> CancelAsync(int id, [FromQuery] string? mode,
                                                  [FromQuery] int? occurrenceId, CancellationToken ct)
    {
        await _events.CancelAsync(id, ParseMode(mode), occurrenceId, ct);
        return Ok(ApiResponse.Ok("已取消事件"));
    }

    [HttpPost("check-attendees")]
    [ProducesResponseType(typeof(ApiResponse<List<AttendeeConflictDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckAttendeesAsync([FromBody] AttendeeConflictRequest req,
                                                          CancellationToken ct)
        => Ok(ApiResponse.Ok(await _events.CheckAttendeesAsync(req, ct)));
}
```

- [ ] **步驟 2：在 `ApiFactory` 新增建立員工的輔助方法**

在 `tests/OfficeCal.Tests/Fixtures/ApiFactory.cs` 的類別中加入：

```csharp
    /// <summary>建立（或取得）一個員工帳號，密碼固定為 EmployeePassword。</summary>
    public const string EmployeePassword = "Employee@12345";

    public async Task<int> EnsureEmployeeAsync(string employeeNo, string displayName,
                                               OfficeCal.Core.Enums.UserRole role
                                                   = OfficeCal.Core.Enums.UserRole.Employee)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfficeCalDbContext>();
        var pwd = scope.ServiceProvider
                       .GetRequiredService<OfficeCal.Services.IPasswordService>();

        var existing = await db.Users.FirstOrDefaultAsync(u => u.EmployeeNo == employeeNo);
        if (existing is not null) return existing.Id;

        var user = new OfficeCal.Core.Entities.User
        {
            EmployeeNo = employeeNo,
            DisplayName = displayName,
            Email = $"{employeeNo.ToLowerInvariant()}@corp.local",
            Role = role,
            IcsFeedToken = pwd.NewFeedToken(),
            IsActive = true,
        };
        user.PasswordHash = pwd.Hash(user, EmployeePassword);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }
```

並在檔案頂端補上 `using Microsoft.AspNetCore.Hosting;`（若尚未存在）。

- [ ] **步驟 3：編寫失敗的 API 測試**

`tests/OfficeCal.Tests/Integration/EventsApiTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Infrastructure;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("Api")]
public class EventsApiTests
{
    private readonly ApiFactory _api;
    public EventsApiTests(ApiFactory api) => _api = api;

    private static DateTime D(int day, int hour) => new(2027, 3, day, hour, 0, 0);

    private static CreateEventRequest Req(string title, int? roomId, int day, int hour) => new()
    {
        Title = title, RoomId = roomId, StartAt = D(day, hour), EndAt = D(day, hour + 1),
    };

    /// <summary>直接查資料庫取種子會議廳，讓本測試不相依於任務 12 的會議廳 API。</summary>
    private async Task<int> FirstRoomIdAsync()
    {
        await using var db = _api.CreateContext();
        return await db.Rooms.OrderBy(r => r.Id).Select(r => r.Id).FirstAsync();
    }

    [Fact]
    public async Task 建立事件並在區間查詢中看到它()
    {
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);

        var create = await client.PostAsJsonAsync("/api/v1/events", Req("驗收會議", null, 2, 9));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<ApiResponse<int>>(ApiFactory.Json);
        Assert.True(created!.Data > 0);

        var list = await client.GetFromJsonAsync<ApiResponse<List<OccurrenceDto>>>(
            $"/api/v1/events?from={D(2, 0):s}&to={D(3, 0):s}&scope=me", ApiFactory.Json);
        Assert.Contains(list!.Data!, o => o.Title == "驗收會議");
    }

    [Fact]
    public async Task 重複預約同一會議廳的重疊時段回四百零九並附衝突明細()
    {
        var roomId = await FirstRoomIdAsync();
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);

        var first = await client.PostAsJsonAsync("/api/v1/events", Req("先到先得", roomId, 5, 14));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v1/events", Req("後到被擋", roomId, 5, 14));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        using var doc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        var conflicts = root.GetProperty("data").GetProperty("conflicts");
        Assert.Equal(1, conflicts.GetArrayLength());
        Assert.Equal("先到先得", conflicts[0].GetProperty("title").GetString());
        Assert.False(string.IsNullOrEmpty(conflicts[0].GetProperty("roomName").GetString()));
    }

    [Fact]
    public async Task 一般員工不能修改他人事件()
    {
        await _api.EnsureEmployeeAsync("E100", "王小明");
        var admin = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);

        var create = await admin.PostAsJsonAsync("/api/v1/events", Req("管理員的事件", null, 8, 10));
        var id = (await create.Content.ReadFromJsonAsync<ApiResponse<int>>(ApiFactory.Json))!.Data;

        var employee = await _api.LoginAsync("E100", ApiFactory.EmployeePassword);
        var res = await employee.PutAsJsonAsync($"/api/v1/events/{id}?mode=series",
            new UpdateEventRequest { Title = "被亂改", StartAt = D(8, 10), EndAt = D(8, 11) });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task 管理員可以強制取消他人預約()
    {
        var employeeId = await _api.EnsureEmployeeAsync("E101", "李小華");
        var roomId = await FirstRoomIdAsync();

        var employee = await _api.LoginAsync("E101", ApiFactory.EmployeePassword);
        var create = await employee.PostAsJsonAsync("/api/v1/events", Req("員工的預約", roomId, 9, 10));
        var id = (await create.Content.ReadFromJsonAsync<ApiResponse<int>>(ApiFactory.Json))!.Data;

        var admin = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        var del = await admin.DeleteAsync($"/api/v1/events/{id}?mode=series");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        // 擁有者收到強制取消通知
        var notes = await employee.GetFromJsonAsync<ApiResponse<NotificationListDto>>(
            "/api/v1/notifications?unreadOnly=true", ApiFactory.Json);
        Assert.Contains(notes!.Data!.Items, n => n.Type == "ForcedCancellation");
    }

    [Fact]
    public async Task Scope為room卻未附roomId回四百()
    {
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        var res = await client.GetAsync($"/api/v1/events?from={D(2, 0):s}&to={D(3, 0):s}&scope=room");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task 未登入不能建立事件()
    {
        var client = _api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing
            .WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var res = await client.PostAsJsonAsync("/api/v1/events", Req("匿名事件", null, 2, 9));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
```

檔案頂端需要 `using Microsoft.EntityFrameworkCore;`（`FirstAsync`）。

- [ ] **步驟 4：運行測試驗證通過**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~EventsApiTests"
```

預期：Passed! 6 passed。

- [ ] **步驟 5：Commit**

```bash
git add -A
git commit -m "feat: 新增事件 API 與權限整合測試"
```

---

### 任務 12：`RoomService`、會議廳 API 與空房查詢

**文件：**
- 創建：`src/OfficeCal.Core/Dtos/RoomDtos.cs`
- 創建：`src/OfficeCal.Services/IRoomService.cs` + `RoomService.cs`
- 創建：`src/OfficeCal.Web/Controllers/RoomsController.cs`
- 修改：`src/OfficeCal.Web/Program.cs`（註冊 `IRoomService`）
- 測試：`tests/OfficeCal.Tests/Integration/RoomsApiTests.cs`

- [ ] **步驟 1：寫 DTO**

`src/OfficeCal.Core/Dtos/RoomDtos.cs`：

```csharp
using System.ComponentModel.DataAnnotations;

namespace OfficeCal.Core.Dtos;

public class RoomDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Location { get; set; }
    public int Capacity { get; set; }
    public string? Equipment { get; set; }
    public bool IsActive { get; set; }
}

public class RoomRequest
{
    [Required(ErrorMessage = "請輸入會議廳名稱")]
    [StringLength(50)] public string Name { get; set; } = "";
    [StringLength(100)] public string? Location { get; set; }
    [Range(1, 1000, ErrorMessage = "容納人數必須介於 1 到 1000")] public int Capacity { get; set; }
    [StringLength(200)] public string? Equipment { get; set; }
    public bool IsActive { get; set; } = true;
}

public class BusySlotDto
{
    public int OccurrenceId { get; set; }
    public int EventId { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public string Title { get; set; } = "";
    public string OwnerName { get; set; } = "";
}

/// <summary>資源時間軸頁的一列：一間會議廳與它當日的占用時段。</summary>
public class RoomAvailabilityDto
{
    public int RoomId { get; set; }
    public string Name { get; set; } = "";
    public string? Location { get; set; }
    public int Capacity { get; set; }
    public string? Equipment { get; set; }
    public List<BusySlotDto> Busy { get; set; } = new();
}
```

- [ ] **步驟 2：編寫失敗的測試**

`tests/OfficeCal.Tests/Integration/RoomsApiTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Infrastructure;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("Api")]
public class RoomsApiTests
{
    private readonly ApiFactory _api;
    public RoomsApiTests(ApiFactory api) => _api = api;

    [Fact]
    public async Task 已登入者可取得會議廳清單()
    {
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        var res = await client.GetFromJsonAsync<ApiResponse<List<RoomDto>>>("/api/v1/rooms",
                                                                            ApiFactory.Json);
        Assert.True(res!.Data!.Count >= 3);   // 種子資料有三間
    }

    [Fact]
    public async Task 非管理員不能維護會議廳()
    {
        await _api.EnsureEmployeeAsync("E200", "王小明");
        var employee = await _api.LoginAsync("E200", ApiFactory.EmployeePassword);

        var res = await employee.PostAsJsonAsync("/api/v1/rooms",
            new RoomRequest { Name = "偷偷新增的會議室", Capacity = 5 });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task 管理員可新增會議廳且名稱重複回四百()
    {
        var admin = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);

        var ok = await admin.PostAsJsonAsync("/api/v1/rooms",
            new RoomRequest { Name = "C 棟 5F 訓練教室", Location = "C 棟 5 樓", Capacity = 30 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var dup = await admin.PostAsJsonAsync("/api/v1/rooms",
            new RoomRequest { Name = "C 棟 5F 訓練教室", Capacity = 30 });
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
    }

    [Fact]
    public async Task 空房查詢回傳當日占用時段並可依人數過濾()
    {
        var admin = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        var rooms = (await admin.GetFromJsonAsync<ApiResponse<List<RoomDto>>>("/api/v1/rooms",
                                                                              ApiFactory.Json))!.Data!;
        var big = rooms.OrderByDescending(r => r.Capacity).First();

        var day = new DateTime(2027, 5, 10, 0, 0, 0);
        await admin.PostAsJsonAsync("/api/v1/events", new CreateEventRequest
        {
            Title = "占用測試", RoomId = big.Id,
            StartAt = day.AddHours(10), EndAt = day.AddHours(11),
        });

        var res = await admin.GetFromJsonAsync<ApiResponse<List<RoomAvailabilityDto>>>(
            $"/api/v1/rooms/availability?date=2027-05-10&capacity={big.Capacity}", ApiFactory.Json);

        var row = res!.Data!.Single(r => r.RoomId == big.Id);
        Assert.Contains(row.Busy, b => b.StartAt == day.AddHours(10) && b.Title == "占用測試");
        Assert.All(res.Data!, r => Assert.True(r.Capacity >= big.Capacity));
    }
}
```

- [ ] **步驟 3：運行測試驗證失敗**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~RoomsApiTests"
```

預期：404 或編譯失敗——`RoomsController` 尚未存在。

- [ ] **步驟 4：實作 `RoomService` 與 `RoomsController`**

`src/OfficeCal.Services/IRoomService.cs`：

```csharp
using OfficeCal.Core.Dtos;

namespace OfficeCal.Services;

public interface IRoomService
{
    Task<List<RoomDto>> ListAsync(bool activeOnly, CancellationToken ct = default);

    /// <summary>指定日期各會議廳的占用時段，可依最低容納人數過濾。</summary>
    Task<List<RoomAvailabilityDto>> GetAvailabilityAsync(DateOnly date, int? minCapacity,
                                                          CancellationToken ct = default);

    Task<int> CreateAsync(RoomRequest req, CancellationToken ct = default);
    Task UpdateAsync(int id, RoomRequest req, CancellationToken ct = default);
}
```

`src/OfficeCal.Services/RoomService.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;

namespace OfficeCal.Services;

public class RoomService : IRoomService
{
    private readonly OfficeCalDbContext _db;
    private readonly IRoomRepository _rooms;
    private readonly IEventOccurrenceRepository _occurrences;

    public RoomService(OfficeCalDbContext db, IRoomRepository rooms,
                       IEventOccurrenceRepository occurrences)
        => (_db, _rooms, _occurrences) = (db, rooms, occurrences);

    public async Task<List<RoomDto>> ListAsync(bool activeOnly, CancellationToken ct = default)
        => (await _rooms.ListAsync(activeOnly, ct)).Select(ToDto).ToList();

    public async Task<List<RoomAvailabilityDto>> GetAvailabilityAsync(DateOnly date, int? minCapacity,
                                                                       CancellationToken ct = default)
    {
        var from = date.ToDateTime(TimeOnly.MinValue);
        var to = from.AddDays(1);

        var rooms = (await _rooms.ListAsync(activeOnly: true, ct))
            .Where(r => minCapacity is null || r.Capacity >= minCapacity)
            .ToList();

        var result = new List<RoomAvailabilityDto>(rooms.Count);
        foreach (var room in rooms)
        {
            var busy = await _occurrences.GetRangeForRoomAsync(room.Id, from, to, ct);
            result.Add(new RoomAvailabilityDto
            {
                RoomId = room.Id,
                Name = room.Name,
                Location = room.Location,
                Capacity = room.Capacity,
                Equipment = room.Equipment,
                Busy = busy.Select(o => new BusySlotDto
                {
                    OccurrenceId = o.Id,
                    EventId = o.EventId,
                    StartAt = o.StartAt,
                    EndAt = o.EndAt,
                    Title = o.TitleOverride ?? o.Event?.Title ?? "",
                    OwnerName = o.Event?.Owner?.DisplayName ?? "",
                }).OrderBy(b => b.StartAt).ToList(),
            });
        }
        return result;
    }

    public async Task<int> CreateAsync(RoomRequest req, CancellationToken ct = default)
    {
        var name = req.Name.Trim();
        if (await _db.Rooms.AnyAsync(r => r.Name == name, ct))
            throw new ValidationException($"已經有名稱為「{name}」的會議廳");

        var room = new Room
        {
            Name = name, Location = req.Location, Capacity = req.Capacity,
            Equipment = req.Equipment, IsActive = req.IsActive,
        };
        _rooms.Add(room);
        await _db.SaveChangesAsync(ct);
        return room.Id;
    }

    public async Task UpdateAsync(int id, RoomRequest req, CancellationToken ct = default)
    {
        var room = await _rooms.GetByIdAsync(id, ct) ?? throw new NotFoundException("找不到會議廳");
        var name = req.Name.Trim();

        if (await _db.Rooms.AnyAsync(r => r.Name == name && r.Id != id, ct))
            throw new ValidationException($"已經有名稱為「{name}」的會議廳");

        room.Name = name;
        room.Location = req.Location;
        room.Capacity = req.Capacity;
        room.Equipment = req.Equipment;
        room.IsActive = req.IsActive;   // 停用後不可新增預約，既有預約保留
        await _db.SaveChangesAsync(ct);
    }

    private static RoomDto ToDto(Room r) => new()
    {
        Id = r.Id, Name = r.Name, Location = r.Location,
        Capacity = r.Capacity, Equipment = r.Equipment, IsActive = r.IsActive,
    };
}
```

`src/OfficeCal.Web/Controllers/RoomsController.cs`：

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Services;

namespace OfficeCal.Web.Controllers;

[ApiController]
[Route("api/v1/rooms")]
[Authorize]
public class RoomsController : ControllerBase
{
    private readonly IRoomService _rooms;
    public RoomsController(IRoomService rooms) => _rooms = rooms;

    [HttpGet("")]
    [ProducesResponseType(typeof(ApiResponse<List<RoomDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAsync([FromQuery] bool includeInactive = false,
                                                CancellationToken ct = default)
        => Ok(ApiResponse.Ok(await _rooms.ListAsync(activeOnly: !includeInactive, ct)));

    [HttpGet("availability")]
    [ProducesResponseType(typeof(ApiResponse<List<RoomAvailabilityDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> AvailabilityAsync([FromQuery] DateOnly date,
                                                        [FromQuery] int? capacity,
                                                        CancellationToken ct)
        => Ok(ApiResponse.Ok(await _rooms.GetAvailabilityAsync(date, capacity, ct)));

    [HttpPost("")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> CreateAsync([FromBody] RoomRequest req, CancellationToken ct)
        => Ok(ApiResponse.Ok(await _rooms.CreateAsync(req, ct), "已新增會議廳"));

    [HttpPut("{id:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> UpdateAsync(int id, [FromBody] RoomRequest req,
                                                  CancellationToken ct)
    {
        await _rooms.UpdateAsync(id, req, ct);
        return Ok(ApiResponse.Ok("已更新會議廳"));
    }
}
```

在 `Program.cs` 補上 `builder.Services.AddScoped<IRoomService, RoomService>();`。

- [ ] **步驟 5：運行測試驗證通過**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~RoomsApiTests"
```

預期：Passed! 4 passed。

- [ ] **步驟 6：Commit**

```bash
git add -A
git commit -m "feat: 新增會議廳主檔維護與空房查詢 API"
```

---

### 任務 13：`IcsService` —— 單筆匯出與訂閱 feed

規格 5.6。**手寫 RFC 5545 文字，不用 Ical.Net 的序列化器**（見 D5、D6）。

**文件：**
- 創建：`src/OfficeCal.Services/IcsWriter.cs`
- 創建：`src/OfficeCal.Services/IIcsService.cs` + `IcsService.cs`
- 創建：`src/OfficeCal.Web/Controllers/FeedsController.cs`
- 修改：`src/OfficeCal.Web/Controllers/EventsController.cs`（加入 `GET {id}/ics`）
- 修改：`src/OfficeCal.Web/Program.cs`（註冊 `IIcsService`）
- 測試：`tests/OfficeCal.Tests/Unit/IcsWriterTests.cs`
- 測試：`tests/OfficeCal.Tests/Integration/IcsApiTests.cs`

**feed 的時間範圍：** 過去 90 天至未來 730 天。規格未指定範圍，但 feed 必須有界才不會隨資料成長而無限膨脹；這個窗口涵蓋所有實務上會用到的訂閱情境。

- [ ] **步驟 1：編寫失敗的 `IcsWriter` 單元測試**

`tests/OfficeCal.Tests/Unit/IcsWriterTests.cs`：

```csharp
using System.Text;
using OfficeCal.Services;
using Xunit;

namespace OfficeCal.Tests.Unit;

public class IcsWriterTests
{
    [Fact]
    public void 跳脫反斜線分號逗號與換行()
    {
        Assert.Equal(@"a\\b", IcsWriter.Escape(@"a\b"));
        Assert.Equal(@"a\;b", IcsWriter.Escape("a;b"));
        Assert.Equal(@"a\,b", IcsWriter.Escape("a,b"));
        Assert.Equal(@"a\nb", IcsWriter.Escape("a\nb"));
        Assert.Equal(@"a\nb", IcsWriter.Escape("a\r\nb"));
    }

    [Fact]
    public void 短行不折行()
    {
        var sb = new StringBuilder();
        IcsWriter.AppendFolded(sb, "SUMMARY:短標題");
        Assert.Equal("SUMMARY:短標題\r\n", sb.ToString());
    }

    [Fact]
    public void 長行以七十五個位元組為界折行且續行以空白開頭()
    {
        var sb = new StringBuilder();
        IcsWriter.AppendFolded(sb, "SUMMARY:" + new string('A', 200));

        var lines = sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length > 1);
        Assert.All(lines, l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75,
                                            $"這一行有 {Encoding.UTF8.GetByteCount(l)} 個位元組"));
        Assert.All(lines.Skip(1), l => Assert.StartsWith(" ", l));

        var rebuilt = string.Concat(lines.Select((l, i) => i == 0 ? l : l[1..]));
        Assert.Equal("SUMMARY:" + new string('A', 200), rebuilt);
    }

    [Fact]
    public void 中文標題折行不會切斷UTF8位元組序列()
    {
        var title = string.Concat(Enumerable.Repeat("會議室預約通知", 20));   // 每字 3 bytes
        var sb = new StringBuilder();
        IcsWriter.AppendFolded(sb, "SUMMARY:" + title);

        var lines = sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75));
        Assert.DoesNotContain("�", sb.ToString());   // 沒有替換字元＝沒有切壞

        var rebuilt = string.Concat(lines.Select((l, i) => i == 0 ? l : l[1..]));
        Assert.Equal("SUMMARY:" + title, rebuilt);
    }

    [Fact]
    public void 台北時區區塊固定為正八小時且無日光節約()
    {
        var vtz = IcsWriter.TaipeiVTimeZone();
        Assert.Contains("TZID:Asia/Taipei", vtz);
        Assert.Contains("TZOFFSETFROM:+0800", vtz);
        Assert.Contains("TZOFFSETTO:+0800", vtz);
        Assert.DoesNotContain("DAYLIGHT", vtz);
    }
}
```

- [ ] **步驟 2：運行測試驗證失敗**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~IcsWriterTests"
```

預期：編譯失敗，`IcsWriter` 不存在。

- [ ] **步驟 3：實作 `IcsWriter`**

`src/OfficeCal.Services/IcsWriter.cs`：

```csharp
using System.Text;

namespace OfficeCal.Services;

/// <summary>
/// RFC 5545 的文字層細節：跳脫、折行、時區區塊。
/// 折行以 75 個「octet」為界並避開 UTF-8 續接位元組——標題是中文，
/// 以字元數折行會切斷位元組序列而產生亂碼。
/// </summary>
public static class IcsWriter
{
    private const string Crlf = "\r\n";
    private const int MaxOctets = 75;

    public static string Escape(string value)
        => value.Replace("\\", "\\\\")
                .Replace("\r\n", "\\n")
                .Replace("\n", "\\n")
                .Replace("\r", "\\n")
                .Replace(";", "\\;")
                .Replace(",", "\\,");

    public static void AppendFolded(StringBuilder sb, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length <= MaxOctets)
        {
            sb.Append(line).Append(Crlf);
            return;
        }

        var pos = 0;
        var first = true;
        while (pos < bytes.Length)
        {
            // 續行要先加一個空白，所以可用的位元組少一個
            var budget = first ? MaxOctets : MaxOctets - 1;
            var take = Math.Min(budget, bytes.Length - pos);

            // 不要停在 UTF-8 的續接位元組（10xxxxxx）上
            while (take > 0 && pos + take < bytes.Length && (bytes[pos + take] & 0xC0) == 0x80)
                take--;

            if (take == 0) take = Math.Min(budget, bytes.Length - pos);   // 理論上不會發生的保險

            if (!first) sb.Append(' ');
            sb.Append(Encoding.UTF8.GetString(bytes, pos, take)).Append(Crlf);

            pos += take;
            first = false;
        }
    }

    /// <summary>本地時間格式：20260914T100000。</summary>
    public static string Local(DateTime dt) => dt.ToString("yyyyMMdd'T'HHmmss");

    /// <summary>UTC 格式，供 DTSTAMP 使用：20260829T031500Z。</summary>
    public static string Utc(DateTime utc) => utc.ToString("yyyyMMdd'T'HHmmss'Z'");

    /// <summary>
    /// 台北時區區塊。只寫 TZID 而不附 VTIMEZONE，Outlook 訂閱時會顯示錯誤時間。
    /// 台灣自 1980 年起無日光節約，因此只需要一個固定 +08:00 的 STANDARD 區塊。
    /// </summary>
    public static string TaipeiVTimeZone()
    {
        var sb = new StringBuilder();
        AppendFolded(sb, "BEGIN:VTIMEZONE");
        AppendFolded(sb, "TZID:Asia/Taipei");
        AppendFolded(sb, "X-LIC-LOCATION:Asia/Taipei");
        AppendFolded(sb, "BEGIN:STANDARD");
        AppendFolded(sb, "DTSTART:19800101T000000");
        AppendFolded(sb, "TZOFFSETFROM:+0800");
        AppendFolded(sb, "TZOFFSETTO:+0800");
        AppendFolded(sb, "TZNAME:CST");
        AppendFolded(sb, "END:STANDARD");
        AppendFolded(sb, "END:VTIMEZONE");
        return sb.ToString();
    }
}
```

- [ ] **步驟 4：編寫失敗的 .ics API 測試**

`tests/OfficeCal.Tests/Integration/IcsApiTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Infrastructure;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("Api")]
public class IcsApiTests
{
    private readonly ApiFactory _api;
    public IcsApiTests(ApiFactory api) => _api = api;

    private static DateTime D(int day, int hour) => new(2027, 7, day, hour, 0, 0);

    [Fact]
    public async Task 單筆匯出含台北時區區塊與正確時間()
    {
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        var create = await client.PostAsJsonAsync("/api/v1/events", new CreateEventRequest
        {
            Title = "半導體; 研討會, 說明",   // 刻意帶需要跳脫的字元
            StartAt = D(5, 10), EndAt = D(5, 11),
        });
        var id = (await create.Content.ReadFromJsonAsync<ApiResponse<int>>(ApiFactory.Json))!.Data;

        var res = await client.GetAsync($"/api/v1/events/{id}/ics");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/calendar", res.Content.Headers.ContentType!.MediaType);

        var ics = await res.Content.ReadAsStringAsync();
        Assert.StartsWith("BEGIN:VCALENDAR", ics);
        Assert.Contains("BEGIN:VTIMEZONE", ics);
        Assert.Contains("TZID:Asia/Taipei", ics);
        Assert.Contains("DTSTART;TZID=Asia/Taipei:20270705T100000", ics);
        Assert.Contains("DTEND;TZID=Asia/Taipei:20270705T110000", ics);
        Assert.Contains(@"半導體\; 研討會\, 說明", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
    }

    [Fact]
    public async Task 訂閱feed為匿名端點且以token授權()
    {
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        await client.PostAsJsonAsync("/api/v1/events", new CreateEventRequest
        {
            Title = "訂閱測試會議", StartAt = D(6, 9), EndAt = D(6, 10),
        });

        var me = await client.GetFromJsonAsync<ApiResponse<MeDto>>("/api/v1/me", ApiFactory.Json);
        var path = new Uri(me!.Data!.FeedUrl).AbsolutePath;

        // 不帶 Cookie 的全新 client
        var anonymous = _api.CreateClient();
        var res = await anonymous.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var ics = await res.Content.ReadAsStringAsync();
        Assert.Contains("訂閱測試會議", ics);
        Assert.Contains("UID:", ics);
        Assert.Contains("@calendar.local", ics);
    }

    [Fact]
    public async Task 錯誤的feed token回四百零四()
    {
        var anonymous = _api.CreateClient();
        var res = await anonymous.GetAsync("/feeds/this-token-does-not-exist.ics");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task 已取消的發生不出現在feed中()
    {
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        var create = await client.PostAsJsonAsync("/api/v1/events", new CreateEventRequest
        {
            Title = "會被取消的會議", StartAt = D(7, 9), EndAt = D(7, 10),
        });
        var id = (await create.Content.ReadFromJsonAsync<ApiResponse<int>>(ApiFactory.Json))!.Data;
        await client.DeleteAsync($"/api/v1/events/{id}?mode=series");

        var me = await client.GetFromJsonAsync<ApiResponse<MeDto>>("/api/v1/me", ApiFactory.Json);
        var ics = await _api.CreateClient().GetStringAsync(new Uri(me!.Data!.FeedUrl).AbsolutePath);

        Assert.DoesNotContain("會被取消的會議", ics);
    }

    [Fact]
    public async Task 重新產生token後舊網址失效()
    {
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        var before = await client.GetFromJsonAsync<ApiResponse<MeDto>>("/api/v1/me", ApiFactory.Json);
        var oldPath = new Uri(before!.Data!.FeedUrl).AbsolutePath;

        var reset = await client.PostAsync("/api/v1/me/reset-feed-token", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var anonymous = _api.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(oldPath)).StatusCode);

        var after = await client.GetFromJsonAsync<ApiResponse<MeDto>>("/api/v1/me", ApiFactory.Json);
        var newPath = new Uri(after!.Data!.FeedUrl).AbsolutePath;
        Assert.NotEqual(oldPath, newPath);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(newPath)).StatusCode);
    }
}
```

> 最後一個測試用到任務 14 的 `POST /api/v1/me/reset-feed-token`。若尚未完成任務 14，先讓其餘四個測試通過，做完任務 14 再跑它。

- [ ] **步驟 5：實作 `IIcsService` 與 `IcsService`**

`src/OfficeCal.Services/IIcsService.cs`：

```csharp
namespace OfficeCal.Services;

public interface IIcsService
{
    /// <summary>單筆事件的 .ics 內容（含其所有未取消的 occurrence）。權限同事件明細。</summary>
    Task<string> ExportEventAsync(int eventId, CancellationToken ct = default);

    /// <summary>個人訂閱 feed。匿名端點，以 token 授權；token 無效時丟 NotFoundException。</summary>
    Task<string> BuildFeedAsync(string token, CancellationToken ct = default);
}
```

`src/OfficeCal.Services/IcsService.cs`：

```csharp
using System.Text;
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Common;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;

namespace OfficeCal.Services;

public class IcsService : IIcsService
{
    /// <summary>feed 的時間窗口：過去 90 天至未來 730 天。</summary>
    private const int FeedPastDays = 90;
    private const int FeedFutureDays = 730;

    private const string UidDomain = "calendar.local";

    private readonly OfficeCalDbContext _db;
    private readonly IUserRepository _users;
    private readonly IEventOccurrenceRepository _occurrences;
    private readonly IUserContext _me;
    private readonly TimeProvider _clock;

    public IcsService(OfficeCalDbContext db, IUserRepository users,
                      IEventOccurrenceRepository occurrences, IUserContext me, TimeProvider clock)
        => (_db, _users, _occurrences, _me, _clock) = (db, users, occurrences, me, clock);

    public async Task<string> ExportEventAsync(int eventId, CancellationToken ct = default)
    {
        var ev = await _db.Events.AsNoTracking()
            .Include(e => e.Room)
            .Include(e => e.Attendees)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct)
            ?? throw new NotFoundException("找不到事件");

        var isAttendee = ev.Attendees.Any(a => a.UserId == _me.UserId);
        if (ev.OwnerId != _me.UserId && !isAttendee && ev.RoomId is null)
            throw new ForbiddenException("沒有權限匯出此事件");

        var rows = await _db.EventOccurrences.AsNoTracking()
            .Include(o => o.Event!).Include(o => o.Room)
            .Where(o => o.EventId == eventId && !o.IsCancelled)
            .OrderBy(o => o.StartAt)
            .ToListAsync(ct);

        return Build(ev.Title, rows);
    }

    public async Task<string> BuildFeedAsync(string token, CancellationToken ct = default)
    {
        var user = await _users.GetByFeedTokenAsync(token, ct)
                   ?? throw new NotFoundException("訂閱網址無效");

        var now = TaipeiTime.Now(_clock);
        var rows = await _occurrences.GetRangeForUserAsync(
            user.Id, now.AddDays(-FeedPastDays), now.AddDays(FeedFutureDays), ct);

        return Build($"{user.DisplayName} 的行事曆", rows);
    }

    /// <summary>
    /// 輸出已展開的逐筆 VEVENT，不輸出 RRULE——相容性最好，
    /// 且與資料庫的權威占用表完全一致（規格 5.6）。
    /// </summary>
    private string Build(string calendarName, IReadOnlyList<EventOccurrence> rows)
    {
        var stamp = IcsWriter.Utc(_clock.GetUtcNow().UtcDateTime);
        var sb = new StringBuilder();

        IcsWriter.AppendFolded(sb, "BEGIN:VCALENDAR");
        IcsWriter.AppendFolded(sb, "VERSION:2.0");
        IcsWriter.AppendFolded(sb, "PRODID:-//OfficeCal//Meeting Room Booking//ZH-TW");
        IcsWriter.AppendFolded(sb, "CALSCALE:GREGORIAN");
        IcsWriter.AppendFolded(sb, "METHOD:PUBLISH");
        IcsWriter.AppendFolded(sb, $"X-WR-CALNAME:{IcsWriter.Escape(calendarName)}");
        IcsWriter.AppendFolded(sb, "X-WR-TIMEZONE:Asia/Taipei");
        sb.Append(IcsWriter.TaipeiVTimeZone());

        foreach (var o in rows.Where(o => !o.IsCancelled
                                          && o.Event?.Status != EventStatus.Cancelled))
        {
            IcsWriter.AppendFolded(sb, "BEGIN:VEVENT");
            IcsWriter.AppendFolded(sb, $"UID:{o.Id}@{UidDomain}");
            IcsWriter.AppendFolded(sb, $"DTSTAMP:{stamp}");
            IcsWriter.AppendFolded(sb,
                $"DTSTART;TZID=Asia/Taipei:{IcsWriter.Local(o.StartAt)}");
            IcsWriter.AppendFolded(sb,
                $"DTEND;TZID=Asia/Taipei:{IcsWriter.Local(o.EndAt)}");
            IcsWriter.AppendFolded(sb,
                $"SUMMARY:{IcsWriter.Escape(o.TitleOverride ?? o.Event?.Title ?? "")}");

            if (!string.IsNullOrWhiteSpace(o.Event?.Description))
                IcsWriter.AppendFolded(sb, $"DESCRIPTION:{IcsWriter.Escape(o.Event.Description)}");

            if (o.Room is not null)
                IcsWriter.AppendFolded(sb,
                    $"LOCATION:{IcsWriter.Escape(o.Room.Name + (o.Room.Location is null ? "" : $"（{o.Room.Location}）"))}");

            IcsWriter.AppendFolded(sb, "END:VEVENT");
        }

        IcsWriter.AppendFolded(sb, "END:VCALENDAR");
        return sb.ToString();
    }
}
```

- [ ] **步驟 6：加上端點**

把 `src/OfficeCal.Web/Controllers/EventsController.cs` 的欄位與建構式改成：

```csharp
    private readonly IEventService _events;
    private readonly IIcsService _ics;

    public EventsController(IEventService events, IIcsService ics)
        => (_events, _ics) = (events, ics);
```

並在同一個類別中加入：

```csharp
    /// <summary>回傳原始 .ics 文字，不套用統一信封——行事曆軟體要的是檔案本身。</summary>
    [HttpGet("{id:int}/ics")]
    public async Task<IActionResult> ExportIcsAsync(int id, CancellationToken ct)
    {
        var ics = await _ics.ExportEventAsync(id, ct);
        return File(System.Text.Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8",
                    $"event-{id}.ics");
    }
```

`src/OfficeCal.Web/Controllers/FeedsController.cs`：

```csharp
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeCal.Services;

namespace OfficeCal.Web.Controllers;

/// <summary>
/// 個人訂閱 feed。匿名端點，以 token 授權——行事曆軟體訂閱時無法攜帶登入狀態。
/// 不套用統一信封。
/// </summary>
[ApiController]
[Route("feeds")]
[AllowAnonymous]
public class FeedsController : ControllerBase
{
    private readonly IIcsService _ics;
    public FeedsController(IIcsService ics) => _ics = ics;

    [HttpGet("{token}.ics")]
    public async Task<IActionResult> GetAsync(string token, CancellationToken ct)
    {
        var ics = await _ics.BuildFeedAsync(token, ct);
        return File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8", "calendar.ics");
    }
}
```

在 `Program.cs` 補上 `builder.Services.AddScoped<IIcsService, IcsService>();`。

- [ ] **步驟 7：運行測試驗證通過**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~Ics"
```

預期：`IcsWriterTests` 5 passed；`IcsApiTests` 前四個 passed（第五個待任務 14）。

- [ ] **步驟 8：Commit**

```bash
git add -A
git commit -m "feat: 新增 .ics 單筆匯出與個人訂閱 feed"
```

---

### 任務 14：使用者、部門與個人設定 API

規格 §7 的 `POST/PUT /api/v1/users`、`POST /api/v1/me/reset-feed-token`，再加上規格 §8 的畫面所需但 §7 未列出的三項：**修改密碼**、**管理員重設密碼**、**與會者選單所需的員工清單**。

**文件：**
- 創建：`src/OfficeCal.Core/Dtos/UserDtos.cs`
- 創建：`src/OfficeCal.Services/IUserService.cs` + `UserService.cs`
- 創建：`src/OfficeCal.Web/Controllers/UsersController.cs`
- 創建：`src/OfficeCal.Web/Controllers/DepartmentsController.cs`
- 修改：`src/OfficeCal.Web/Controllers/MeController.cs`（加入改密碼與重設 token）
- 修改：`src/OfficeCal.Web/Program.cs`（註冊 `IUserService`）
- 測試：`tests/OfficeCal.Tests/Integration/UsersApiTests.cs`

- [ ] **步驟 1：寫 DTO**

`src/OfficeCal.Core/Dtos/UserDtos.cs`：

```csharp
using System.ComponentModel.DataAnnotations;

namespace OfficeCal.Core.Dtos;

/// <summary>與會者選單用的最小資訊，任何已登入者可讀。</summary>
public class UserPickerDto
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string EmployeeNo { get; set; } = "";
    public string? DepartmentName { get; set; }
}

/// <summary>員工管理後台用，含角色與啟用狀態。</summary>
public class UserAdminDto : UserPickerDto
{
    public string Email { get; set; } = "";
    public int? DepartmentId { get; set; }
    public string Role { get; set; } = "";
    public bool IsActive { get; set; }
}

public class CreateUserRequest
{
    [Required][StringLength(20)] public string EmployeeNo { get; set; } = "";
    [Required][StringLength(50)] public string DisplayName { get; set; } = "";
    [Required][EmailAddress][StringLength(100)] public string Email { get; set; } = "";
    public int? DepartmentId { get; set; }
    [Required] public string Role { get; set; } = "Employee";
    [Required][StringLength(100, MinimumLength = 8, ErrorMessage = "密碼至少 8 個字元")]
    public string Password { get; set; } = "";
}

public class UpdateUserRequest
{
    [Required][StringLength(50)] public string DisplayName { get; set; } = "";
    [Required][EmailAddress][StringLength(100)] public string Email { get; set; } = "";
    public int? DepartmentId { get; set; }
    [Required] public string Role { get; set; } = "Employee";
    public bool IsActive { get; set; } = true;
}

public class ResetPasswordRequest
{
    [Required][StringLength(100, MinimumLength = 8, ErrorMessage = "密碼至少 8 個字元")]
    public string NewPassword { get; set; } = "";
}

public class DepartmentDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
}
```

- [ ] **步驟 2：編寫失敗的測試**

`tests/OfficeCal.Tests/Integration/UsersApiTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Infrastructure;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("Api")]
public class UsersApiTests
{
    private readonly ApiFactory _api;
    public UsersApiTests(ApiFactory api) => _api = api;

    [Fact]
    public async Task 任何已登入者都能取得與會者選單()
    {
        await _api.EnsureEmployeeAsync("E300", "王小明");
        var employee = await _api.LoginAsync("E300", ApiFactory.EmployeePassword);

        var res = await employee.GetFromJsonAsync<ApiResponse<List<UserPickerDto>>>(
            "/api/v1/users/picker", ApiFactory.Json);

        Assert.Contains(res!.Data!, u => u.EmployeeNo == "E300");
    }

    [Fact]
    public async Task 非管理員不能維護員工帳號()
    {
        await _api.EnsureEmployeeAsync("E301", "李小華");
        var employee = await _api.LoginAsync("E301", ApiFactory.EmployeePassword);

        var res = await employee.PostAsJsonAsync("/api/v1/users", new CreateUserRequest
        {
            EmployeeNo = "E999", DisplayName = "偷建的帳號",
            Email = "e999@corp.local", Role = "Admin", Password = "Whatever@123",
        });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task 管理員可建立帳號並用新帳號登入()
    {
        var admin = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);

        var create = await admin.PostAsJsonAsync("/api/v1/users", new CreateUserRequest
        {
            EmployeeNo = "E400", DisplayName = "新進員工",
            Email = "e400@corp.local", Role = "Employee", Password = "NewHire@123",
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var newbie = await _api.LoginAsync("E400", "NewHire@123");
        var me = await newbie.GetFromJsonAsync<ApiResponse<MeDto>>("/api/v1/me", ApiFactory.Json);
        Assert.Equal("新進員工", me!.Data!.DisplayName);
        Assert.False(me.Data.IsAdmin);
    }

    [Fact]
    public async Task 員工編號重複回四百()
    {
        var admin = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        var req = new CreateUserRequest
        {
            EmployeeNo = DbSeeder.AdminEmployeeNo, DisplayName = "撞號",
            Email = "dup@corp.local", Role = "Employee", Password = "Whatever@123",
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/users", req)).StatusCode);
    }

    [Fact]
    public async Task 管理員可重設密碼且新密碼可登入()
    {
        var id = await _api.EnsureEmployeeAsync("E401", "忘記密碼的人");
        var admin = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);

        var res = await admin.PostAsJsonAsync($"/api/v1/users/{id}/reset-password",
            new ResetPasswordRequest { NewPassword = "Reset@12345" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var client = await _api.LoginAsync("E401", "Reset@12345");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task 本人可修改密碼但舊密碼錯誤時回四百()
    {
        await _api.EnsureEmployeeAsync("E402", "改密碼的人");
        var client = await _api.LoginAsync("E402", ApiFactory.EmployeePassword);

        var wrong = await client.PostAsJsonAsync("/api/v1/me/change-password",
            new ChangePasswordRequest { CurrentPassword = "wrong", NewPassword = "Brand@New123" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var ok = await client.PostAsJsonAsync("/api/v1/me/change-password",
            new ChangePasswordRequest
            {
                CurrentPassword = ApiFactory.EmployeePassword, NewPassword = "Brand@New123",
            });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var relogin = await _api.LoginAsync("E402", "Brand@New123");
        Assert.Equal(HttpStatusCode.OK, (await relogin.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task 停用帳號後不能再登入()
    {
        var id = await _api.EnsureEmployeeAsync("E403", "即將離職");
        var admin = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);

        await admin.PutAsJsonAsync($"/api/v1/users/{id}", new UpdateUserRequest
        {
            DisplayName = "即將離職", Email = "e403@corp.local",
            Role = "Employee", IsActive = false,
        });

        var client = _api.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest { EmployeeNo = "E403", Password = ApiFactory.EmployeePassword });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
```

- [ ] **步驟 3：運行測試驗證失敗**

```bash
dotnet test tests/OfficeCal.Tests --filter "FullyQualifiedName~UsersApiTests"
```

預期：404（端點不存在）或編譯失敗。

- [ ] **步驟 4：實作 `IUserService` 與 `UserService`**

`src/OfficeCal.Services/IUserService.cs`：

```csharp
using OfficeCal.Core.Dtos;

namespace OfficeCal.Services;

public interface IUserService
{
    Task<List<UserPickerDto>> ListForPickerAsync(CancellationToken ct = default);
    Task<List<UserAdminDto>> ListForAdminAsync(CancellationToken ct = default);
    Task<int> CreateAsync(CreateUserRequest req, CancellationToken ct = default);
    Task UpdateAsync(int id, UpdateUserRequest req, CancellationToken ct = default);
    Task ResetPasswordAsync(int id, string newPassword, CancellationToken ct = default);
    Task ChangeOwnPasswordAsync(int userId, ChangePasswordRequest req, CancellationToken ct = default);
    /// <summary>重新產生訂閱 token，舊網址即刻失效。回傳新 token。</summary>
    Task<string> ResetFeedTokenAsync(int userId, CancellationToken ct = default);
    Task<List<DepartmentDto>> ListDepartmentsAsync(CancellationToken ct = default);
}
```

`src/OfficeCal.Services/UserService.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;

namespace OfficeCal.Services;

public class UserService : IUserService
{
    private readonly OfficeCalDbContext _db;
    private readonly IUserRepository _users;
    private readonly IPasswordService _passwords;

    public UserService(OfficeCalDbContext db, IUserRepository users, IPasswordService passwords)
        => (_db, _users, _passwords) = (db, users, passwords);

    public async Task<List<UserPickerDto>> ListForPickerAsync(CancellationToken ct = default)
        => (await _users.ListAsync(ct))
            .Where(u => u.IsActive)
            .Select(u => new UserPickerDto
            {
                Id = u.Id, DisplayName = u.DisplayName, EmployeeNo = u.EmployeeNo,
                DepartmentName = u.Department?.Name,
            })
            .ToList();

    public async Task<List<UserAdminDto>> ListForAdminAsync(CancellationToken ct = default)
        => (await _users.ListAsync(ct))
            .Select(u => new UserAdminDto
            {
                Id = u.Id, DisplayName = u.DisplayName, EmployeeNo = u.EmployeeNo,
                DepartmentName = u.Department?.Name, DepartmentId = u.DepartmentId,
                Email = u.Email, Role = u.Role.ToString(), IsActive = u.IsActive,
            })
            .ToList();

    public async Task<int> CreateAsync(CreateUserRequest req, CancellationToken ct = default)
    {
        var employeeNo = req.EmployeeNo.Trim();
        var email = req.Email.Trim();

        if (await _db.Users.AnyAsync(u => u.EmployeeNo == employeeNo, ct))
            throw new ValidationException($"員工編號「{employeeNo}」已存在");
        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
            throw new ValidationException($"Email「{email}」已被使用");

        var user = new User
        {
            EmployeeNo = employeeNo,
            DisplayName = req.DisplayName.Trim(),
            Email = email,
            DepartmentId = req.DepartmentId,
            Role = ParseRole(req.Role),
            IcsFeedToken = _passwords.NewFeedToken(),
            IsActive = true,
        };
        user.PasswordHash = _passwords.Hash(user, req.Password);

        _users.Add(user);
        await _db.SaveChangesAsync(ct);
        return user.Id;
    }

    public async Task UpdateAsync(int id, UpdateUserRequest req, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(id, ct) ?? throw new NotFoundException("找不到使用者");
        var email = req.Email.Trim();

        if (await _db.Users.AnyAsync(u => u.Email == email && u.Id != id, ct))
            throw new ValidationException($"Email「{email}」已被使用");

        user.DisplayName = req.DisplayName.Trim();
        user.Email = email;
        user.DepartmentId = req.DepartmentId;
        user.Role = ParseRole(req.Role);
        user.IsActive = req.IsActive;   // 停用帳號不能登入，既有事件保留
        await _db.SaveChangesAsync(ct);
    }

    public async Task ResetPasswordAsync(int id, string newPassword, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(id, ct) ?? throw new NotFoundException("找不到使用者");
        user.PasswordHash = _passwords.Hash(user, newPassword);
        await _db.SaveChangesAsync(ct);
    }

    public async Task ChangeOwnPasswordAsync(int userId, ChangePasswordRequest req,
                                             CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct) ?? throw new NotFoundException("找不到使用者");

        if (!_passwords.Verify(user, req.CurrentPassword))
            throw new ValidationException("目前密碼不正確");

        user.PasswordHash = _passwords.Hash(user, req.NewPassword);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<string> ResetFeedTokenAsync(int userId, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct) ?? throw new NotFoundException("找不到使用者");
        user.IcsFeedToken = _passwords.NewFeedToken();
        await _db.SaveChangesAsync(ct);
        return user.IcsFeedToken;
    }

    public async Task<List<DepartmentDto>> ListDepartmentsAsync(CancellationToken ct = default)
        => await _db.Departments.AsNoTracking().OrderBy(d => d.Name)
            .Select(d => new DepartmentDto { Id = d.Id, Name = d.Name, IsActive = d.IsActive })
            .ToListAsync(ct);

    private static UserRole ParseRole(string role)
        => Enum.TryParse<UserRole>(role, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ValidationException("角色必須是 Employee 或 Admin");
}
```

- [ ] **步驟 5：寫 Controller**

`src/OfficeCal.Web/Controllers/UsersController.cs`：

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Services;

namespace OfficeCal.Web.Controllers;

[ApiController]
[Route("api/v1/users")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly IUserService _users;
    public UsersController(IUserService users) => _users = users;

    /// <summary>與會者多選用。任何已登入者可讀，只回傳姓名與部門。</summary>
    [HttpGet("picker")]
    [ProducesResponseType(typeof(ApiResponse<List<UserPickerDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> PickerAsync(CancellationToken ct)
        => Ok(ApiResponse.Ok(await _users.ListForPickerAsync(ct)));

    [HttpGet("")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> ListAsync(CancellationToken ct)
        => Ok(ApiResponse.Ok(await _users.ListForAdminAsync(ct)));

    [HttpPost("")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> CreateAsync([FromBody] CreateUserRequest req, CancellationToken ct)
        => Ok(ApiResponse.Ok(await _users.CreateAsync(req, ct), "已建立帳號"));

    [HttpPut("{id:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> UpdateAsync(int id, [FromBody] UpdateUserRequest req,
                                                  CancellationToken ct)
    {
        await _users.UpdateAsync(id, req, ct);
        return Ok(ApiResponse.Ok("已更新帳號"));
    }

    [HttpPost("{id:int}/reset-password")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> ResetPasswordAsync(int id, [FromBody] ResetPasswordRequest req,
                                                         CancellationToken ct)
    {
        await _users.ResetPasswordAsync(id, req.NewPassword, ct);
        return Ok(ApiResponse.Ok("已重設密碼"));
    }
}
```

`src/OfficeCal.Web/Controllers/DepartmentsController.cs`：

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeCal.Core.Common;
using OfficeCal.Services;

namespace OfficeCal.Web.Controllers;

[ApiController]
[Route("api/v1/departments")]
[Authorize]
public class DepartmentsController : ControllerBase
{
    private readonly IUserService _users;
    public DepartmentsController(IUserService users) => _users = users;

    [HttpGet("")]
    public async Task<IActionResult> ListAsync(CancellationToken ct)
        => Ok(ApiResponse.Ok(await _users.ListDepartmentsAsync(ct)));
}
```

把 `src/OfficeCal.Web/Controllers/MeController.cs` 的欄位與建構式改成：

```csharp
    private readonly IUserRepository _users;
    private readonly IUserContext _me;
    private readonly IUserService _userService;

    public MeController(IUserRepository users, IUserContext me, IUserService userService)
        => (_users, _me, _userService) = (users, me, userService);
```

並在同一個類別中加入：

```csharp
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePasswordAsync([FromBody] ChangePasswordRequest req,
                                                          CancellationToken ct)
    {
        await _userService.ChangeOwnPasswordAsync(_me.UserId, req, ct);
        return Ok(ApiResponse.Ok("密碼已更新"));
    }

    [HttpPost("reset-feed-token")]
    public async Task<IActionResult> ResetFeedTokenAsync(CancellationToken ct)
    {
        var token = await _userService.ResetFeedTokenAsync(_me.UserId, ct);
        var url = $"{Request.Scheme}://{Request.Host}/feeds/{token}.ics";
        return Ok(ApiResponse.Ok(new { feedUrl = url }, "已重新產生訂閱網址，舊網址已失效"));
    }
```

在 `Program.cs` 補上 `builder.Services.AddScoped<IUserService, UserService>();`。

- [ ] **步驟 6：運行全部測試**

```bash
dotnet test tests/OfficeCal.Tests
```

預期：全部通過，包含任務 13 先前跳過的「重新產生 token 後舊網址失效」。

- [ ] **步驟 7：Commit**

```bash
git add -A
git commit -m "feat: 新增員工帳號維護、個人設定與訂閱 token 重設"
```

---

## 階段三：前端

前端沒有 JS 測試框架——規格 §10 只要求 xUnit。**每個前端任務的驗證步驟是「跑起來點一遍」**：`dotnet run` 後依步驟中的檢查清單逐項確認。若環境有 Playwright MCP 工具，可用它自動走一遍相同的點擊路徑。

### 任務 15：離線資源、Layout、登入頁與 Axios 共用層

**文件：**
- 創建：`src/OfficeCal.Web/wwwroot/css/bootstrap.min.css` 等五個離線資源
- 創建：`src/OfficeCal.Web/wwwroot/css/site.css`
- 創建：`src/OfficeCal.Web/wwwroot/js/app/api.js`
- 修改：`src/OfficeCal.Web/Pages/Shared/_Layout.cshtml`（全文替換）
- 創建：`src/OfficeCal.Web/Pages/Login.cshtml` + `.cshtml.cs`
- 修改：`src/OfficeCal.Web/Pages/Index.cshtml` + `.cshtml.cs`
- 刪除：`src/OfficeCal.Web/wwwroot/lib/`（樣板附帶的資源，改用我們自己下載的）

- [ ] **步驟 1：下載離線資源**

`net-core-app` 技能建議「從內部知識庫擷取程式碼後寫入檔案」——**此處刻意不照做**：Bootstrap 與 Vue 是數百 KB 的壓縮檔，逐字重建必然損壞。改用釘住版本的下載，內容與正式發行版逐位元一致。

```bash
mkdir -p src/OfficeCal.Web/wwwroot/css src/OfficeCal.Web/wwwroot/js
rm -rf src/OfficeCal.Web/wwwroot/lib

curl -fsSL -o src/OfficeCal.Web/wwwroot/css/bootstrap.min.css \
  https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css
curl -fsSL -o src/OfficeCal.Web/wwwroot/js/bootstrap.bundle.min.js \
  https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js
curl -fsSL -o src/OfficeCal.Web/wwwroot/js/vue.global.prod.js \
  https://cdn.jsdelivr.net/npm/vue@3.5.13/dist/vue.global.prod.js
curl -fsSL -o src/OfficeCal.Web/wwwroot/js/axios.min.js \
  https://cdn.jsdelivr.net/npm/axios@1.7.9/dist/axios.min.js
curl -fsSL -o src/OfficeCal.Web/wwwroot/js/sweetalert2.all.min.js \
  https://cdn.jsdelivr.net/npm/sweetalert2@11.15.10/dist/sweetalert2.all.min.js
```

驗證（每個檔案都應該有合理的大小，且不是 HTML 錯誤頁）：

```bash
ls -l src/OfficeCal.Web/wwwroot/css src/OfficeCal.Web/wwwroot/js
head -c 60 src/OfficeCal.Web/wwwroot/js/vue.global.prod.js; echo
```

預期：`bootstrap.min.css` 約 230 KB、`vue.global.prod.js` 約 150 KB、`axios.min.js` 約 35 KB、`sweetalert2.all.min.js` 約 75 KB、`bootstrap.bundle.min.js` 約 80 KB；`head` 的輸出是 JS 而不是 `<!DOCTYPE html>`。

`vue.global.prod.js` 是**含模板編譯器**的完整建置（不是 `vue.runtime.*`），因為本專案的元件都用字串模板。

- [ ] **步驟 2：寫 `site.css`（Hero 風格）**

`src/OfficeCal.Web/wwwroot/css/site.css`：

```css
:root {
  --oc-primary: #1f3c88;
  --oc-primary-soft: #eaf0ff;
  --oc-accent: #f6a623;
  --oc-ink: #1c2434;
}

body { color: var(--oc-ink); background: #f6f7fb; }

/* Hero 區塊：漸層底 + Overlay，標題、副標題與 CTA */
.oc-hero {
  background: linear-gradient(135deg, var(--oc-primary) 0%, #37509b 55%, #4a69bd 100%);
  color: #fff;
  position: relative;
  overflow: hidden;
}
.oc-hero::after {
  content: "";
  position: absolute; inset: 0;
  background: radial-gradient(circle at 80% 20%, rgba(255,255,255,.18), transparent 55%);
}
.oc-hero > * { position: relative; z-index: 1; }
.oc-hero h1 { font-weight: 700; letter-spacing: .02em; }

.navbar-oc { background: #fff; border-bottom: 1px solid #e6e9f2; }
.card-oc { border: 1px solid #e6e9f2; border-radius: .75rem; background: #fff; }

/* 行事曆格線 */
.oc-month { display: grid; grid-template-columns: repeat(7, 1fr); border-top: 1px solid #e6e9f2; }
.oc-month-cell {
  min-height: 118px; border-right: 1px solid #e6e9f2; border-bottom: 1px solid #e6e9f2;
  padding: .35rem; cursor: pointer; background: #fff;
}
.oc-month-cell.is-other-month { background: #fafbfe; color: #9aa3b5; }
.oc-month-cell.is-today { box-shadow: inset 0 0 0 2px var(--oc-accent); }
.oc-chip {
  display: block; font-size: .78rem; line-height: 1.35; border-radius: .35rem;
  padding: .1rem .35rem; margin-bottom: .15rem; background: var(--oc-primary-soft);
  color: var(--oc-primary); white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
}
.oc-chip.is-room { background: #e8f7ee; color: #1c7a45; }

/* 週／日檢視與資源時間軸共用的時間格線 */
.oc-grid { position: relative; border-left: 1px solid #e6e9f2; }
.oc-hour-row { height: 44px; border-bottom: 1px dashed #edf0f7; }
.oc-slot {
  position: absolute; left: 2px; right: 2px; border-radius: .35rem; padding: .1rem .35rem;
  font-size: .78rem; background: var(--oc-primary); color: #fff; overflow: hidden; cursor: pointer;
}
.oc-timeline-row { position: relative; height: 46px; border-bottom: 1px solid #e6e9f2; }
.oc-timeline-slot {
  position: absolute; top: 5px; bottom: 5px; border-radius: .35rem;
  background: var(--oc-primary); color: #fff; font-size: .75rem;
  padding: 0 .35rem; overflow: hidden; white-space: nowrap; cursor: pointer;
}
.oc-timeline-head { display: flex; font-size: .72rem; color: #7b8496; }
```

- [ ] **步驟 3：寫 Axios 共用層**

`src/OfficeCal.Web/wwwroot/js/app/api.js`：

```js
// 全站共用的 Axios 實例與錯誤處理。頁面一律透過 window.api 呼叫後端。
(function () {
  const http = axios.create({ baseURL: '/', withCredentials: true });

  // 規格 9：409 顯示衝突明細、401 導向登入頁、其餘以 SweetAlert2 顯示 message
  http.interceptors.response.use(
    (res) => res,
    (err) => {
      const status = err.response ? err.response.status : 0;
      const body = err.response ? err.response.data : null;

      if (status === 401) {
        window.location.href = '/Login?returnUrl=' + encodeURIComponent(window.location.pathname);
        return new Promise(() => {});   // 停在這裡，不要讓呼叫端再處理
      }

      if (status === 409 && body && body.data && body.data.conflicts) {
        showConflicts(body.message, body.data.conflicts);
        return Promise.reject(err);
      }

      if (status === 0) {
        Swal.fire('連線失敗', '無法連線到伺服器，請檢查網路後再試', 'error');
      } else {
        const errors = (body && body.errors && body.errors.length)
          ? '<ul class="text-start small mb-0">'
            + body.errors.map((e) => '<li>' + escapeHtml(e) + '</li>').join('') + '</ul>'
          : '';
        Swal.fire({
          icon: 'error',
          title: '操作失敗',
          html: escapeHtml((body && body.message) || '發生未預期的錯誤') + errors,
        });
      }
      return Promise.reject(err);
    }
  );

  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function fmt(iso) {
    const d = new Date(iso);
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getMonth() + 1}/${d.getDate()} ${p(d.getHours())}:${p(d.getMinutes())}`;
  }

  function showConflicts(message, conflicts) {
    const rows = conflicts.map((c) => `
      <tr>
        <td class="text-nowrap">${escapeHtml(c.roomName)}</td>
        <td class="text-nowrap">${fmt(c.startAt)} – ${fmt(c.endAt).split(' ')[1]}</td>
        <td>${escapeHtml(c.title)}</td>
        <td class="text-nowrap">${escapeHtml(c.ownerName)}</td>
      </tr>`).join('');

    Swal.fire({
      icon: 'warning',
      title: message || '會議廳於下列時段已被預約',
      width: 640,
      html: `<div class="table-responsive"><table class="table table-sm align-middle mb-0">
               <thead><tr><th>會議廳</th><th>時段</th><th>事件</th><th>預約人</th></tr></thead>
               <tbody>${rows}</tbody></table></div>
             <p class="small text-muted mt-2 mb-0">整筆預約未寫入，請調整時段後重新送出。</p>`,
    });
  }

  // 統一拆信封：成功時回傳 data，失敗時已由攔截器處理
  async function unwrap(promise) {
    const res = await promise;
    return res.data.data;
  }

  window.api = {
    http,
    escapeHtml,
    fmtDateTime: fmt,
    get: (url, config) => unwrap(http.get(url, config)),
    post: (url, body, config) => unwrap(http.post(url, body, config)),
    put: (url, body, config) => unwrap(http.put(url, body, config)),
    del: (url, config) => unwrap(http.delete(url, config)),

    /** Date → 'YYYY-MM-DDTHH:mm:ss'，不做時區轉換（全系統為台北當地時間）。 */
    toLocalIso(d) {
      const p = (n) => String(n).padStart(2, '0');
      return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
           + `T${p(d.getHours())}:${p(d.getMinutes())}:00`;
    },
    /** 'YYYY-MM-DD' → Date（當地時間 00:00）。 */
    parseDate(s) {
      const [y, m, d] = s.split('-').map(Number);
      return new Date(y, m - 1, d);
    },
  };
})();
```

- [ ] **步驟 4：寫 Layout**

`src/OfficeCal.Web/Pages/Shared/_Layout.cshtml`（全文替換）：

```html
@{
    var isAuthenticated = User.Identity?.IsAuthenticated == true;
    var isAdmin = User.IsInRole("Admin");
}
<!DOCTYPE html>
<html lang="zh-Hant">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>@ViewData["Title"] － 公司行事曆與會議廳預約</title>
    <link rel="stylesheet" href="~/css/bootstrap.min.css" />
    <link rel="stylesheet" href="~/css/site.css" />
</head>
<body>
@if (isAuthenticated)
{
    <nav class="navbar navbar-expand-lg navbar-oc sticky-top">
        <div class="container-fluid px-4">
            <a class="navbar-brand fw-bold text-primary" href="/">公司行事曆</a>
            <button class="navbar-toggler" type="button" data-bs-toggle="collapse"
                    data-bs-target="#navMain"><span class="navbar-toggler-icon"></span></button>
            <div class="collapse navbar-collapse" id="navMain">
                <ul class="navbar-nav me-auto">
                    <li class="nav-item"><a class="nav-link" href="/">我的行事曆</a></li>
                    <li class="nav-item"><a class="nav-link" href="/Rooms">會議廳時間軸</a></li>
                    @if (isAdmin)
                    {
                        <li class="nav-item"><a class="nav-link" href="/Admin/Rooms">會議廳管理</a></li>
                        <li class="nav-item"><a class="nav-link" href="/Admin/Users">員工管理</a></li>
                    }
                </ul>
                <div id="notification-center" class="me-3"></div>
                <ul class="navbar-nav">
                    <li class="nav-item dropdown">
                        <a class="nav-link dropdown-toggle" href="#" data-bs-toggle="dropdown">
                            @User.Identity!.Name
                        </a>
                        <ul class="dropdown-menu dropdown-menu-end">
                            <li><a class="dropdown-item" href="/Settings">個人設定</a></li>
                            <li><hr class="dropdown-divider" /></li>
                            <li><a class="dropdown-item" href="#" id="logout-link">登出</a></li>
                        </ul>
                    </li>
                </ul>
            </div>
        </div>
    </nav>
}

<main>
    @RenderBody()
</main>

<script src="~/js/bootstrap.bundle.min.js"></script>
<script src="~/js/vue.global.prod.js"></script>
<script src="~/js/axios.min.js"></script>
<script src="~/js/sweetalert2.all.min.js"></script>
<script src="~/js/app/api.js"></script>
@if (isAuthenticated)
{
    <script>
        document.getElementById('logout-link').addEventListener('click', async function (e) {
            e.preventDefault();
            await window.api.post('/api/v1/auth/logout');
            window.location.href = '/Login';
        });
    </script>
}
@await RenderSectionAsync("Scripts", required: false)
</body>
</html>
```

- [ ] **步驟 5：寫登入頁與首頁殼**

`src/OfficeCal.Web/Pages/Login.cshtml.cs`：

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OfficeCal.Web.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    public void OnGet() { }
}
```

`src/OfficeCal.Web/Pages/Login.cshtml`：

```html
@page
@model OfficeCal.Web.Pages.LoginModel
@{
    ViewData["Title"] = "登入";
    Layout = "_Layout";
}

<section class="oc-hero py-5">
    <div class="container py-5">
        <div class="row align-items-center g-5">
            <div class="col-lg-6">
                <h1 class="display-5 mb-3">公司行事曆與會議廳預約</h1>
                <p class="lead mb-4 opacity-75">
                    管理個人日程、預約會議廳，同一間會議廳的同一時段永遠不會被重複預約。
                </p>
                <ul class="list-unstyled opacity-75">
                    <li class="mb-2">・月／週／日三種檢視，一眼看完整週行程</li>
                    <li class="mb-2">・會議廳資源時間軸，空檔一目了然</li>
                    <li class="mb-2">・可訂閱的 .ics 網址，行事曆軟體直接同步</li>
                </ul>
            </div>
            <div class="col-lg-5 offset-lg-1">
                <div class="card card-oc shadow-lg p-4" id="login-app">
                    <h2 class="h4 mb-3 text-dark">登入</h2>
                    <form @@submit.prevent="submit">
                        <div class="mb-3">
                            <label class="form-label text-dark">員工編號</label>
                            <input class="form-control" v-model.trim="employeeNo" autofocus
                                   autocomplete="username" />
                        </div>
                        <div class="mb-3">
                            <label class="form-label text-dark">密碼</label>
                            <input class="form-control" type="password" v-model="password"
                                   autocomplete="current-password" />
                        </div>
                        <button class="btn btn-primary w-100 py-2" :disabled="loading">
                            {{ loading ? '登入中…' : '登入' }}
                        </button>
                    </form>
                </div>
            </div>
        </div>
    </div>
</section>

@section Scripts {
<script>
    const { createApp } = Vue;
    createApp({
        data() { return { employeeNo: '', password: '', loading: false }; },
        methods: {
            async submit() {
                if (!this.employeeNo || !this.password) {
                    Swal.fire('請輸入完整資料', '員工編號與密碼都要填', 'info');
                    return;
                }
                this.loading = true;
                try {
                    await window.api.post('/api/v1/auth/login',
                        { employeeNo: this.employeeNo, password: this.password });
                    const params = new URLSearchParams(window.location.search);
                    window.location.href = params.get('returnUrl') || '/';
                } catch (e) {
                    // 攔截器已顯示訊息
                } finally {
                    this.loading = false;
                }
            },
        },
    }).mount('#login-app');
</script>
}
```

註：Razor 中 Vue 的 `@submit` 要寫成 `@@submit`，`{{ }}` 插值不需跳脫。

`src/OfficeCal.Web/Pages/Index.cshtml.cs`：

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OfficeCal.Web.Pages;

[Authorize]
public class IndexModel : PageModel
{
    public void OnGet() { }
}
```

`src/OfficeCal.Web/Pages/Index.cshtml`（任務 17 會填入行事曆內容）：

```html
@page
@model OfficeCal.Web.Pages.IndexModel
@{
    ViewData["Title"] = "我的行事曆";
}
<div class="container-fluid px-4 py-4">
    <div id="calendar-app"></div>
</div>
```

- [ ] **步驟 6：驗證（跑起來點一遍）**

```bash
dotnet run --project src/OfficeCal.Web
```

檢查清單（在瀏覽器開啟終端輸出的網址）：

1. 未登入時開 `/` → 被導向 `/Login`
2. 登入頁看得到 Hero 區塊（漸層背景、大標題、CTA 按鈕），版面沒有跑掉 → 表示 `bootstrap.min.css` 正確載入
3. 開發者工具 Network 分頁：五個離線資源都是 200，且**沒有任何請求指向 cdn 網域**
4. 用 `A0001` / `Admin@12345` 登入 → 導向 `/`，導覽列出現「我的行事曆」「會議廳時間軸」「會議廳管理」「員工管理」
5. 故意輸入錯誤密碼 → 跳出 SweetAlert2「操作失敗：員工編號或密碼錯誤」
6. 點右上角姓名 → 登出 → 回到登入頁；再開 `/` 仍被導向登入頁

- [ ] **步驟 7：Commit**

```bash
git add -A
git commit -m "feat: 前端骨架、離線資源、Hero 風格登入頁與 Axios 共用層"
```

---

### 任務 16：重複設定器與事件彈窗

規格 5.3「使用者永遠不會看到或輸入 RRULE 字串」、§8 的事件建立／編輯彈窗與事件明細彈窗。這兩個元件被「我的行事曆」與「會議廳時間軸」兩頁共用，因此獨立成一個任務。

**文件：**
- 創建：`src/OfficeCal.Web/wwwroot/js/app/recurrence-editor.js`
- 創建：`src/OfficeCal.Web/wwwroot/js/app/event-modal.js`
- 創建：`src/OfficeCal.Web/wwwroot/js/app/event-detail-modal.js`
- 修改：`src/OfficeCal.Web/Pages/Shared/_Layout.cshtml`（載入這三支 js）

- [ ] **步驟 1：寫重複設定器**

`src/OfficeCal.Web/wwwroot/js/app/recurrence-editor.js`：

```js
// 結構化重複設定器。對外的 modelValue 就是後端的 RecurrencePatternDto（null = 不重複）。
// 使用者永遠看不到 RRULE 字串——轉換一律由後端的 RruleFormatter 負責。
(function () {
  const WEEKDAYS = [
    { value: 'Sunday', label: '日' },
    { value: 'Monday', label: '一' },
    { value: 'Tuesday', label: '二' },
    { value: 'Wednesday', label: '三' },
    { value: 'Thursday', label: '四' },
    { value: 'Friday', label: '五' },
    { value: 'Saturday', label: '六' },
  ];

  window.RecurrenceEditor = {
    props: {
      modelValue: { type: Object, default: null },
      // 事件起始日，用來推導預設值（yyyy-MM-dd）
      startDate: { type: String, required: true },
    },
    emits: ['update:modelValue'],
    data() {
      return { weekdays: WEEKDAYS, freq: 'None', p: this.defaults() };
    },
    computed: {
      startAsDate() { return window.api.parseDate(this.startDate); },
      nthLabel() {
        const d = this.startAsDate;
        const nth = Math.floor((d.getDate() - 1) / 7) + 1;
        return ['第一個', '第二個', '第三個', '第四個'][nth - 1] || '第四個';
      },
    },
    watch: {
      modelValue: {
        immediate: true,
        handler(v) {
          if (!v) { this.freq = 'None'; this.p = this.defaults(); return; }
          this.freq = v.frequency;
          this.p = Object.assign(this.defaults(), v);
        },
      },
      // 起始日改變時，重新推導與起始日繫結的欄位（後端會驗證兩者必須一致）
      startDate() { if (this.freq !== 'None') { this.syncToStart(); this.emit(); } },
    },
    methods: {
      defaults() {
        return {
          frequency: 'Weekly',
          interval: 1,
          byWeekDays: [],
          monthlyMode: 'DayOfMonth',
          byMonthDay: null,
          bySetPosition: null,
          byPositionWeekDay: null,
          byMonth: null,
          endMode: 'UntilDate',
          untilDate: null,
          count: null,
        };
      },
      syncToStart() {
        const d = this.startAsDate;
        const dow = WEEKDAYS[d.getDay()].value;
        const nth = Math.floor((d.getDate() - 1) / 7) + 1;

        if (this.freq === 'Weekly' && this.p.byWeekDays.length === 0) this.p.byWeekDays = [dow];
        if (this.freq === 'Weekly' && !this.p.byWeekDays.includes(dow)) this.p.byWeekDays = [dow];

        if (this.freq === 'Monthly') {
          this.p.byMonthDay = d.getDate();
          this.p.byPositionWeekDay = dow;
          this.p.bySetPosition = nth > 4 ? -1 : nth;
        }
        if (this.freq === 'Yearly') {
          this.p.byMonth = d.getMonth() + 1;
          this.p.byMonthDay = d.getDate();
        }
        if (!this.p.untilDate) {
          const until = new Date(d.getFullYear(), d.getMonth() + 3, d.getDate());
          this.p.untilDate = window.api.toLocalIso(until).slice(0, 10);
        }
      },
      onFreqChange() {
        if (this.freq === 'None') { this.$emit('update:modelValue', null); return; }
        this.p.frequency = this.freq;
        this.p.interval = this.p.interval || 1;
        this.syncToStart();
        this.emit();
      },
      toggleWeekday(v) {
        const i = this.p.byWeekDays.indexOf(v);
        if (i >= 0) this.p.byWeekDays.splice(i, 1);
        else this.p.byWeekDays.push(v);
        this.emit();
      },
      emit() {
        if (this.freq === 'None') { this.$emit('update:modelValue', null); return; }
        const out = {
          frequency: this.freq,
          interval: Number(this.p.interval) || 1,
          byWeekDays: this.freq === 'Weekly' ? this.p.byWeekDays.slice() : [],
          monthlyMode: this.p.monthlyMode,
          byMonthDay: null,
          bySetPosition: null,
          byPositionWeekDay: null,
          byMonth: null,
          endMode: this.p.endMode,
          untilDate: this.p.endMode === 'UntilDate' ? this.p.untilDate : null,
          count: this.p.endMode === 'Count' ? (Number(this.p.count) || 1) : null,
        };
        if (this.freq === 'Monthly' && this.p.monthlyMode === 'DayOfMonth') {
          out.byMonthDay = Number(this.p.byMonthDay);
        }
        if (this.freq === 'Monthly' && this.p.monthlyMode === 'WeekDayOfMonth') {
          out.bySetPosition = Number(this.p.bySetPosition);
          out.byPositionWeekDay = this.p.byPositionWeekDay;
        }
        if (this.freq === 'Yearly') {
          out.byMonth = Number(this.p.byMonth);
          out.byMonthDay = Number(this.p.byMonthDay);
        }
        this.$emit('update:modelValue', out);
      },
    },
    template: `
<div class="border rounded p-3 bg-light-subtle">
  <div class="row g-2 align-items-end">
    <div class="col-sm-5">
      <label class="form-label small mb-1">重複</label>
      <select class="form-select form-select-sm" v-model="freq" @change="onFreqChange">
        <option value="None">不重複</option>
        <option value="Daily">每天</option>
        <option value="Weekly">每週</option>
        <option value="Monthly">每月</option>
        <option value="Yearly">每年</option>
      </select>
    </div>
    <div class="col-sm-4" v-if="freq !== 'None'">
      <label class="form-label small mb-1">間隔</label>
      <div class="input-group input-group-sm">
        <span class="input-group-text">每</span>
        <input type="number" min="1" max="999" class="form-control"
               v-model.number="p.interval" @change="emit" />
        <span class="input-group-text">
          {{ { Daily:'天', Weekly:'週', Monthly:'個月', Yearly:'年' }[freq] }}
        </span>
      </div>
    </div>
  </div>

  <div class="mt-3" v-if="freq === 'Weekly'">
    <label class="form-label small mb-1">星期（可複選）</label>
    <div class="btn-group d-flex flex-wrap" role="group">
      <button type="button" class="btn btn-sm me-1 mb-1"
              v-for="w in weekdays" :key="w.value"
              :class="p.byWeekDays.includes(w.value) ? 'btn-primary' : 'btn-outline-secondary'"
              @click="toggleWeekday(w.value)">{{ w.label }}</button>
    </div>
  </div>

  <div class="mt-3" v-if="freq === 'Monthly'">
    <div class="form-check">
      <input class="form-check-input" type="radio" id="mm-day" value="DayOfMonth"
             v-model="p.monthlyMode" @change="emit" />
      <label class="form-check-label small" for="mm-day">每月 {{ p.byMonthDay }} 日</label>
    </div>
    <div class="form-check">
      <input class="form-check-input" type="radio" id="mm-nth" value="WeekDayOfMonth"
             v-model="p.monthlyMode" @change="emit" />
      <label class="form-check-label small" for="mm-nth">
        每月{{ p.bySetPosition === -1 ? '最後一個' : nthLabel }}星期{{
          weekdays.find(w => w.value === p.byPositionWeekDay)
            ? weekdays.find(w => w.value === p.byPositionWeekDay).label : '' }}
      </label>
    </div>
    <div class="form-check ms-4" v-if="p.monthlyMode === 'WeekDayOfMonth'">
      <input class="form-check-input" type="checkbox" id="mm-last"
             :checked="p.bySetPosition === -1"
             @change="p.bySetPosition = $event.target.checked ? -1
                        : Math.floor((startAsDate.getDate() - 1) / 7) + 1; emit()" />
      <label class="form-check-label small" for="mm-last">改用「每月最後一個」</label>
    </div>
  </div>

  <div class="mt-3" v-if="freq === 'Yearly'">
    <span class="small">每年 {{ p.byMonth }} 月 {{ p.byMonthDay }} 日</span>
  </div>

  <div class="mt-3" v-if="freq !== 'None'">
    <label class="form-label small mb-1">結束條件（必填）</label>
    <div class="row g-2">
      <div class="col-sm-6">
        <div class="input-group input-group-sm">
          <div class="input-group-text">
            <input class="form-check-input mt-0" type="radio" value="UntilDate"
                   v-model="p.endMode" @change="emit" />
          </div>
          <input type="date" class="form-control" v-model="p.untilDate"
                 :disabled="p.endMode !== 'UntilDate'" @change="emit" />
        </div>
      </div>
      <div class="col-sm-6">
        <div class="input-group input-group-sm">
          <div class="input-group-text">
            <input class="form-check-input mt-0" type="radio" value="Count"
                   v-model="p.endMode" @change="emit" />
          </div>
          <input type="number" min="1" max="730" class="form-control" placeholder="重複次數"
                 v-model.number="p.count" :disabled="p.endMode !== 'Count'" @change="emit" />
          <span class="input-group-text">次</span>
        </div>
      </div>
    </div>
    <div class="form-text">重複事件必須有結束日期或次數，且展開後不得超過 730 次。</div>
  </div>
</div>`,
  };
})();
```

- [ ] **步驟 2：寫事件彈窗**

`src/OfficeCal.Web/wwwroot/js/app/event-modal.js`：

```js
// 事件建立／編輯彈窗 + 明細彈窗。以 Bootstrap Modal 呈現，父元件透過 ref 呼叫 open*()。
(function () {
  window.EventModal = {
    components: { RecurrenceEditor: window.RecurrenceEditor },
    props: { rooms: { type: Array, default: () => [] }, currentUserId: { type: Number, required: true } },
    emits: ['saved'],
    data() {
      return {
        modal: null,
        mode: 'create',        // create | edit
        editScope: 'series',   // series | single
        saving: false,
        eventId: null,
        occurrenceId: null,
        canEdit: true,
        isRecurring: false,
        users: [],
        attendeeWarnings: [],
        form: this.blank(),
      };
    },
    mounted() {
      this.modal = new bootstrap.Modal(this.$refs.root);
      window.api.get('/api/v1/users/picker').then((u) => { this.users = u; });
    },
    computed: {
      startDate() { return this.form.startAt.slice(0, 10); },
      // 不要在同一個元素上同時寫 v-for 與 v-if：Vue 3 的 v-if 優先度較高，
      // 會在迴圈變數還不存在時求值。先在這裡濾好。
      selectableUsers() { return this.users.filter((u) => u.id !== this.currentUserId); },
      title() {
        if (this.mode === 'create') return '建立事件';
        return this.editScope === 'single' ? '編輯這一筆' : '編輯整個系列';
      },
      singleLocked() { return this.mode === 'edit' && this.editScope === 'single'; },
    },
    methods: {
      blank() {
        const now = new Date();
        now.setMinutes(0, 0, 0);
        const start = new Date(now.getTime() + 3600000);
        return {
          title: '', description: '', roomId: null,
          startAt: window.api.toLocalIso(start).slice(0, 16),
          endAt: window.api.toLocalIso(new Date(start.getTime() + 3600000)).slice(0, 16),
          isAllDay: false, attendeeIds: [], recurrence: null,
        };
      },

      /** 從行事曆空白格快速建立。start/end 為 Date，roomId 可選。 */
      openCreate(start, end, roomId) {
        this.mode = 'create';
        this.editScope = 'series';
        this.eventId = null;
        this.occurrenceId = null;
        this.canEdit = true;
        this.isRecurring = false;
        this.attendeeWarnings = [];
        this.form = this.blank();
        if (start) this.form.startAt = window.api.toLocalIso(start).slice(0, 16);
        if (end) this.form.endAt = window.api.toLocalIso(end).slice(0, 16);
        if (roomId) this.form.roomId = roomId;
        this.modal.show();
      },

      /** 從既有 occurrence 開啟編輯。scope = 'single' | 'series'。 */
      async openEdit(occurrence, scope) {
        const detail = await window.api.get('/api/v1/events/' + occurrence.eventId);
        this.mode = 'edit';
        this.editScope = scope;
        this.eventId = detail.id;
        this.occurrenceId = occurrence.occurrenceId;
        this.canEdit = detail.canEdit;
        this.isRecurring = !!detail.recurrence;
        this.attendeeWarnings = [];

        this.form = {
          title: scope === 'single' ? occurrence.title : detail.title,
          description: detail.description || '',
          roomId: detail.roomId,
          startAt: (scope === 'single' ? occurrence.startAt : detail.startAt).slice(0, 16),
          endAt: (scope === 'single' ? occurrence.endAt : detail.endAt).slice(0, 16),
          isAllDay: detail.isAllDay,
          attendeeIds: detail.attendees.map((a) => a.userId),
          recurrence: scope === 'single' ? null : detail.recurrence,
        };
        this.modal.show();
      },

      async checkAttendees() {
        if (this.form.attendeeIds.length === 0) { this.attendeeWarnings = []; return; }
        const result = await window.api.post('/api/v1/events/check-attendees', {
          attendeeIds: this.form.attendeeIds,
          slots: [{ startAt: this.form.startAt + ':00', endAt: this.form.endAt + ':00' }],
        });
        this.attendeeWarnings = result.filter((r) => r.conflictCount > 0);
      },

      async save() {
        if (!this.form.title.trim()) {
          Swal.fire('請輸入標題', '', 'info');
          return;
        }
        const body = {
          title: this.form.title.trim(),
          description: this.form.description,
          roomId: this.form.roomId || null,
          startAt: this.form.startAt + ':00',
          endAt: this.form.endAt + ':00',
          isAllDay: this.form.isAllDay,
          attendeeIds: this.form.attendeeIds,
          recurrence: this.form.recurrence,
          occurrenceId: this.editScope === 'single' ? this.occurrenceId : null,
        };

        this.saving = true;
        try {
          if (this.mode === 'create') {
            await window.api.post('/api/v1/events', body);
          } else {
            await window.api.put(
              `/api/v1/events/${this.eventId}?mode=${this.editScope}`, body);
          }
          this.modal.hide();
          this.$emit('saved');
          Swal.fire({ icon: 'success', title: '已儲存', timer: 1200, showConfirmButton: false });
        } catch (e) {
          // 409 的衝突明細已由攔截器以 SweetAlert2 呈現，彈窗保持開啟讓使用者改時段
        } finally {
          this.saving = false;
        }
      },
    },
    template: `
<div class="modal fade" tabindex="-1" ref="root">
  <div class="modal-dialog modal-lg modal-dialog-scrollable">
    <div class="modal-content">
      <div class="modal-header">
        <h5 class="modal-title">{{ title }}</h5>
        <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
      </div>
      <div class="modal-body">
        <div class="mb-3">
          <label class="form-label">標題</label>
          <input class="form-control" v-model="form.title" maxlength="100" />
        </div>

        <div class="mb-3" v-if="!singleLocked">
          <label class="form-label">說明</label>
          <textarea class="form-control" rows="2" v-model="form.description"
                    maxlength="1000"></textarea>
        </div>

        <div class="row g-3 mb-3">
          <div class="col-md-6">
            <label class="form-label">開始</label>
            <input type="datetime-local" class="form-control" v-model="form.startAt"
                   @change="checkAttendees" />
          </div>
          <div class="col-md-6">
            <label class="form-label">結束</label>
            <input type="datetime-local" class="form-control" v-model="form.endAt"
                   @change="checkAttendees" />
          </div>
        </div>

        <div class="form-check mb-3" v-if="!singleLocked">
          <input class="form-check-input" type="checkbox" id="all-day" v-model="form.isAllDay" />
          <label class="form-check-label" for="all-day">全天事件（00:00–23:59）</label>
        </div>

        <div class="mb-3">
          <label class="form-label">會議廳</label>
          <select class="form-select" v-model="form.roomId" :disabled="singleLocked">
            <option :value="null">不指定（純個人事件，不占用資源）</option>
            <option v-for="r in rooms" :key="r.id" :value="r.id">
              {{ r.name }}（{{ r.capacity }} 人）{{ r.location ? '・' + r.location : '' }}
            </option>
          </select>
          <div class="form-text" v-if="singleLocked">
            單筆編輯不可變更會議廳。要換會議廳請取消這一筆後另建事件。
          </div>
        </div>

        <div class="mb-3" v-if="!singleLocked">
          <label class="form-label">與會者</label>
          <select class="form-select" multiple size="6" v-model="form.attendeeIds"
                  @change="checkAttendees">
            <option v-for="u in selectableUsers" :key="u.id" :value="u.id">
              {{ u.displayName }}（{{ u.employeeNo }}）{{ u.departmentName ? '・' + u.departmentName : '' }}
            </option>
          </select>
          <div class="form-text">按住 Ctrl／Cmd 可複選。</div>
          <div class="alert alert-warning py-2 px-3 mt-2 mb-0" v-if="attendeeWarnings.length">
            <div class="small" v-for="w in attendeeWarnings" :key="w.userId">
              {{ w.displayName }} 該時段已有 {{ w.conflictCount }} 場會議（{{ w.titles.join('、') }}）
            </div>
            <div class="small text-muted mt-1">這只是提示，仍可直接送出。</div>
          </div>
        </div>

        <div class="mb-2" v-if="!singleLocked">
          <recurrence-editor v-model="form.recurrence" :start-date="startDate"></recurrence-editor>
        </div>
        <div class="alert alert-info py-2 px-3 small mb-0" v-if="singleLocked">
          只會修改這一次發生，其餘各次不受影響。
        </div>
      </div>
      <div class="modal-footer">
        <button class="btn btn-outline-secondary" data-bs-dismiss="modal">取消</button>
        <button class="btn btn-primary" :disabled="saving || !canEdit" @click="save">
          {{ saving ? '儲存中…' : '儲存' }}
        </button>
      </div>
    </div>
  </div>
</div>`,
  };
})();
```

- [ ] **步驟 3：寫事件明細彈窗**

`src/OfficeCal.Web/wwwroot/js/app/event-detail-modal.js`：

```js
// 事件明細彈窗：完整資訊、與會者名單、「這一筆／整個系列」的編輯與取消、單筆 .ics 下載。
(function () {
  window.EventDetailModal = {
    emits: ['edit', 'changed'],
    data() { return { modal: null, occ: null, detail: null }; },
    mounted() { this.modal = new bootstrap.Modal(this.$refs.root); },
    computed: {
      isRecurring() { return !!(this.detail && this.detail.recurrence); },
      when() {
        if (!this.occ) return '';
        return window.api.fmtDateTime(this.occ.startAt) + ' – '
             + window.api.fmtDateTime(this.occ.endAt).split(' ')[1];
      },
    },
    methods: {
      async open(occurrence) {
        this.occ = occurrence;
        this.detail = await window.api.get('/api/v1/events/' + occurrence.eventId);
        this.modal.show();
      },
      edit(scope) {
        this.modal.hide();
        this.$emit('edit', { occurrence: this.occ, scope: scope });
      },
      async cancel(scope) {
        const text = scope === 'single'
          ? '只會取消這一次發生，該時段的會議廳會被釋出。'
          : '整個系列的所有次數都會被取消。';
        const confirmed = await Swal.fire({
          icon: 'warning', title: '確定要取消嗎？', text: text,
          showCancelButton: true, confirmButtonText: '確定取消', cancelButtonText: '再想想',
        });
        if (!confirmed.isConfirmed) return;

        let url = `/api/v1/events/${this.detail.id}?mode=${scope}`;
        if (scope === 'single') url += `&occurrenceId=${this.occ.occurrenceId}`;
        await window.api.del(url);

        this.modal.hide();
        this.$emit('changed');
        Swal.fire({ icon: 'success', title: '已取消', timer: 1200, showConfirmButton: false });
      },
      downloadIcs() { window.location.href = `/api/v1/events/${this.detail.id}/ics`; },
    },
    template: `
<div class="modal fade" tabindex="-1" ref="root">
  <div class="modal-dialog modal-dialog-centered">
    <div class="modal-content" v-if="detail">
      <div class="modal-header">
        <h5 class="modal-title">{{ occ.title }}</h5>
        <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
      </div>
      <div class="modal-body">
        <dl class="row mb-0 small">
          <dt class="col-4">時間</dt><dd class="col-8">{{ when }}</dd>
          <dt class="col-4">會議廳</dt>
          <dd class="col-8">{{ detail.roomName || '未指定（純個人事件）' }}</dd>
          <dt class="col-4">預約人</dt><dd class="col-8">{{ detail.ownerName }}</dd>
          <dt class="col-4" v-if="isRecurring">重複</dt>
          <dd class="col-8" v-if="isRecurring">此事件屬於一個重複系列</dd>
          <dt class="col-4" v-if="detail.description">說明</dt>
          <dd class="col-8" v-if="detail.description" style="white-space:pre-wrap">
            {{ detail.description }}
          </dd>
          <dt class="col-4">與會者</dt>
          <dd class="col-8">
            <span v-if="!detail.attendees.length" class="text-muted">無</span>
            <span v-for="a in detail.attendees" :key="a.userId"
                  class="badge text-bg-light me-1 mb-1">{{ a.displayName }}</span>
          </dd>
        </dl>
      </div>
      <div class="modal-footer justify-content-between">
        <button class="btn btn-outline-secondary btn-sm" @click="downloadIcs">下載 .ics</button>
        <div v-if="detail.canEdit">
          <template v-if="isRecurring">
            <button class="btn btn-outline-primary btn-sm me-1" @click="edit('single')">
              編輯這一筆
            </button>
            <button class="btn btn-outline-primary btn-sm me-3" @click="edit('series')">
              編輯整個系列
            </button>
            <button class="btn btn-outline-danger btn-sm me-1" @click="cancel('single')">
              取消這一筆
            </button>
            <button class="btn btn-outline-danger btn-sm" @click="cancel('series')">
              取消整個系列
            </button>
          </template>
          <template v-else>
            <button class="btn btn-outline-primary btn-sm me-1" @click="edit('series')">編輯</button>
            <button class="btn btn-outline-danger btn-sm" @click="cancel('series')">取消預約</button>
          </template>
        </div>
      </div>
    </div>
  </div>
</div>`,
  };
})();
```

- [ ] **步驟 4：在 Layout 載入這三支 js**

在 `_Layout.cshtml` 的 `<script src="~/js/app/api.js"></script>` 之後、`RenderSectionAsync` 之前加入：

```html
@if (isAuthenticated)
{
    <script src="~/js/app/recurrence-editor.js"></script>
    <script src="~/js/app/event-modal.js"></script>
    <script src="~/js/app/event-detail-modal.js"></script>
}
```

- [ ] **步驟 5：驗證**

這三個元件要等任務 17 掛上頁面才看得到。此處先做語法驗證：

```bash
node --check src/OfficeCal.Web/wwwroot/js/app/recurrence-editor.js
node --check src/OfficeCal.Web/wwwroot/js/app/event-modal.js
node --check src/OfficeCal.Web/wwwroot/js/app/event-detail-modal.js
```

預期：無輸出（語法正確）。若機器上沒有 Node，改在瀏覽器主控台確認 `window.RecurrenceEditor`、`window.EventModal`、`window.EventDetailModal` 都是物件、且沒有語法錯誤。

- [ ] **步驟 6：Commit**

```bash
git add -A
git commit -m "feat: 新增結構化重複設定器與事件建立／編輯／明細彈窗元件"
```

---

### 任務 17：我的行事曆（月／週／日）

**文件：**
- 創建：`src/OfficeCal.Web/wwwroot/js/app/calendar.js`
- 修改：`src/OfficeCal.Web/Pages/Index.cshtml`（全文替換）

- [ ] **步驟 1：寫行事曆頁元件**

`src/OfficeCal.Web/wwwroot/js/app/calendar.js`：

```js
// 我的行事曆：月／週／日三種檢視。資料一律讀 occurrence（scope=me）。
(function () {
  const HOUR_H = 44;   // 與 site.css 的 .oc-hour-row 高度一致
  const DAY_NAMES = ['日', '一', '二', '三', '四', '五', '六'];

  function startOfDay(d) { return new Date(d.getFullYear(), d.getMonth(), d.getDate()); }
  function addDays(d, n) { const x = new Date(d); x.setDate(x.getDate() + n); return x; }
  function startOfWeek(d) { return addDays(startOfDay(d), -d.getDay()); }
  function sameDay(a, b) {
    return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth()
        && a.getDate() === b.getDate();
  }

  window.CalendarPage = {
    components: { EventModal: window.EventModal, EventDetailModal: window.EventDetailModal },
    props: { currentUserId: { type: Number, required: true } },
    data() {
      const params = new URLSearchParams(window.location.search);
      return {
        view: params.get('view') || 'month',
        anchor: params.get('date') ? window.api.parseDate(params.get('date')) : new Date(),
        items: [],
        rooms: [],
        hours: Array.from({ length: 24 }, (_, i) => i),
        dayNames: DAY_NAMES,
        loading: false,
      };
    },
    computed: {
      rangeStart() {
        if (this.view === 'month') return startOfWeek(new Date(this.anchor.getFullYear(),
                                                              this.anchor.getMonth(), 1));
        if (this.view === 'week') return startOfWeek(this.anchor);
        return startOfDay(this.anchor);
      },
      rangeEnd() {
        if (this.view === 'month') return addDays(this.rangeStart, 42);
        if (this.view === 'week') return addDays(this.rangeStart, 7);
        return addDays(this.rangeStart, 1);
      },
      heading() {
        const d = this.anchor;
        if (this.view === 'month') return `${d.getFullYear()} 年 ${d.getMonth() + 1} 月`;
        if (this.view === 'week') {
          const s = this.rangeStart, e = addDays(this.rangeStart, 6);
          return `${s.getFullYear()}/${s.getMonth() + 1}/${s.getDate()}`
               + ` – ${e.getMonth() + 1}/${e.getDate()}`;
        }
        return `${d.getFullYear()}/${d.getMonth() + 1}/${d.getDate()}（${DAY_NAMES[d.getDay()]}）`;
      },
      monthCells() {
        return Array.from({ length: 42 }, (_, i) => addDays(this.rangeStart, i));
      },
      weekCells() {
        const n = this.view === 'week' ? 7 : 1;
        return Array.from({ length: n }, (_, i) => addDays(this.rangeStart, i));
      },
    },
    mounted() {
      window.api.get('/api/v1/rooms').then((r) => { this.rooms = r; });
      this.load();
    },
    methods: {
      async load() {
        this.loading = true;
        try {
          this.items = await window.api.get('/api/v1/events', {
            params: {
              from: window.api.toLocalIso(this.rangeStart),
              to: window.api.toLocalIso(this.rangeEnd),
              scope: 'me',
            },
          });
        } finally {
          this.loading = false;
        }
      },
      setView(v) { this.view = v; this.load(); },
      move(step) {
        const d = new Date(this.anchor);
        if (this.view === 'month') d.setMonth(d.getMonth() + step);
        else if (this.view === 'week') d.setDate(d.getDate() + step * 7);
        else d.setDate(d.getDate() + step);
        this.anchor = d;
        this.load();
      },
      goToday() { this.anchor = new Date(); this.load(); },

      isToday(d) { return sameDay(d, new Date()); },
      isOtherMonth(d) { return d.getMonth() !== this.anchor.getMonth(); },
      itemsOn(day) {
        return this.items.filter((o) => sameDay(new Date(o.startAt), day))
                         .sort((a, b) => a.startAt.localeCompare(b.startAt));
      },
      timeLabel(iso) { return window.api.fmtDateTime(iso).split(' ')[1]; },

      slotStyle(o) {
        const s = new Date(o.startAt), e = new Date(o.endAt);
        const top = (s.getHours() + s.getMinutes() / 60) * HOUR_H;
        const height = Math.max(18, ((e - s) / 3600000) * HOUR_H - 2);
        return { top: top + 'px', height: height + 'px' };
      },

      /** 點空白格快速建立：月檢視給整點 09:00，週／日檢視依點擊位置取整點。 */
      quickCreate(day, hour) {
        const start = new Date(day.getFullYear(), day.getMonth(), day.getDate(),
                               hour == null ? 9 : hour, 0, 0);
        this.$refs.editor.openCreate(start, new Date(start.getTime() + 3600000), null);
      },
      openDetail(o) { this.$refs.detail.open(o); },
      onEdit(payload) { this.$refs.editor.openEdit(payload.occurrence, payload.scope); },
    },
    template: `
<div>
  <div class="d-flex flex-wrap align-items-center gap-2 mb-3">
    <div class="btn-group">
      <button class="btn btn-outline-secondary btn-sm" @click="move(-1)">‹</button>
      <button class="btn btn-outline-secondary btn-sm" @click="goToday">今天</button>
      <button class="btn btn-outline-secondary btn-sm" @click="move(1)">›</button>
    </div>
    <h1 class="h5 mb-0 ms-2">{{ heading }}</h1>
    <div class="ms-auto btn-group">
      <button class="btn btn-sm" :class="view==='month' ? 'btn-primary':'btn-outline-primary'"
              @click="setView('month')">月</button>
      <button class="btn btn-sm" :class="view==='week' ? 'btn-primary':'btn-outline-primary'"
              @click="setView('week')">週</button>
      <button class="btn btn-sm" :class="view==='day' ? 'btn-primary':'btn-outline-primary'"
              @click="setView('day')">日</button>
    </div>
    <button class="btn btn-primary btn-sm" @click="quickCreate(anchor, null)">＋ 新增事件</button>
  </div>

  <div class="card-oc p-0 overflow-hidden">
    <!-- 月檢視 -->
    <template v-if="view === 'month'">
      <div class="d-grid" style="grid-template-columns: repeat(7,1fr)">
        <div v-for="n in dayNames" :key="n" class="text-center small text-muted py-2">{{ n }}</div>
      </div>
      <div class="oc-month">
        <div v-for="d in monthCells" :key="d.toISOString()"
             class="oc-month-cell"
             :class="{ 'is-other-month': isOtherMonth(d), 'is-today': isToday(d) }"
             @click="quickCreate(d, null)">
          <div class="small fw-semibold mb-1">{{ d.getDate() }}</div>
          <span v-for="o in itemsOn(d)" :key="o.occurrenceId"
                class="oc-chip" :class="{ 'is-room': o.roomId }"
                :title="o.title"
                @click.stop="openDetail(o)">
            {{ o.isAllDay ? '全天' : timeLabel(o.startAt) }} {{ o.title }}
          </span>
        </div>
      </div>
    </template>

    <!-- 週／日檢視 -->
    <template v-else>
      <div class="d-flex border-bottom">
        <div style="width:56px"></div>
        <div v-for="d in weekCells" :key="d.toISOString()"
             class="flex-fill text-center small py-2"
             :class="{ 'fw-bold text-primary': isToday(d) }">
          {{ d.getMonth() + 1 }}/{{ d.getDate() }}（{{ dayNames[d.getDay()] }}）
        </div>
      </div>
      <div class="d-flex" style="max-height:620px; overflow-y:auto">
        <div style="width:56px">
          <div v-for="h in hours" :key="h" class="oc-hour-row text-end pe-2 small text-muted">
            {{ String(h).padStart(2,'0') }}:00
          </div>
        </div>
        <div v-for="d in weekCells" :key="d.toISOString()" class="flex-fill oc-grid">
          <div v-for="h in hours" :key="h" class="oc-hour-row" @click="quickCreate(d, h)"></div>
          <div v-for="o in itemsOn(d)" :key="o.occurrenceId"
               class="oc-slot" :style="slotStyle(o)" @click.stop="openDetail(o)">
            <div class="fw-semibold">{{ o.title }}</div>
            <div class="opacity-75">{{ o.roomName || '個人事件' }}</div>
          </div>
        </div>
      </div>
    </template>
  </div>

  <div class="text-muted small mt-2" v-if="loading">載入中…</div>

  <event-modal ref="editor" :rooms="rooms" :current-user-id="currentUserId"
               @saved="load"></event-modal>
  <event-detail-modal ref="detail" @edit="onEdit" @changed="load"></event-detail-modal>
</div>`,
  };
})();
```

- [ ] **步驟 2：把元件掛到首頁**

`src/OfficeCal.Web/Pages/Index.cshtml`（全文替換）：

```html
@page
@using System.Security.Claims
@model OfficeCal.Web.Pages.IndexModel
@{
    ViewData["Title"] = "我的行事曆";
    var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
}
<div class="container-fluid px-4 py-4">
    <div id="calendar-app">
        <calendar-page :current-user-id="@userId"></calendar-page>
    </div>
</div>

@section Scripts {
<script src="~/js/app/calendar.js"></script>
<script>
    Vue.createApp({ components: { CalendarPage: window.CalendarPage } }).mount('#calendar-app');
</script>
}
```

- [ ] **步驟 3：驗證（跑起來點一遍）**

```bash
dotnet run --project src/OfficeCal.Web
```

以 `A0001` / `Admin@12345` 登入後，逐項確認：

1. 預設看到當月的月檢視，星期列與日期格對齊
2. 點某個空白日期格 → 開啟建立彈窗，開始時間是該日 09:00
3. 標題填「專案啟動會議」、會議廳選「A 棟 3F 大會議廳」、儲存 → 綠色成功提示，格子上出現事件
4. 切到「週」→ 該事件出現在正確的星期與時段上（垂直位置對得上左側時間軸）
5. 切到「日」→ 同樣正確
6. 再次在**同一會議廳、同一時段**建立事件 → 跳出 SweetAlert2 表格，列出會議廳、時段、事件、預約人，且事件沒有被建立
7. 建立一個重複事件：頻率「每週」、星期自動勾選起始日的星期、結束條件選「重複次數 4」→ 儲存後月檢視上出現四筆
8. 點其中一筆 → 明細彈窗顯示時間、會議廳、預約人、與會者，並有「編輯這一筆／編輯整個系列／取消這一筆／取消整個系列」四個按鈕
9. 「編輯這一筆」→ 會議廳下拉是停用狀態且有說明文字；改時間後儲存 → 只有那一筆變動
10. 「編輯整個系列」→ 改時間後儲存 → 先前單獨修改的那一筆維持原樣，其餘各次改變
11. 「下載 .ics」→ 瀏覽器下載檔案，用文字編輯器開啟看得到 `BEGIN:VCALENDAR` 與 `TZID:Asia/Taipei`

- [ ] **步驟 4：Commit**

```bash
git add -A
git commit -m "feat: 新增我的行事曆頁，支援月／週／日三種檢視"
```

---

### 任務 18：會議廳資源時間軸

規格 §8：**橫軸為時間、縱軸為各會議廳**的當日甘特式檢視；點空白區塊直接帶入該會議廳與時段開始預約。

**文件：**
- 創建：`src/OfficeCal.Web/wwwroot/js/app/timeline.js`
- 創建：`src/OfficeCal.Web/Pages/Rooms.cshtml` + `.cshtml.cs`

- [ ] **步驟 1：寫時間軸元件**

`src/OfficeCal.Web/wwwroot/js/app/timeline.js`：

```js
// 會議廳資源時間軸：橫軸時間、縱軸會議廳，一眼看出空檔。
(function () {
  const DAY_MINUTES = 24 * 60;

  window.TimelinePage = {
    components: { EventModal: window.EventModal, EventDetailModal: window.EventDetailModal },
    props: { currentUserId: { type: Number, required: true } },
    data() {
      const today = new Date();
      return {
        date: window.api.toLocalIso(today).slice(0, 10),
        capacity: null,
        rows: [],
        rooms: [],
        hours: Array.from({ length: 24 }, (_, i) => i),
        loading: false,
      };
    },
    mounted() {
      window.api.get('/api/v1/rooms').then((r) => { this.rooms = r; });
      this.load();
    },
    methods: {
      async load() {
        this.loading = true;
        try {
          this.rows = await window.api.get('/api/v1/rooms/availability', {
            params: { date: this.date, capacity: this.capacity || null },
          });
        } finally {
          this.loading = false;
        }
      },
      shiftDay(step) {
        const d = window.api.parseDate(this.date);
        d.setDate(d.getDate() + step);
        this.date = window.api.toLocalIso(d).slice(0, 10);
        this.load();
      },
      minutesOf(iso) {
        const d = new Date(iso);
        return d.getHours() * 60 + d.getMinutes();
      },
      slotStyle(b) {
        const start = Math.max(0, this.minutesOf(b.startAt));
        const end = Math.min(DAY_MINUTES, this.minutesOf(b.endAt) || DAY_MINUTES);
        return {
          left: (start / DAY_MINUTES * 100) + '%',
          width: (Math.max(15, end - start) / DAY_MINUTES * 100) + '%',
        };
      },
      /** 點空白區塊：依點擊的水平位置換算成整點，帶入該會議廳開始預約。 */
      quickBook(row, ev) {
        const rect = ev.currentTarget.getBoundingClientRect();
        const ratio = Math.min(0.99, Math.max(0, (ev.clientX - rect.left) / rect.width));
        const hour = Math.floor(ratio * 24);
        const d = window.api.parseDate(this.date);
        const start = new Date(d.getFullYear(), d.getMonth(), d.getDate(), hour, 0, 0);
        this.$refs.editor.openCreate(start, new Date(start.getTime() + 3600000), row.roomId);
      },
      openDetail(b, row) {
        this.$refs.detail.open({
          occurrenceId: b.occurrenceId, eventId: b.eventId, title: b.title,
          startAt: b.startAt, endAt: b.endAt, roomId: row.roomId, roomName: row.name,
        });
      },
      onEdit(payload) { this.$refs.editor.openEdit(payload.occurrence, payload.scope); },
    },
    template: `
<div>
  <div class="d-flex flex-wrap align-items-center gap-2 mb-3">
    <div class="btn-group">
      <button class="btn btn-outline-secondary btn-sm" @click="shiftDay(-1)">‹</button>
      <button class="btn btn-outline-secondary btn-sm" @click="shiftDay(1)">›</button>
    </div>
    <input type="date" class="form-control form-control-sm" style="width:auto"
           v-model="date" @change="load" />
    <div class="input-group input-group-sm" style="width:210px">
      <span class="input-group-text">最少可容納</span>
      <input type="number" min="1" class="form-control" v-model.number="capacity"
             @change="load" placeholder="不限" />
      <span class="input-group-text">人</span>
    </div>
    <span class="text-muted small ms-2" v-if="loading">載入中…</span>
  </div>

  <div class="card-oc p-3">
    <div class="d-flex">
      <div style="width:190px"></div>
      <div class="flex-fill oc-timeline-head">
        <div v-for="h in hours" :key="h" class="flex-fill">
          {{ h % 2 === 0 ? String(h).padStart(2,'0') : '' }}
        </div>
      </div>
    </div>

    <div v-for="row in rows" :key="row.roomId" class="d-flex align-items-stretch">
      <div style="width:190px" class="pe-2 py-2 border-end">
        <div class="fw-semibold small">{{ row.name }}</div>
        <div class="text-muted" style="font-size:.72rem">
          {{ row.capacity }} 人{{ row.location ? '・' + row.location : '' }}
        </div>
      </div>
      <div class="flex-fill oc-timeline-row" @click="quickBook(row, $event)"
           :title="'點一下即可預約 ' + row.name">
        <div v-for="b in row.busy" :key="b.occurrenceId"
             class="oc-timeline-slot" :style="slotStyle(b)"
             :title="b.title + '（' + b.ownerName + '）'"
             @click.stop="openDetail(b, row)">
          {{ b.title }}
        </div>
      </div>
    </div>

    <div class="text-muted small py-4 text-center" v-if="!rows.length && !loading">
      沒有符合條件的會議廳。
    </div>
  </div>

  <event-modal ref="editor" :rooms="rooms" :current-user-id="currentUserId"
               @saved="load"></event-modal>
  <event-detail-modal ref="detail" @edit="onEdit" @changed="load"></event-detail-modal>
</div>`,
  };
})();
```

- [ ] **步驟 2：寫頁面**

`src/OfficeCal.Web/Pages/Rooms.cshtml.cs`：

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OfficeCal.Web.Pages;

[Authorize]
public class RoomsModel : PageModel
{
    public void OnGet() { }
}
```

`src/OfficeCal.Web/Pages/Rooms.cshtml`：

```html
@page
@using System.Security.Claims
@model OfficeCal.Web.Pages.RoomsModel
@{
    ViewData["Title"] = "會議廳時間軸";
    var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
}
<div class="container-fluid px-4 py-4">
    <h1 class="h5 mb-3">會議廳資源時間軸</h1>
    <div id="timeline-app">
        <timeline-page :current-user-id="@userId"></timeline-page>
    </div>
</div>

@section Scripts {
<script src="~/js/app/timeline.js"></script>
<script>
    Vue.createApp({ components: { TimelinePage: window.TimelinePage } }).mount('#timeline-app');
</script>
}
```

- [ ] **步驟 3：驗證**

```bash
dotnet run --project src/OfficeCal.Web
```

1. 開 `/Rooms` → 每間會議廳一列，橫軸 0–23 時
2. 前一天／後一天按鈕、日期選擇器都能重新載入
3. 「最少可容納」填 20 → 只剩容量 ≥ 20 的會議廳
4. 任務 17 建立的預約以藍色色塊出現在正確的水平位置（例如 10:00–11:00 大約在 42%–46%）
5. 點某列的空白處 → 建立彈窗開啟，且**會議廳已自動帶入該列**、時間為點擊處的整點
6. 直接送出 → 時間軸重新載入並出現新色塊
7. 點色塊 → 明細彈窗顯示該預約，且他人的預約也看得到（資源排程透明）

- [ ] **步驟 4：Commit**

```bash
git add -A
git commit -m "feat: 新增會議廳資源時間軸頁"
```

---

### 任務 19：通知中心與個人設定

**文件：**
- 創建：`src/OfficeCal.Web/wwwroot/js/app/notifications.js`
- 創建：`src/OfficeCal.Web/wwwroot/js/app/settings.js`
- 創建：`src/OfficeCal.Web/Pages/Settings.cshtml` + `.cshtml.cs`
- 修改：`src/OfficeCal.Web/Pages/Shared/_Layout.cshtml`（掛載通知中心）

- [ ] **步驟 1：寫通知中心**

`src/OfficeCal.Web/wwwroot/js/app/notifications.js`：

```js
// 導覽列的通知中心：未讀紅點 + 下拉清單，點擊跳至該事件所在的日檢視。
(function () {
  window.NotificationCenter = {
    data() { return { items: [], unread: 0, open: false, loading: false }; },
    mounted() {
      this.load();
      // 每 60 秒輪詢一次未讀數；規格不做推播，輪詢已足夠。
      setInterval(this.load, 60000);
      document.addEventListener('click', this.onDocumentClick);
    },
    beforeUnmount() { document.removeEventListener('click', this.onDocumentClick); },
    methods: {
      onDocumentClick(e) { if (!this.$el.contains(e.target)) this.open = false; },
      async load() {
        const data = await window.api.get('/api/v1/notifications', { params: { take: 20 } });
        this.items = data.items;
        this.unread = data.unreadCount;
      },
      fmt(iso) { return window.api.fmtDateTime(iso); },
      async click(n) {
        if (!n.isRead) {
          await window.api.post(`/api/v1/notifications/${n.id}/read`);
          n.isRead = true;
          this.unread = Math.max(0, this.unread - 1);
        }
        if (!n.eventId) return;
        try {
          const detail = await window.api.get('/api/v1/events/' + n.eventId);
          const date = detail.startAt.slice(0, 10);
          window.location.href = `/?view=day&date=${date}`;
        } catch (e) {
          window.location.href = '/';   // 事件已被刪除或沒有權限，回行事曆
        }
      },
    },
    template: `
<div class="position-relative">
  <button class="btn btn-link text-decoration-none position-relative p-1"
          @click.stop="open = !open" title="通知">
    <span style="font-size:1.25rem">🔔</span>
    <span v-if="unread > 0"
          class="position-absolute top-0 start-100 translate-middle badge rounded-pill text-bg-danger">
      {{ unread > 99 ? '99+' : unread }}
    </span>
  </button>
  <div v-show="open" class="card card-oc shadow position-absolute end-0 mt-1"
       style="width:340px; max-height:420px; overflow:auto; z-index:1050">
    <div class="p-2 border-bottom small fw-semibold">通知</div>
    <div v-if="!items.length" class="p-3 text-muted small text-center">目前沒有通知</div>
    <a v-for="n in items" :key="n.id" href="#"
       class="d-block px-3 py-2 border-bottom text-decoration-none"
       :class="n.isRead ? 'text-muted' : 'fw-semibold text-dark'"
       @click.prevent="click(n)">
      <div class="small">{{ n.message }}</div>
      <div style="font-size:.72rem" class="text-muted">{{ fmt(n.createdAt) }}</div>
    </a>
  </div>
</div>`,
  };
})();
```

在 `_Layout.cshtml` 的登入者 script 區塊補上：

```html
    <script src="~/js/app/notifications.js"></script>
    <script>
        Vue.createApp({ components: { NotificationCenter: window.NotificationCenter },
                        template: '<notification-center></notification-center>' })
           .mount('#notification-center');
    </script>
```

- [ ] **步驟 2：寫個人設定頁**

`src/OfficeCal.Web/wwwroot/js/app/settings.js`：

```js
(function () {
  window.SettingsPage = {
    data() {
      return {
        me: null,
        pwd: { currentPassword: '', newPassword: '', confirm: '' },
        saving: false,
      };
    },
    mounted() { window.api.get('/api/v1/me').then((m) => { this.me = m; }); },
    methods: {
      async copyFeed() {
        try {
          await navigator.clipboard.writeText(this.me.feedUrl);
          Swal.fire({ icon: 'success', title: '已複製訂閱網址', timer: 1200,
                      showConfirmButton: false });
        } catch (e) {
          Swal.fire('請手動複製', this.me.feedUrl, 'info');
        }
      },
      async resetToken() {
        const ok = await Swal.fire({
          icon: 'warning', title: '重新產生訂閱網址？',
          text: '舊網址會立刻失效，已訂閱的行事曆軟體需要重新加入。',
          showCancelButton: true, confirmButtonText: '重新產生', cancelButtonText: '取消',
        });
        if (!ok.isConfirmed) return;
        const data = await window.api.post('/api/v1/me/reset-feed-token');
        this.me.feedUrl = data.feedUrl;
        Swal.fire({ icon: 'success', title: '已重新產生', timer: 1400, showConfirmButton: false });
      },
      async changePassword() {
        if (this.pwd.newPassword !== this.pwd.confirm) {
          Swal.fire('兩次輸入的新密碼不一致', '', 'info');
          return;
        }
        this.saving = true;
        try {
          await window.api.post('/api/v1/me/change-password', {
            currentPassword: this.pwd.currentPassword,
            newPassword: this.pwd.newPassword,
          });
          this.pwd = { currentPassword: '', newPassword: '', confirm: '' };
          Swal.fire({ icon: 'success', title: '密碼已更新', timer: 1400, showConfirmButton: false });
        } catch (e) {
          // 攔截器已顯示訊息
        } finally {
          this.saving = false;
        }
      },
    },
    template: `
<div class="row g-4" v-if="me">
  <div class="col-lg-6">
    <div class="card-oc p-4 h-100">
      <h2 class="h6 mb-3">個人資料</h2>
      <dl class="row small mb-0">
        <dt class="col-4">員工編號</dt><dd class="col-8">{{ me.employeeNo }}</dd>
        <dt class="col-4">姓名</dt><dd class="col-8">{{ me.displayName }}</dd>
        <dt class="col-4">Email</dt><dd class="col-8">{{ me.email }}</dd>
        <dt class="col-4">部門</dt><dd class="col-8">{{ me.departmentName || '未指定' }}</dd>
        <dt class="col-4">角色</dt>
        <dd class="col-8">{{ me.isAdmin ? '系統管理員' : '一般員工' }}</dd>
      </dl>

      <hr />
      <h2 class="h6 mb-2">訂閱行事曆</h2>
      <p class="small text-muted">
        把下面的網址加進 Outlook 或 Google 行事曆的「訂閱」功能，即可自動同步你的行程。
      </p>
      <div class="input-group input-group-sm mb-2">
        <input class="form-control" :value="me.feedUrl" readonly />
        <button class="btn btn-outline-secondary" @click="copyFeed">複製</button>
      </div>
      <button class="btn btn-outline-danger btn-sm" @click="resetToken">重新產生訂閱網址</button>
    </div>
  </div>

  <div class="col-lg-6">
    <div class="card-oc p-4 h-100">
      <h2 class="h6 mb-3">修改密碼</h2>
      <form @submit.prevent="changePassword">
        <div class="mb-3">
          <label class="form-label small">目前密碼</label>
          <input type="password" class="form-control" v-model="pwd.currentPassword" />
        </div>
        <div class="mb-3">
          <label class="form-label small">新密碼（至少 8 個字元）</label>
          <input type="password" class="form-control" v-model="pwd.newPassword" />
        </div>
        <div class="mb-3">
          <label class="form-label small">再輸入一次新密碼</label>
          <input type="password" class="form-control" v-model="pwd.confirm" />
        </div>
        <button class="btn btn-primary" :disabled="saving">
          {{ saving ? '更新中…' : '更新密碼' }}
        </button>
      </form>
    </div>
  </div>
</div>`,
  };
})();
```

`src/OfficeCal.Web/Pages/Settings.cshtml.cs`：

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OfficeCal.Web.Pages;

[Authorize]
public class SettingsModel : PageModel
{
    public void OnGet() { }
}
```

`src/OfficeCal.Web/Pages/Settings.cshtml`：

```html
@page
@model OfficeCal.Web.Pages.SettingsModel
@{ ViewData["Title"] = "個人設定"; }
<div class="container py-4">
    <h1 class="h5 mb-3">個人設定</h1>
    <div id="settings-app"><settings-page></settings-page></div>
</div>

@section Scripts {
<script src="~/js/app/settings.js"></script>
<script>
    Vue.createApp({ components: { SettingsPage: window.SettingsPage } }).mount('#settings-app');
</script>
}
```

- [ ] **步驟 3：驗證**

```bash
dotnet run --project src/OfficeCal.Web
```

1. 用管理員帳號把某員工加為某事件的與會者 → 以該員工登入 → 導覽列鈴鐺有紅點
2. 點鈴鐺 → 下拉列出「XXX 邀請你參加 9/7 10:00 的「…」」
3. 點該則通知 → 紅點數字減一，頁面跳到該事件當天的日檢視
4. 改期該事件 → 該員工再次收到「已改期至 …」的通知
5. 開 `/Settings` → 看得到訂閱網址；按「複製」有成功提示
6. 把訂閱網址貼到瀏覽器新分頁（**不帶登入狀態的無痕視窗**）→ 直接下載 .ics
7. 按「重新產生訂閱網址」→ 舊網址在無痕視窗重新整理後變成 404，新網址可用
8. 修改密碼：舊密碼填錯 → 顯示「目前密碼不正確」；填對 → 成功，登出後用新密碼可登入

- [ ] **步驟 4：Commit**

```bash
git add -A
git commit -m "feat: 新增通知中心與個人設定頁"
```

---

### 任務 20：管理員後台（會議廳管理、員工管理）

**文件：**
- 創建：`src/OfficeCal.Web/wwwroot/js/app/admin-rooms.js`
- 創建：`src/OfficeCal.Web/wwwroot/js/app/admin-users.js`
- 創建：`src/OfficeCal.Web/Pages/Admin/Rooms.cshtml` + `.cshtml.cs`
- 創建：`src/OfficeCal.Web/Pages/Admin/Users.cshtml` + `.cshtml.cs`

- [ ] **步驟 1：寫會議廳管理**

`src/OfficeCal.Web/wwwroot/js/app/admin-rooms.js`：

```js
(function () {
  window.AdminRoomsPage = {
    data() {
      return { rooms: [], editing: null, saving: false, modal: null };
    },
    mounted() { this.modal = new bootstrap.Modal(this.$refs.dialog); this.load(); },
    methods: {
      async load() {
        this.rooms = await window.api.get('/api/v1/rooms', { params: { includeInactive: true } });
      },
      blank() {
        return { id: null, name: '', location: '', capacity: 10, equipment: '', isActive: true };
      },
      openCreate() { this.editing = this.blank(); this.modal.show(); },
      openEdit(r) { this.editing = Object.assign({}, r); this.modal.show(); },
      async save() {
        const body = {
          name: this.editing.name, location: this.editing.location,
          capacity: Number(this.editing.capacity), equipment: this.editing.equipment,
          isActive: this.editing.isActive,
        };
        this.saving = true;
        try {
          if (this.editing.id) await window.api.put('/api/v1/rooms/' + this.editing.id, body);
          else await window.api.post('/api/v1/rooms', body);
          this.modal.hide();
          await this.load();
          Swal.fire({ icon: 'success', title: '已儲存', timer: 1200, showConfirmButton: false });
        } catch (e) {
          // 攔截器已顯示訊息（例如名稱重複）
        } finally {
          this.saving = false;
        }
      },
      async toggleActive(r) {
        const next = !r.isActive;
        const ok = await Swal.fire({
          icon: 'question',
          title: next ? '要啟用這間會議廳嗎？' : '要停用這間會議廳嗎？',
          text: next ? '啟用後可以再被預約。' : '停用後不可新增預約，既有預約仍會保留。',
          showCancelButton: true, confirmButtonText: '確定', cancelButtonText: '取消',
        });
        if (!ok.isConfirmed) return;
        await window.api.put('/api/v1/rooms/' + r.id, {
          name: r.name, location: r.location, capacity: r.capacity,
          equipment: r.equipment, isActive: next,
        });
        await this.load();
      },
    },
    template: `
<div>
  <div class="d-flex align-items-center mb-3">
    <h1 class="h5 mb-0">會議廳管理</h1>
    <button class="btn btn-primary btn-sm ms-auto" @click="openCreate">＋ 新增會議廳</button>
  </div>

  <div class="card-oc p-0">
    <table class="table table-hover align-middle mb-0">
      <thead class="table-light">
        <tr><th>名稱</th><th>位置</th><th class="text-end">容納人數</th><th>設備</th>
            <th>狀態</th><th class="text-end">操作</th></tr>
      </thead>
      <tbody>
        <tr v-for="r in rooms" :key="r.id">
          <td class="fw-semibold">{{ r.name }}</td>
          <td class="text-muted small">{{ r.location }}</td>
          <td class="text-end">{{ r.capacity }}</td>
          <td class="text-muted small">{{ r.equipment }}</td>
          <td>
            <span class="badge" :class="r.isActive ? 'text-bg-success' : 'text-bg-secondary'">
              {{ r.isActive ? '啟用中' : '已停用' }}
            </span>
          </td>
          <td class="text-end">
            <button class="btn btn-sm btn-outline-primary me-1" @click="openEdit(r)">編輯</button>
            <button class="btn btn-sm" :class="r.isActive ? 'btn-outline-danger' : 'btn-outline-success'"
                    @click="toggleActive(r)">{{ r.isActive ? '停用' : '啟用' }}</button>
          </td>
        </tr>
      </tbody>
    </table>
  </div>

  <div class="modal fade" tabindex="-1" ref="dialog">
    <div class="modal-dialog">
      <div class="modal-content" v-if="editing">
        <div class="modal-header">
          <h5 class="modal-title">{{ editing.id ? '編輯會議廳' : '新增會議廳' }}</h5>
          <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
        </div>
        <div class="modal-body">
          <div class="mb-3">
            <label class="form-label">名稱</label>
            <input class="form-control" v-model.trim="editing.name" maxlength="50" />
          </div>
          <div class="mb-3">
            <label class="form-label">位置</label>
            <input class="form-control" v-model.trim="editing.location" maxlength="100" />
          </div>
          <div class="mb-3">
            <label class="form-label">容納人數</label>
            <input type="number" min="1" max="1000" class="form-control" v-model.number="editing.capacity" />
          </div>
          <div class="mb-3">
            <label class="form-label">設備</label>
            <input class="form-control" v-model.trim="editing.equipment" maxlength="200"
                   placeholder="投影機、視訊設備…" />
          </div>
          <div class="form-check">
            <input class="form-check-input" type="checkbox" id="room-active" v-model="editing.isActive" />
            <label class="form-check-label" for="room-active">啟用中</label>
          </div>
        </div>
        <div class="modal-footer">
          <button class="btn btn-outline-secondary" data-bs-dismiss="modal">取消</button>
          <button class="btn btn-primary" :disabled="saving" @click="save">儲存</button>
        </div>
      </div>
    </div>
  </div>
</div>`,
  };
})();
```

- [ ] **步驟 2：寫員工管理**

`src/OfficeCal.Web/wwwroot/js/app/admin-users.js`：

```js
(function () {
  window.AdminUsersPage = {
    data() {
      return { users: [], departments: [], editing: null, saving: false, modal: null };
    },
    mounted() {
      this.modal = new bootstrap.Modal(this.$refs.dialog);
      window.api.get('/api/v1/departments').then((d) => { this.departments = d; });
      this.load();
    },
    methods: {
      async load() { this.users = await window.api.get('/api/v1/users'); },
      blank() {
        return {
          id: null, employeeNo: '', displayName: '', email: '',
          departmentId: null, role: 'Employee', isActive: true, password: '',
        };
      },
      openCreate() { this.editing = this.blank(); this.modal.show(); },
      openEdit(u) { this.editing = Object.assign(this.blank(), u); this.modal.show(); },
      async save() {
        this.saving = true;
        try {
          if (this.editing.id) {
            await window.api.put('/api/v1/users/' + this.editing.id, {
              displayName: this.editing.displayName, email: this.editing.email,
              departmentId: this.editing.departmentId, role: this.editing.role,
              isActive: this.editing.isActive,
            });
          } else {
            await window.api.post('/api/v1/users', {
              employeeNo: this.editing.employeeNo, displayName: this.editing.displayName,
              email: this.editing.email, departmentId: this.editing.departmentId,
              role: this.editing.role, password: this.editing.password,
            });
          }
          this.modal.hide();
          await this.load();
          Swal.fire({ icon: 'success', title: '已儲存', timer: 1200, showConfirmButton: false });
        } catch (e) {
          // 攔截器已顯示訊息
        } finally {
          this.saving = false;
        }
      },
      async resetPassword(u) {
        const result = await Swal.fire({
          title: `重設 ${u.displayName} 的密碼`,
          input: 'password',
          inputLabel: '新密碼（至少 8 個字元）',
          showCancelButton: true, confirmButtonText: '重設', cancelButtonText: '取消',
        });
        if (!result.isConfirmed || !result.value) return;
        await window.api.post(`/api/v1/users/${u.id}/reset-password`, { newPassword: result.value });
        Swal.fire({ icon: 'success', title: '已重設密碼', timer: 1400, showConfirmButton: false });
      },
      async toggleActive(u) {
        await window.api.put('/api/v1/users/' + u.id, {
          displayName: u.displayName, email: u.email, departmentId: u.departmentId,
          role: u.role, isActive: !u.isActive,
        });
        await this.load();
      },
    },
    template: `
<div>
  <div class="d-flex align-items-center mb-3">
    <h1 class="h5 mb-0">員工管理</h1>
    <button class="btn btn-primary btn-sm ms-auto" @click="openCreate">＋ 新增帳號</button>
  </div>

  <div class="card-oc p-0">
    <table class="table table-hover align-middle mb-0">
      <thead class="table-light">
        <tr><th>員工編號</th><th>姓名</th><th>Email</th><th>部門</th><th>角色</th>
            <th>狀態</th><th class="text-end">操作</th></tr>
      </thead>
      <tbody>
        <tr v-for="u in users" :key="u.id">
          <td>{{ u.employeeNo }}</td>
          <td class="fw-semibold">{{ u.displayName }}</td>
          <td class="small text-muted">{{ u.email }}</td>
          <td class="small">{{ u.departmentName || '—' }}</td>
          <td>
            <span class="badge" :class="u.role === 'Admin' ? 'text-bg-primary' : 'text-bg-light'">
              {{ u.role === 'Admin' ? '系統管理員' : '一般員工' }}
            </span>
          </td>
          <td>
            <span class="badge" :class="u.isActive ? 'text-bg-success' : 'text-bg-secondary'">
              {{ u.isActive ? '啟用中' : '已停用' }}
            </span>
          </td>
          <td class="text-end text-nowrap">
            <button class="btn btn-sm btn-outline-primary me-1" @click="openEdit(u)">編輯</button>
            <button class="btn btn-sm btn-outline-secondary me-1" @click="resetPassword(u)">
              重設密碼
            </button>
            <button class="btn btn-sm"
                    :class="u.isActive ? 'btn-outline-danger' : 'btn-outline-success'"
                    @click="toggleActive(u)">{{ u.isActive ? '停用' : '啟用' }}</button>
          </td>
        </tr>
      </tbody>
    </table>
  </div>

  <div class="modal fade" tabindex="-1" ref="dialog">
    <div class="modal-dialog">
      <div class="modal-content" v-if="editing">
        <div class="modal-header">
          <h5 class="modal-title">{{ editing.id ? '編輯帳號' : '新增帳號' }}</h5>
          <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
        </div>
        <div class="modal-body">
          <div class="mb-3" v-if="!editing.id">
            <label class="form-label">員工編號</label>
            <input class="form-control" v-model.trim="editing.employeeNo" maxlength="20" />
          </div>
          <div class="mb-3">
            <label class="form-label">姓名</label>
            <input class="form-control" v-model.trim="editing.displayName" maxlength="50" />
          </div>
          <div class="mb-3">
            <label class="form-label">Email</label>
            <input type="email" class="form-control" v-model.trim="editing.email" maxlength="100" />
          </div>
          <div class="mb-3">
            <label class="form-label">部門</label>
            <select class="form-select" v-model="editing.departmentId">
              <option :value="null">未指定</option>
              <option v-for="d in departments" :key="d.id" :value="d.id">{{ d.name }}</option>
            </select>
          </div>
          <div class="mb-3">
            <label class="form-label">角色</label>
            <select class="form-select" v-model="editing.role">
              <option value="Employee">一般員工</option>
              <option value="Admin">系統管理員</option>
            </select>
          </div>
          <div class="mb-3" v-if="!editing.id">
            <label class="form-label">初始密碼（至少 8 個字元）</label>
            <input type="password" class="form-control" v-model="editing.password" />
          </div>
          <div class="form-check" v-if="editing.id">
            <input class="form-check-input" type="checkbox" id="user-active" v-model="editing.isActive" />
            <label class="form-check-label" for="user-active">啟用中</label>
          </div>
        </div>
        <div class="modal-footer">
          <button class="btn btn-outline-secondary" data-bs-dismiss="modal">取消</button>
          <button class="btn btn-primary" :disabled="saving" @click="save">儲存</button>
        </div>
      </div>
    </div>
  </div>
</div>`,
  };
})();
```

- [ ] **步驟 3：寫兩個後台頁面**

`src/OfficeCal.Web/Pages/Admin/Rooms.cshtml.cs`：

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OfficeCal.Web.Pages.Admin;

[Authorize(Policy = "Admin")]
public class RoomsModel : PageModel
{
    public void OnGet() { }
}
```

`src/OfficeCal.Web/Pages/Admin/Rooms.cshtml`：

```html
@page
@model OfficeCal.Web.Pages.Admin.RoomsModel
@{ ViewData["Title"] = "會議廳管理"; }
<div class="container-fluid px-4 py-4">
    <div id="admin-rooms-app"><admin-rooms-page></admin-rooms-page></div>
</div>

@section Scripts {
<script src="~/js/app/admin-rooms.js"></script>
<script>
    Vue.createApp({ components: { AdminRoomsPage: window.AdminRoomsPage } }).mount('#admin-rooms-app');
</script>
}
```

`src/OfficeCal.Web/Pages/Admin/Users.cshtml.cs`：

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OfficeCal.Web.Pages.Admin;

[Authorize(Policy = "Admin")]
public class UsersModel : PageModel
{
    public void OnGet() { }
}
```

`src/OfficeCal.Web/Pages/Admin/Users.cshtml`：

```html
@page
@model OfficeCal.Web.Pages.Admin.UsersModel
@{ ViewData["Title"] = "員工管理"; }
<div class="container-fluid px-4 py-4">
    <div id="admin-users-app"><admin-users-page></admin-users-page></div>
</div>

@section Scripts {
<script src="~/js/app/admin-users.js"></script>
<script>
    Vue.createApp({ components: { AdminUsersPage: window.AdminUsersPage } }).mount('#admin-users-app');
</script>
}
```

- [ ] **步驟 4：驗證**

```bash
dotnet run --project src/OfficeCal.Web
```

1. 以管理員登入 → 導覽列有「會議廳管理」「員工管理」
2. `/Admin/Rooms`：新增一間會議廳 → 清單出現；用相同名稱再新增 → 顯示「已經有名稱為…的會議廳」
3. 停用某間會議廳 → 徽章變成「已停用」；到事件彈窗的會議廳下拉中該間**不再出現**（下拉只取啟用中的）
4. `/Admin/Users`：新增一個一般員工 → 用新帳號登入成功
5. 對該員工按「重設密碼」→ 用新密碼可登入
6. 停用該員工 → 該帳號登入時顯示「員工編號或密碼錯誤」
7. 以一般員工身分直接開 `/Admin/Rooms` → 收到 403（頁面顯示禁止存取）

- [ ] **步驟 5：Commit**

```bash
git add -A
git commit -m "feat: 新增管理員後台的會議廳管理與員工管理頁"
```

---

### 任務 21：端到端驗收

不寫新程式碼，逐條核對規格 §11 的九項驗收標準。有任何一條不通過就回到對應任務修正。

**文件：**
- 創建：`docs/superpowers/plans/2026-08-29-acceptance-log.md`（驗收紀錄）

- [ ] **步驟 1：跑完整測試套件**

```bash
dotnet test
```

預期：全部通過。把輸出的最後幾行（passed / failed / skipped 數字）貼進驗收紀錄。

- [ ] **步驟 2：確認併發驗收**

```bash
dotnet test --filter "FullyQualifiedName~ConcurrentBookingTests"
```

預期：2 passed。這對應驗收標準 5（連續 50 輪皆為「恰好一個成功」）。

- [ ] **步驟 3：逐條走過驗收標準**

啟動站台後依序確認，並把每一條的結果記進 `docs/superpowers/plans/2026-08-29-acceptance-log.md`：

| # | 驗收標準 | 怎麼驗 |
|---|---|---|
| 1 | 員工可登入、建立個人事件、在三種檢視中看到它 | 以一般員工登入 → 建立不指定會議廳的事件 → 月／週／日都看得到 |
| 2 | 重複預約重疊時段會被拒絕並看到衝突明細 | 兩次預約同一會議廳同一時段 → 第二次跳出衝突表格，事件未建立 |
| 3 | 可建立「每月第二個星期三」這類規則，並單獨修改／取消其中一次 | 重複設定器選「每月」「第 N 個星期 X」→ 建立 → 對其中一次用「編輯這一筆」改時間、對另一次用「取消這一筆」 |
| 4 | 修改整個系列後，先前被單獨修改／取消的那幾次仍維持原樣 | 接續第 3 條 → 「編輯整個系列」改時間 → 確認被單獨處理的那兩次沒有被覆蓋 |
| 5 | 併發測試連續 50 輪皆為「恰好一個成功」 | 步驟 2 |
| 6 | 與會者收到站內通知；改期或取消時再次收到 | 建立事件時加入與會者 → 以該員工登入看鈴鐺 → 改期、取消後再看 |
| 7 | 訂閱 feed 可被 Outlook 訂閱並正確顯示時間；重新產生 token 後舊網址失效 | 把 `/Settings` 的網址加進 Outlook「從網際網路訂閱行事曆」→ 確認時間是台北時間而非 UTC → 重新產生 token → Outlook 重新整理後取不到資料 |
| 8 | 管理員可維護會議廳與員工帳號，並可強制取消他人預約 | 任務 20 的檢查清單 + 以管理員取消他人預約，確認擁有者收到「已強制取消」通知 |
| 9 | 全部單元測試與整合測試通過 | 步驟 1 |

驗收標準 7 若手邊沒有 Outlook，退而求其次：把 feed 網址下載成檔案，確認同時含 `BEGIN:VTIMEZONE`／`TZID:Asia/Taipei`／`DTSTART;TZID=Asia/Taipei:` 三者，並用 Google 行事曆的「以網址訂閱」交叉驗證。**只確認檔案內容不算通過**——這一條的重點正是「行事曆軟體真的讀得對」。

- [ ] **步驟 4：Commit 驗收紀錄**

```bash
git add -A
git commit -m "docs: 新增端到端驗收紀錄"
```

---

## 自檢紀錄

本計劃完成後，已對照規格逐節檢查：

**規格覆蓋度**

| 規格章節 | 對應任務 |
|---|---|
| 2.1 帳號與登入 | 8 |
| 2.1 個人行事曆三檢視 | 17 |
| 2.1 重複事件（完整 RRULE、有界展開） | 2、3 |
| 2.1 會議廳主檔與空房查詢 | 12、18、20 |
| 2.1 預約與衝突偵測 | 5、6、7 |
| 2.1 與會者名單 | 10、16 |
| 2.1 站內通知中心 | 9、19 |
| 2.1 .ics 匯出與訂閱 feed | 13、19 |
| 2.1 管理員後台 | 14、20 |
| 3 角色與權限、可見性規則 | 8、10、11 |
| 4.1–4.7 資料模型與索引 | 1 |
| 5.1 衝突規則、頭尾相接 | 4、5 |
| 5.2 併發控制 | 5、6 |
| 5.3 重複事件的兩條約束與結構化設定器 | 2、3、16 |
| 5.4 系列編輯語意（single / series） | 7、10、11 |
| 5.5 通知產生時機 | 9、10 |
| 5.6 .ics 輸出 | 13 |
| 6 架構與分層、6.1 邊界原則 | 全部（D2、D3、D5 為總則） |
| 6.2 技術棧 | 1、8 |
| 7 API 契約與統一信封 | 8、11、12、13、14 |
| 7.2 409 回應格式 | 5、8、11 |
| 7.3 三種 scope | 4、10、11 |
| 7.4 與會者衝突警示 | 10、16 |
| 8 畫面規格（九個頁面） | 15–20 |
| 9 錯誤處理 | 8 |
| 10.1–10.4 測試策略 | 2、3、5、6、7、8、11、12、13、14 |
| 11 驗收標準 | 21 |

**規格未寫明而由本計劃決定的事項**（都已在對應任務中標示理由）：

1. 登入失敗回 400 而非 401（任務 8）
2. 系列換會議廳時，保留的未來 occurrence 一併搬到新會議廳並參與衝突檢查（任務 7）
3. 只鎖新的會議廳，不鎖舊的（D3）
4. 重複規則必須與事件起始日一致，否則回 400（任務 3）
5. 全天事件存為 00:00–23:59，`.ics` 也以帶 TZID 的時間輸出（任務 10、13）
6. feed 的時間窗口為過去 90 天至未來 730 天（任務 13）
7. 規格 §8 需要但 §7 未列出的三個端點：`GET /api/v1/me`、`POST /api/v1/me/change-password`、`POST /api/v1/users/{id}/reset-password`，以及與會者選單用的 `GET /api/v1/users/picker`（任務 8、14）
8. 前端沒有 JS 測試框架，改以「跑起來點一遍」的檢查清單驗證（階段三前言）

---

## 執行交接

計劃已完成並保存到 `docs/superpowers/plans/2026-08-29-calendar-room-booking.md`。兩種執行方式：

**1. 子代理驅動（推薦）** — 每個任務調度一個新的子代理，任務間進行審查，快速迭代

**2. 內聯執行** — 在當前會話中使用 executing-plans 執行任務，批量執行並設有檢查點

選哪種方式？
