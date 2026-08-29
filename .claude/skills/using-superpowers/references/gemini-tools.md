# Gemini CLI 工具映射

Skills 說的是動作（"分派一個子智能體"、"建一條待辦"、"讀一個文件"）。在 Gemini CLI 上，這些動作對應下面這些工具。

| Skill 請求的動作 | Gemini CLI 等價工具 |
|----------------|-------------------|
| 讀取一個文件 | `read_file` |
| 一次讀取多個文件 | `read_many_files` |
| 創建新文件 | `write_file` |
| 編輯文件 | `replace` |
| 執行 shell 命令 | `run_shell_command` |
| 搜索文件內容 | `grep_search` |
| 按名稱查找文件 | `glob` |
| 列出文件和子目錄 | `list_directory` |
| 抓取 URL | `web_fetch` |
| 搜索網頁 | `google_web_search` |
| 調用一個 skill | `activate_skill` |
| 分派子智能體（`Subagent (general-purpose):` 模板） | `invoke_agent`，`agent_name: "generalist"`（也可用 `@generalist` 聊天語法調用——見[子智能體支持](#子智能體支持)） |
| 多個並行分派 | 同一條響應裡發多個 `invoke_agent` 調用 |
| 任務跟蹤（"建一條待辦"、"標記完成"） | `write_todos`（狀態：pending、in_progress、completed、cancelled、blocked） |

## 指令文件

當某個 skill 提到"你的指令文件"時，在 Gemini CLI 上指的是 **`GEMINI.md`**。Gemini CLI 按層級加載 `GEMINI.md`：全局的在 `~/.gemini/GEMINI.md`，項目級的在工作區目錄及其各級父目錄裡，另外當某個工具訪問子目錄中的文件時，該子目錄下的 `GEMINI.md` 也會被加載。

## 個人 skills 目錄

用戶級 skills 放在 **`~/.gemini/skills/`**，**`~/.agents/skills/`** 是跨運行時的別名目錄（與 Codex、Copilot CLI 共用）。當同一層級下兩個目錄都存在時，`.agents/skills/` 優先。每個 skill 是一個子目錄，裡面有一份帶 `name` 和 `description` frontmatter 的 `SKILL.md`。

## 子智能體支持

Gemini CLI 通過 `invoke_agent` 工具分派子智能體，該工具接收 `agent_name` 和 `prompt` 兩個參數。同一個分派動作也有聊天語法快捷方式：輸入 `@generalist <prompt>` 等價於以 `agent_name: "generalist"` 調用 `invoke_agent`。內置的 agent 名包括 `generalist`、`cli_help`、`codebase_investigator`，以及（啟用瀏覽器工具後的）`browser_agent`。

Skills 用 `Subagent (general-purpose):` 來分派，並且要麼引用一個提示詞模板文件（例如 `superpowers:subagent-driven-development` 的 `./implementer-prompt.md`），要麼直接給出內聯提示詞。在 Gemini CLI 上：

| Skill 裡的分派形式 | Gemini CLI 等價做法 |
|------------------|-------------------|
| 引用某個 `*-prompt.md` 模板（implementer、task-reviewer、code-reviewer 等） | 把模板填好，然後以 `agent_name: "generalist"` 和填好的提示詞調用 `invoke_agent` |
| 引用 `superpowers:requesting-code-review` 的 `./code-reviewer.md` | 以 `agent_name: "generalist"` 和填好的審查模板調用 `invoke_agent` |
| 內聯提示詞（沒有引用模板） | 以 `agent_name: "generalist"` 和你的內聯提示詞調用 `invoke_agent` |

### 填寫提示詞

Skills 提供的提示詞模板裡有 `{WHAT_WAS_IMPLEMENTED}` 或 `[FULL TEXT of task]` 這類佔位符。把所有佔位符都填好，再把完整提示詞交給 `invoke_agent`。模板本身就包含了該 agent 的角色、審查標準和期望的輸出格式——子智能體會照著它執行。

### 並行分派

Gemini CLI 支持並行分派子智能體。在同一條響應裡發出多個 `invoke_agent` 調用（或在一個提示詞裡寫多個 `@generalist` 調用），即可讓相互獨立的子智能體工作並行跑。有依賴關係的任務保持串行，但**不要**為了讓歷史記錄簡單一點就把相互獨立的子智能體任務串起來。

## Gemini CLI 額外工具

以下工具是 Gemini CLI 獨有的：

| 工具 | 用途 |
|------|------|
| `save_memory`（舊版） | 當 `experimental.memoryV2 = false` 時，跨會話持久化事實 |
| `get_internal_docs` | 查閱 Gemini CLI 自帶的文檔 |
| `ask_user` | 向用戶提出結構化問題（文本 / 單選 / 多選） |
| `enter_plan_mode` / `exit_plan_mode` | 進入和退出只讀的計劃模式 |
| `update_topic` | 更新當前會話的主題 / 戰略意圖元數據 |
| `complete_task` | 表示某個 Gemini 子智能體已完成，並把結果返回給父 agent |
| `tracker_create_task`、`tracker_update_task`、`tracker_get_task`、`tracker_list_tasks`、`tracker_add_dependency`、`tracker_visualize` | 功能完整的任務跟蹤器，支持依賴關係與可視化 |
| `read_mcp_resource`、`list_mcp_resources` | 訪問 MCP 資源 |
