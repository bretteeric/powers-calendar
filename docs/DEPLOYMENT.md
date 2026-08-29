# 部署前提

本文件記錄「個人行事曆與會議廳預約系統」上線前必須處理的事項。

這些項目**不阻擋合併**——它們都是部署環境的設定，不是程式碼缺陷；在開發環境維持現狀
才能讓 `start.ps1`、整合測試與驗收流程照常運作。但**正式環境上線前必須逐項確認**。

---

## 1. 預設管理員密碼是原始碼中的公開常數

`src/OfficeCal.Infrastructure/DbSeeder.cs`：

```csharp
public const string AdminEmployeeNo = "A0001";
public const string AdminInitialPassword = "Admin@12345";
```

這組帳密寫死在原始碼裡，任何拿得到程式碼（或讀過本文件）的人都知道。
系統也**沒有「首次登入必須改密碼」的機制**——不改就會一直有效。

**上線動作：** 首次部署完成後**立即**以 `A0001` 登入，透過「設定 → 修改密碼」
（`POST /api/v1/me/change-password`）改成新密碼。這是上線後的第一件事，不是待辦事項。

> 系統禁止管理員停用或降級自己的帳號，因此 `A0001` 無法被自己關閉；
> 若要停用它，需先建立另一個 Admin 帳號，再以該帳號停用 `A0001`。

---

## 2. `SeedAsync` 在所有環境無條件執行，且會自動套用 migration

`src/OfficeCal.Web/Program.cs` 在建立 `WebApplication` 之後、處理任何請求之前，
無條件呼叫 `DbSeeder.SeedAsync(...)`，沒有 `IsDevelopment()` 之類的環境判斷。
而 `SeedAsync` 的第一件事是：

```csharp
await db.Database.MigrateAsync();
```

也就是說，**每次站台啟動都會自動把尚未執行的 migration 套用到目標資料庫**，
接著補上缺少的種子資料（三個部門、`A0001` 管理員、三間會議廳）。
種子邏輯本身是冪等的（都先 `AnyAsync` 檢查再新增），不會重複塞資料，
但「自動 migrate」這件事本身需要被明確接受。

**上線前確認：**

- 正式環境的資料庫連線帳號具備 DDL 權限（`MigrateAsync` 需要）——若貴組織的
  正式環境不允許應用程式帳號改結構，必須改為部署流程外部執行
  `dotnet ef database update`，並把 `SeedAsync` 內的 `MigrateAsync()` 拿掉。
- 多台站台同時啟動時，EF Core 會以 `sp_getapplock`（`__EFMigrationsLock`）互斥，
  不會並行套用同一份 migration；但仍建議部署時先讓單一執行個體完成啟動。
- 種子資料是否符合實際組織。**會議廳**可在上線後透過管理後台調整
  （`/Admin/Rooms`，對應 `POST`／`PUT /api/v1/rooms`）。
  **部門則沒有管理介面**——`DepartmentsController` 只有 `GET`，也沒有對應的後台頁面，
  三個種子部門（資訊部／業務部／管理部）若不符合實際組織，只能直接改資料庫，
  或另外開發部門維護功能。請勿為此改 `DbSeeder`（那會影響既有測試）。

---

## 3. 沒有 HTTPS 重導，也沒有設定 Cookie 的 Secure 政策

`Program.cs` 的管線中**沒有** `UseHttpsRedirection()` 與 `UseHsts()`，
Cookie 驗證也**沒有**設定 `CookieSecurePolicy`：

```csharp
o.Cookie.Name = "OfficeCal.Auth";
o.Cookie.HttpOnly = true;
o.Cookie.SameSite = SameSiteMode.Lax;
// 沒有 o.Cookie.SecurePolicy = ...
```

ASP.NET Core 的預設值是 `CookieSecurePolicy.SameAsRequest`：走 HTTP 進來，
驗證 Cookie 就會以**非 Secure** 的形式送出，同網段的被動竊聽者可以直接取得 session。

**上線前確認：**

- **若站台部署在會終止 TLS 的反向代理後方**（Nginx／IIS ARR／Application Gateway 等），
  代理與瀏覽器之間已是 HTTPS，代理到站台之間走 HTTP 是常見且可接受的設計。
  此時仍應在 `Program.cs` 的 `AddCookie` 內加上：

  ```csharp
  o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
  ```

  否則站台看到的是 HTTP 請求，Cookie 不會帶 `Secure` 旗標。
  同時請確認代理有轉送 `X-Forwarded-Proto`，並視需要加上
  `UseForwardedHeaders()`。

- **若站台直接對外提供服務**，除了上述 `SecurePolicy = Always`，
  還必須補上 `app.UseHttpsRedirection()` 與 `app.UseHsts()`。

---

## 4. 登入端點沒有速率限制

`POST /api/v1/auth/login` 沒有任何嘗試次數限制、鎖定機制或延遲。
配合第 1 項的已知預設帳號（`A0001`），這是可被自動化暴力破解的組合。

**建議做法：** 使用 .NET 內建的 `AddRateLimiter`（`Microsoft.AspNetCore.RateLimiting`，
不需額外套件），對登入端點掛上以來源 IP 或員工編號為分割鍵的固定視窗／權杖桶限制，
例如「同一 IP 每分鐘 10 次」。超限時回 429，並且要走本專案統一的
`ApiResponse` 信封（`GlobalExceptionMiddleware` 之外需另外處理 `OnRejected`）。

若組織有帳號鎖定政策，也可改為在 `AuthController` 記錄連續失敗次數並暫時鎖定帳號；
兩者擇一即可，重點是不能讓登入端點無限次嘗試。

---

## 5. 驗收標準 7（.ics 訂閱）尚未以真實用戶端驗證

規格 §11 驗收標準 7 要求 `.ics` 訂閱網址能被 Outlook／Google 日曆正確訂閱。
開發環境的站台跑在 `http://localhost:5088`，**對外不可達**，因此 Outlook 與
Google 的抓取伺服器無法連上，這條標準在開發階段無法以真實用戶端驗證。

**已完成的替代驗證**（見 `docs/superpowers/plans/2026-08-29-acceptance-log.md` 標準 7）：

- 產出的 `.ics` 以 Ical.Net 獨立交叉解析——Ical.Net 是第三方 iCalendar 函式庫，
  在本專案中原本只被 `RecurrenceService` 用來展開規則，從未用於解析自家輸出，
  因此是真正獨立的驗證。結果確認 `TZID=Asia/Taipei` 被正確解析為台北時區
  （`VTIMEZONE` 區塊存在），得到的當地時間與 UI 一致，沒有被誤讀成 UTC
- 輸出格式符合 RFC 5545：CRLF 換行、75 octet 折行、含 `VTIMEZONE`
  （`tests/OfficeCal.Tests/Unit/IcsWriterTests.cs`、
  `tests/OfficeCal.Tests/Integration/IcsApiTests.cs`）
- 重新產生 token 後舊網址回 404、新網址回 200

**部署後動作：** 站台對外可達之後，實際用 Outlook 與 Google 日曆各訂閱一次
`/feeds/{token}.ics`（完整網址可在「設定」頁取得），確認：

1. 訂閱成功、事件出現在對方行事曆上
2. 顯示的時間與系統內一致（台北當地時間，不應有時區位移）
3. 重複事件展開正確
4. 行事曆軟體的自動更新週期內，異動能同步過去

---

## 上線檢查清單

| # | 項目 | 何時 |
|---|---|---|
| 1 | 修改 `A0001` 的預設密碼 | 首次部署後立即 |
| 2 | 確認自動 migrate 行為可接受、DB 帳號權限足夠 | 部署前 |
| 3 | 設定 `Cookie.SecurePolicy = Always`（必要時加 `UseHttpsRedirection`／`UseHsts`） | 部署前 |
| 4 | 為登入端點加上 `AddRateLimiter` | 部署前 |
| 5 | 以真實 Outlook／Google 用戶端驗證 `.ics` 訂閱 | 部署後 |
