# Antigravity CLI（`agy`）工具映射

Skills 說的是動作（"分派一個子智能體"、"建一條待辦"、"讀一個文件"）。在 Antigravity CLI（`agy`）上，這些動作對應下面這些工具。

| Skill 請求的動作 | Antigravity CLI 等價工具 |
|----------------|----------------------|
| 分派子智能體（`Subagent (general-purpose):` 模板） | `invoke_subagent`，配一個內置的 `TypeName` —— 全能力工作用 `self`，只讀調研用 `research` |
| 任務跟蹤（"建一條待辦"、"標記完成"） | 一個 **task artifact** —— 用 `write_to_file` 並帶上 `IsArtifact: true` 與 `ArtifactType: "task"`（見下方[任務跟蹤](#任務跟蹤)）。**不是** `manage_task`，那個是管後臺進程的。 |

## 任務跟蹤

Antigravity **沒有 todo 工具**（`manage_task` 管的是後臺進程 —— `list`／`kill`／`status`／`send_input` —— 它**不是**清單工具）。當某個 skill 說要創建待辦清單或跟蹤任務時，改為維護一個 **task artifact**：一份用 `write_to_file` 保存的 markdown 清單（`IsArtifact: true`、`ArtifactMetadata.ArtifactType: "task"`），過程中用 `replace_file_content` ／ `multi_replace_file_content` 來編輯。

任何多步任務一開始，就創建這個 task artifact，把你計劃裡的每一步都列上。每完成一步，就編輯該 artifact 把它標記為完成（`- [x]`）。計劃有變就更新清單。**保持它是最新的** —— 它是"還剩什麼沒做"的唯一事實來源；一旦對話變長，每開始一步之前先重讀它。
