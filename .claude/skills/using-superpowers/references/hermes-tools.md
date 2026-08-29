# Hermes Agent 工具映射

技能使用 Claude Code 的工具名稱。當你在技能中遇到這些工具時，使用你平臺的等價工具：

| 技能中引用的工具 | Hermes Agent 等價工具 |
|-----------------|----------------------|
| `Read`（讀取文件） | `read_file` |
| `Write`（創建文件） | `write_file` |
| `Edit`（編輯文件） | `patch` |
| `Bash`（運行命令） | `terminal` |
| `Grep`（搜索文件內容） | `search_files` |
| `Glob`（按名稱搜索文件） | `search_files` |
| `Skill` 工具（調用技能） | `skill_view` |
| `WebFetch` | `web_extract` |
| `WebSearch` | `web_search` |
| `Task` 工具（分派子智能體） | `delegate_task` |
| 多個 `Task` 調用（並行） | 多個 `delegate_task` 調用 |
| `TodoWrite`（任務跟蹤） | `todo` |
| `EnterPlanMode` / `ExitPlanMode` | 無等價工具 — 留在主會話中 |

## 技能管理

Hermes Agent 使用三級漸進式技能加載：

| 操作 | 工具 |
|------|------|
| 列出所有可用技能 | `skills_list` |
| 查看技能完整內容 | `skill_view(name)` |
| 查看技能的引用文件 | `skill_view(name, path)` |
| 管理技能（安裝/更新） | `skill_manage` |

## 額外的 Hermes Agent 工具

| 工具 | 用途 |
|------|---------|
| `memory` | 持久化知識供未來會話使用 |
| `session_search` | 搜索歷史會話記錄 |
| `execute_code` | 在沙箱中執行代碼 |
| `process` | 後臺進程管理 |
| `vision_analyze` | 圖像分析 |
| `image_generate` | 圖像生成 |
| `clarify` | 向用戶提出澄清性問題 |
| `browser_*` | 瀏覽器自動化工具集 |
| `mixture_of_agents` | 多智能體高級推理 |
