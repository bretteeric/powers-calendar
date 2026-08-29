# 視覺伴侶指南

基於瀏覽器的視覺頭腦風暴伴侶，用於展示原型、圖表和選項。

## 何時使用

逐問題決定，而非按會話決定。判斷標準：**用戶看到它是否比讀到它更容易理解？**

**使用瀏覽器** 當內容本身是視覺的：

- **UI 原型** — 線框圖、佈局、導航結構、組件設計
- **架構圖** — 系統組件、數據流、關係圖
- **並排視覺對比** — 對比兩種佈局、兩種配色方案、兩種設計方向
- **設計細節打磨** — 當問題涉及外觀感受、間距、視覺層次
- **空間關係** — 狀態機、流程圖、實體關係圖

**使用終端** 當內容是文字或表格的：

- **需求和範圍問題** — "X 是什麼意思？"、"哪些功能在範圍內？"
- **概念性 A/B/C 選擇** — 在用文字描述的方案之間做選擇
- **權衡列表** — 優缺點、對比表
- **技術決策** — API 設計、數據建模、架構方案選擇
- **澄清問題** — 任何回答是文字而非視覺偏好的問題

關於 UI 主題的問題不一定是視覺問題。"你想要什麼樣的嚮導？"是概念性的——使用終端。"這些嚮導佈局中哪個感覺對？"是視覺性的——使用瀏覽器。

## 工作原理

服務器監視一個目錄中的 HTML 文件，將最新的文件提供給瀏覽器。你寫入 HTML 內容，用戶在瀏覽器中看到它，並可以點擊選擇選項。選擇結果被記錄到一個 `.events` 文件中，你在下一輪會話中讀取它。

**內容片段 vs 完整文檔：** 如果你的 HTML 文件以 `<!DOCTYPE` 或 `<html` 開頭，服務器會原樣提供（僅注入輔助腳本）。否則，服務器會自動將你的內容包裹在框架模板中——添加頭部、CSS 主題、選擇指示器和所有交互基礎設施。**默認寫內容片段即可。** 只有當你需要完全控制頁面時才寫完整文檔。

## 啟動會話

```bash
# 啟動服務器並持久化（原型保存到項目中）
scripts/start-server.sh --project-dir /path/to/project

# 返回：{"type":"server-started","port":52341,"url":"http://localhost:52341",
#           "screen_dir":"/path/to/project/.superpowers/brainstorm/12345-1706000000"}
```

保存響應中的 `screen_dir`。告訴用戶打開該 URL。

**查找連接信息：** 服務器將其啟動 JSON 寫入 `$SCREEN_DIR/.server-info`。如果你在後臺啟動了服務器且沒有捕獲 stdout，讀取該文件以獲取 URL 和端口。使用 `--project-dir` 時，檢查 `<project>/.superpowers/brainstorm/` 獲取會話目錄。

**注意：** 傳入項目根目錄作為 `--project-dir`，這樣原型會持久化在 `.superpowers/brainstorm/` 中，不會因服務器重啟而丟失。不傳的話，文件會保存到 `/tmp` 並在清理時被刪除。提醒用戶將 `.superpowers/` 添加到 `.gitignore`（如果尚未添加）。

**按平臺啟動服務器：**

**Claude Code (macOS / Linux)：**
```bash
# 默認模式即可——腳本會自動將服務器放到後臺
scripts/start-server.sh --project-dir /path/to/project
```

**Claude Code (Windows)：**
```bash
# Windows 會自動檢測並使用前臺模式，這會阻塞工具調用。
# 在 Bash 工具調用上設置 run_in_background: true，
# 讓服務器在會話輪次之間存活。
scripts/start-server.sh --project-dir /path/to/project
```
通過 Bash 工具調用時，設置 `run_in_background: true`。然後在下一輪讀取 `$SCREEN_DIR/.server-info` 獲取 URL 和端口。

**Codex：**
```bash
# Codex 會回收後臺進程。腳本會自動檢測 CODEX_CI 並
# 切換到前臺模式。正常運行即可——不需要額外標誌。
scripts/start-server.sh --project-dir /path/to/project
```

**Gemini CLI：**
```bash
# 使用 --foreground 並在 shell 工具調用上設置 is_background: true，
# 讓進程在輪次之間存活
scripts/start-server.sh --project-dir /path/to/project --foreground
```

**其他環境：** 服務器必須在會話輪次之間持續在後臺運行。如果你的環境會回收分離的進程，使用 `--foreground` 並通過平臺的後臺執行機制啟動命令。

如果瀏覽器無法訪問該 URL（在遠程/容器化環境中常見），綁定一個非迴環主機：

```bash
scripts/start-server.sh \
  --project-dir /path/to/project \
  --host 0.0.0.0 \
  --url-host localhost
```

使用 `--url-host` 控制返回的 URL JSON 中顯示的主機名。

## 工作循環

1. **檢查服務器存活**，然後**將 HTML 寫入** `screen_dir` 中的新文件：
   - 每次寫入前，檢查 `$SCREEN_DIR/.server-info` 是否存在。如果不存在（或 `.server-stopped` 存在），服務器已關閉——在繼續之前用 `start-server.sh` 重啟。服務器在 30 分鐘無活動後會自動退出。
   - 使用語義化文件名：`platform.html`、`visual-style.html`、`layout.html`
   - **絕不復用文件名** — 每個屏幕用一個新文件
   - 使用 Write 工具 — **絕不使用 cat/heredoc**（會在終端產生噪音）
   - 服務器自動提供最新的文件

2. **告訴用戶預期內容並結束你的回合：**
   - 每一步都提醒他們 URL（不僅僅是第一次）
   - 簡要文字說明屏幕上的內容（例如"展示了 3 個首頁佈局選項"）
   - 請他們在終端中回覆："看一下，告訴我你的想法。如果你願意，可以點擊選擇一個選項。"

3. **在你的下一輪** — 用戶在終端回覆後：
   - 如果存在 `$SCREEN_DIR/.events`，讀取它——其中包含用戶的瀏覽器交互（點擊、選擇），格式為 JSON 行
   - 將終端文字和事件合併以獲得完整信息
   - 終端消息是主要反饋；`.events` 提供結構化的交互數據

4. **迭代或推進** — 如果反饋要求修改當前屏幕，寫入新文件（例如 `layout-v2.html`）。只有當前步驟驗證通過後才進入下一個問題。

5. **回到終端時卸載** — 當下一步不需要瀏覽器時（例如澄清問題、權衡討論），推送一個等待屏幕以清除過時內容：

   ```html
   <!-- 文件名：waiting.html（或 waiting-2.html 等）-->
   <div style="display:flex;align-items:center;justify-content:center;min-height:60vh">
     <p class="subtitle">在終端中繼續...</p>
   </div>
   ```

   這樣可以防止用戶盯著一個已經解決的選擇，而對話已經繼續了。當下一個視覺問題出現時，照常推送新的內容文件。

6. 重複直到完成。

## 編寫內容片段

只寫放在頁面內部的內容。服務器會自動用框架模板包裹它（頭部、主題 CSS、選擇指示器和所有交互基礎設施）。

**最簡示例：**

```html
<h2>哪種佈局更好？</h2>
<p class="subtitle">考慮可讀性和視覺層次</p>

<div class="options">
  <div class="option" data-choice="a" onclick="toggleSelect(this)">
    <div class="letter">A</div>
    <div class="content">
      <h3>單欄</h3>
      <p>簡潔、專注的閱讀體驗</p>
    </div>
  </div>
  <div class="option" data-choice="b" onclick="toggleSelect(this)">
    <div class="letter">B</div>
    <div class="content">
      <h3>雙欄</h3>
      <p>側邊欄導航加主內容區</p>
    </div>
  </div>
</div>
```

就這些。不需要 `<html>`，不需要 CSS，不需要 `<script>` 標籤。服務器會提供這一切。

## 可用的 CSS 類

框架模板為你的內容提供以下 CSS 類：

### 選項（A/B/C 選擇）

```html
<div class="options">
  <div class="option" data-choice="a" onclick="toggleSelect(this)">
    <div class="letter">A</div>
    <div class="content">
      <h3>標題</h3>
      <p>描述</p>
    </div>
  </div>
</div>
```

**多選：** 在容器上添加 `data-multiselect` 讓用戶選擇多個選項。每次點擊切換選中狀態。指示欄顯示數量。

```html
<div class="options" data-multiselect>
  <!-- 相同的選項標記——用戶可以選擇/取消選擇多個 -->
</div>
```

### 卡片（視覺設計）

```html
<div class="cards">
  <div class="card" data-choice="design1" onclick="toggleSelect(this)">
    <div class="card-image"><!-- 原型內容 --></div>
    <div class="card-body">
      <h3>名稱</h3>
      <p>描述</p>
    </div>
  </div>
</div>
```

### 原型容器

```html
<div class="mockup">
  <div class="mockup-header">預覽：儀表盤佈局</div>
  <div class="mockup-body"><!-- 你的原型 HTML --></div>
</div>
```

### 分屏視圖（並排）

```html
<div class="split">
  <div class="mockup"><!-- 左側 --></div>
  <div class="mockup"><!-- 右側 --></div>
</div>
```

### 優缺點

```html
<div class="pros-cons">
  <div class="pros"><h4>優點</h4><ul><li>好處</li></ul></div>
  <div class="cons"><h4>缺點</h4><ul><li>不足</li></ul></div>
</div>
```

### 模擬元素（線框圖構建塊）

```html
<div class="mock-nav">Logo | 首頁 | 關於 | 聯繫我們</div>
<div style="display: flex;">
  <div class="mock-sidebar">導航</div>
  <div class="mock-content">主內容區域</div>
</div>
<button class="mock-button">操作按鈕</button>
<input class="mock-input" placeholder="輸入框">
<div class="placeholder">佔位區域</div>
```

### 排版和區塊

- `h2` — 頁面標題
- `h3` — 章節標題
- `.subtitle` — 標題下方的輔助文字
- `.section` — 帶底部邊距的內容塊
- `.label` — 小號大寫標籤文字

## 瀏覽器事件格式

當用戶在瀏覽器中點擊選項時，交互記錄會保存到 `$SCREEN_DIR/.events`（每行一個 JSON 對象）。推送新屏幕時文件會自動清空。

```jsonl
{"type":"click","choice":"a","text":"選項 A - 簡單佈局","timestamp":1706000101}
{"type":"click","choice":"c","text":"選項 C - 複雜網格","timestamp":1706000108}
{"type":"click","choice":"b","text":"選項 B - 混合方案","timestamp":1706000115}
```

完整的事件流展示了用戶的探索路徑——他們可能在確定之前點擊了多個選項。最後一個 `choice` 事件通常是最終選擇，但點擊模式可以揭示猶豫或值得詢問的偏好。

如果 `.events` 不存在，說明用戶沒有與瀏覽器交互——僅使用他們的終端文字。

## 設計技巧

- **保真度匹配問題** — 佈局問題用線框圖，細節打磨問題用精細設計
- **在每個頁面上解釋問題** — "哪種佈局看起來更專業？"而不僅僅是"選一個"
- **推進前先迭代** — 如果反饋修改了當前屏幕，寫入新版本
- 每個屏幕最多 **2-4 個選項**
- **必要時使用真實內容** — 對於攝影作品集，使用實際圖片（Unsplash）。佔位內容會掩蓋設計問題。
- **保持原型簡潔** — 專注於佈局和結構，而非像素級精確的設計

## 文件命名

- 使用語義化名稱：`platform.html`、`visual-style.html`、`layout.html`
- 絕不復用文件名——每個屏幕必須是新文件
- 迭代版本：添加版本後綴如 `layout-v2.html`、`layout-v3.html`
- 服務器按修改時間提供最新文件

## 清理

```bash
scripts/stop-server.sh $SCREEN_DIR
```

如果會話使用了 `--project-dir`，原型文件會持久化在 `.superpowers/brainstorm/` 中以供日後參考。只有 `/tmp` 會話會在停止時被刪除。

## 參考

- 框架模板（CSS 參考）：`scripts/frame-template.html`
- 輔助腳本（客戶端）：`scripts/helper.js`
