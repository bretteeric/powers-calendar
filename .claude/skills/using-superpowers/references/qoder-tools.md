# Qoder 工具映射

Skills 使用 Claude Code 的工具名稱。Qoder（阿里 AI IDE）大部分工具與 Claude Code **同名**，只有少數差異：

| Skill 中的引用 | Qoder 等價工具 |
|---------------|---------------|
| `Read` / `Write` / `Edit` | 同名（`Read` / `Write` / `Edit`） |
| `Bash` | 同名 |
| `Grep` / `Glob` | 同名 |
| `Task`（派遣子 agent） | 同名（`Task`） |
| `WebFetch` / `WebSearch` | 同名 |
| `AskUserQuestion` | 同名 |
| `Skill` | 同名 |
| `TodoWrite` | 同名 |
| `EnterPlanMode` / `ExitPlanMode` | **`EnterSpecMode` / `ExitSpecMode`**（Qoder 把"計劃模式"稱為"Spec 模式"）|

## Task 子 Agent 類型

> **適用範圍：Qoder CLI。** 下表逐條核對自 [Qoder 官方文檔 · 子代理](https://docs.qoder.com/zh/cli/subagent)（核對於 2026-08）。
> **Qoder IDE 的內置 subagent 集合與此不同，我們尚未核實** —— 見下方「IDE 與 CLI 的差異」。

| Claude Code Agent | Qoder CLI 等價 | 說明 |
|------------------|---------------|------|
| `general-purpose` | `general-purpose` | 通用研究型，適合複雜搜索、多文件分析、調用鏈追蹤、多步驟任務 |
| `Explore` | `Explore` | 同名。只讀代碼探索 |
| `Plan` | `Plan` | 同名。只讀設計與規劃 |
| `claude-code-guide` | `qoder-guide` | 非 SDK 模式下可用 |

文檔另列出 `statusline-setup`（TUI 模式）。**沒有內置的 `code-reviewer`** —— 文檔裡出現的 `api-reviewer` 是用戶自建 subagent 的示例，不是內置項。需要專職審查者時，用 `general-purpose` 配 `superpowers:requesting-code-review` 的 `code-reviewer.md` 模板。

### IDE 與 CLI 的差異

[#119](https://github.com/jnMetaCode/superpowers-zh/issues/119) 報告：在 **Qoder IDE** 裡跑 `subagent-driven-development` 時，Qoder 說它只提供 `CodeReview` subagent、**沒有** `general-purpose`，於是自行降級為「控制者直接實現 + CodeReview agent 做審查」。

官方 subagent 文檔只覆蓋 CLI，沒有說這套內置集合同樣適用於 IDE。**所以上表在 Qoder IDE 上不保證成立。** 如果你在 IDE 裡遇到「找不到 general-purpose」，那是預期內的差異，不是 superpowers-zh 裝錯了 —— Qoder 的自動降級本身是合理適配。

## Quest MCP 工具（Qoder 原生）

Qoder 內置 Quest 系統提供以下工具，Claude Code 沒有等價物，可在 skill 流程中直接調用：

| 工具 | 用途 |
|------|------|
| `mcp__quest__search_codebase` | 語義化代碼搜索（按意圖找代碼） |
| `mcp__quest__search_symbol` | 按符號名搜索代碼及關係 |
| `mcp__quest__get_problems` | 獲取文件編譯/語法錯誤 |
| `mcp__quest__run_preview` | 啟動本地 Web 服務器預覽 |
| `mcp__quest__search_memory` / `update_memory` | 跨會話記憶管理 |
| `mcp__quest__fetch_rules` | 查詢規則文件 |

## 加載方式

Qoder 在每個會話自動加載 `.qoder/rules/superpowers-zh.md`（`trigger: always_on`），裡面包含 skill 索引。`.qoder/skills/<name>/SKILL.md` 由模型按 description 自主調用，也可輸入 `/<skill-name>` 手動觸發。
