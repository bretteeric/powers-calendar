# Codex 工具映射

Skills 使用 Claude Code 的工具名稱。在 Codex 中遇到這些名稱時，請使用對應的平臺等價工具：

| Skill 中的引用 | Codex 等價工具 |
|---------------|---------------|
| `Task` 工具（派遣子 agent） | `spawn_agent` |
| 多個 `Task` 調用（並行） | 多個 `spawn_agent` 調用 |
| Task 返回結果 | `wait_agent` |
| Task 自動完成 | `close_agent` 釋放槽位 |
| `TodoWrite`（任務跟蹤） | `update_plan` |
| `Skill` 工具（調用 skill） | Skills 原生加載——直接按說明操作 |
| `Read`、`Write`、`Edit`（文件） | 使用原生文件工具 |
| `Bash`（執行命令） | 使用原生 shell 工具 |

## 子 Agent 派遣需要多 Agent 支持

在 Codex 配置文件（`~/.codex/config.toml`）中添加：

```toml
[features]
multi_agent = true
```

啟用後可使用 `spawn_agent`、`wait_agent` 和 `close_agent`，支持 `dispatching-parallel-agents` 和 `subagent-driven-development` 等 skills。使用 subagent-driven-development 時，implementer 和 reviewer 子 agent 完成全部工作後應始終 `close_agent` 釋放。
