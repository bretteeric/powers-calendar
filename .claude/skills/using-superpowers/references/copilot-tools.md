# Copilot CLI 工具映射

技能使用 Claude Code 的工具名稱。當你在技能中遇到這些工具時，使用你平臺的等價工具：

| 技能中引用的工具 | Copilot CLI 等價工具 |
|-----------------|----------------------|
| `Read`（讀取文件） | `view` |
| `Write`（創建文件） | `create` |
| `Edit`（編輯文件） | `edit` |
| `Bash`（運行命令） | `bash`（Windows 上常為 `powershell`，見[異步 Shell 會話](#異步-shell-會話)） |
| `Grep`（搜索文件內容） | `grep` |
| `Glob`（按名稱搜索文件） | `glob` |
| `Skill` 工具（調用技能） | `skill` |
| `WebFetch` | `web_fetch` |
| `Task` 工具（分派子智能體） | `task`（參見[智能體類型](#智能體類型)） |
| 多個 `Task` 調用（並行） | 多個 `task` 調用 |
| Task 狀態/輸出 | `read_agent`、`list_agents` |
| `TodoWrite`（任務跟蹤） | `sql` 配合內置 `todos` 表 |
| `WebSearch` | 無等價工具 — 使用 `web_fetch` 配合搜索引擎 URL |
| `EnterPlanMode` / `ExitPlanMode` | 無等價工具 — 留在主會話中 |

## 智能體類型

Copilot CLI 的 `task` 工具接受 `agent_type` 參數：

| Claude Code 智能體 | Copilot CLI 等價 |
|-------------------|----------------------|
| `general-purpose` | `"general-purpose"` |
| `Explore` | `"explore"` |
| 命名的插件智能體（如 `superpowers:code-reviewer`） | 從已安裝的插件中自動發現 |

## 異步 Shell 會話

Copilot CLI 支持持久化的異步 shell 會話，這在 Claude Code 中沒有直接等價物。

> ⚠️ **shell 工具面隨平臺和版本而異，下面兩套工具名不會同時出現。** 動手之前先確認你這個 build 實際註冊的是哪一套 —— 照著不存在的工具名調用，agent 會找不到工具然後即興發揮。Windows 上常見的是 powershell 那一套（實測 Copilot CLI 1.0.69-1 / Windows 只有 powershell，沒有任何 `bash` / `async` 家族工具）。

**Unix / macOS —— bash 一套：**

| 工具 | 用途 |
|------|---------|
| `bash` 配合 `async: true` | 在後臺啟動長時間運行的命令 |
| `write_bash` | 向運行中的異步會話發送輸入 |
| `read_bash` | 讀取異步會話的輸出 |
| `stop_bash` | 終止異步會話 |
| `list_bash` | 列出所有活躍的 shell 會話 |

**Windows —— powershell 一套：**

| 工具 | 用途 |
|------|---------|
| `powershell` 配合 `detach: true` | 在後臺啟動長時間運行的命令（參數名是 `detach`，**不是** `async`） |
| `read_powershell` | 讀取會話的輸出 |
| `stop_powershell` | 終止會話 |
| `list_powershell` | 列出所有活躍的 shell 會話 |
| （無 `write_powershell`） | 這一套**沒有**向運行中會話發送輸入的工具 |

### Windows 上的兩個坑

**1. `.sh` 腳本不能裸跑。** powershell 下直接執行 `scripts/start-server.sh` 會報 `The term 'scripts/start-server.sh' is not recognized...`，必須顯式走 Git Bash：

```powershell
& "C:\Program Files\Git\bin\bash.exe" scripts/start-server.sh
```

**2. `stop_powershell` 停不掉 `detach: true` 啟動的進程。** detached 進程要按 PID 停：

```powershell
Stop-Process -Id <PID>
```

所以**不要把 `stop_*` 當作 detached 常駐進程的唯一清理路徑** —— 必須先拿到真實的 Windows PID（不是 MSYS PID），再 `Stop-Process`。涉及長駐 server 的 skill（如 brainstorming 的視覺伴侶）在 Windows 上尤其要注意這一點。

## 額外的 Copilot CLI 工具

| 工具 | 用途 |
|------|---------|
| `store_memory` | 持久化代碼庫相關事實供未來會話使用 |
| `report_intent` | 更新 UI 狀態行顯示當前意圖 |
| `sql` | 查詢會話的 SQLite 數據庫（待辦、元數據） |
| `fetch_copilot_cli_documentation` | 查閱 Copilot CLI 文檔 |
| GitHub MCP 工具（`github-mcp-server-*`） | 原生 GitHub API 訪問（issue、PR、代碼搜索） |
