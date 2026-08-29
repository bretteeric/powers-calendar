---
name: workflow-runner
description: "在 Claude Code / OpenClaw / Cursor 中直接運行 agency-orchestrator YAML 工作流——無需 API key，使用當前會話的 LLM 作為執行引擎。當用戶提供 .yaml 工作流文件或要求多角色協作完成任務時觸發。"
version: "1.0.0"
license: MIT
metadata:
  hermes:
    tags: [execution, workflow]
---

# 工作流執行器：在 AI 工具內運行多角色編排

直接在當前會話中執行 agency-orchestrator 的 YAML 工作流，無需配置 API key。當前 LLM 就是執行引擎——依次扮演每個角色完成任務。

## 適用場景

- 用戶提供了一個 `.yaml` 工作流文件（如 `運行 workflows/story-creation.yaml`）
- 用戶要求多個角色協作完成任務（如"用產品經理和架構師一起評審這個 PRD"）
- 用戶安裝了 `agency-agents-zh` 並希望直接在 AI 工具內編排多角色

## 執行流程（5 步）

按以下順序執行，不要跳步：

### 第 1 步：解析工作流

用 Read 工具讀取用戶指定的 YAML 文件，提取以下字段：

```yaml
name: "工作流名稱"
agents_dir: "agency-agents-zh"    # 角色定義目錄
inputs:                            # 輸入變量
  - name: xxx
    required: true/false
    default: "默認值"
steps:                             # 執行步驟
  - id: step_id
    role: "category/agent-name"    # 角色路徑
    task: "任務描述 {{變量}}"       # 支持模板變量
    output: variable_name          # 輸出變量名
    depends_on: [other_step_id]    # 依賴關係
```

**忽略 `llm`、`concurrency`、`timeout`、`retry` 配置**——Skill 模式使用當前會話的 LLM，這些字段僅用於 CLI 模式。

**定位角色目錄**：用 Bash `test -d` 按以下順序檢查，用第一個存在的：
1. 當前工作目錄下的 `{agents_dir}/`（如 `./agency-agents-zh/`）
2. `../{agents_dir}/`（上級目錄）
3. 相對於 YAML 文件所在目錄的 `{agents_dir}/`
4. `node_modules/agency-agents-zh/`

如果全部找不到，**停止執行**並提示用戶：
```
找不到角色目錄。請先安裝：
  git clone --depth 1 https://github.com/jnMetaCode/agency-agents-zh.git
  或：npm install agency-agents-zh
```

### 第 2 步：收集輸入

- 對每個 `required: true` 的輸入，檢查用戶消息中是否已提供值
- 未提供的必填輸入：**立即向用戶詢問**，不要猜測或用空值
- 有 `default` 的可選輸入：使用默認值
- 無默認值的可選輸入：設為空字符串

### 第 3 步：構建執行順序

根據 `depends_on` 進行拓撲排序，將步驟分成多個層級：

- **無 depends_on 的步驟** → 第 1 層
- **depends_on 全部在第 N 層或之前的步驟** → 第 N+1 層
- **同一層內的步驟**互不依賴，可並行

在回覆中展示執行計劃：
```
執行計劃（共 N 步）：
  第 1 層: [step_id] — 角色名
  第 2 層: [step_a, step_b] — 並行
  第 3 層: [step_id] — 角色名
```

### 第 4 步：逐層執行

對每一層：

#### 4a. 預讀角色文件

用 Read 工具讀取該層所有步驟的角色 `.md` 文件：`{角色目錄}/{role}.md`

從文件中提取：
- **角色名**：frontmatter 中的 `name` 字段
- **角色 system prompt**：第二個 `---` 之後的全部 markdown 內容

#### 4b. 渲染 task 模板

將 task 中的 `{{變量名}}` 替換為：
- 來自 inputs 的用戶輸入值
- 來自前序步驟 output 的結果文本

#### 4c. 執行

**單步驟層**：直接在主會話中扮演該角色執行。格式：

```
### Step N/Total: step_id（角色名）

[以該角色身份完成 task，使用角色的專業知識和溝通風格]
```

**多步驟層（並行）**：使用 Agent 工具為每個步驟啟動子代理。每個子代理的 prompt 必須包含：
- 角色文件的**完整文本內容**（不是路徑——子代理可能無法讀文件）
- 渲染後的 task 文本
- 指令："以上是你的角色定義，請以該角色身份完成以下任務，直接輸出結果"

#### 4d. 保存輸出到上下文

如果 step 有 `output` 字段，將該步驟的輸出文本存入變量上下文，供後續步驟的 `{{變量}}` 使用。

### 第 5 步：保存結果並展示

用 Write 工具將結果保存到文件：

```
.ao-output/{工作流名稱}-{YYYY-MM-DD}/
├── steps/
│   ├── 1-{step_id}.md       # 每步的輸出
│   ├── 2-{step_id}.md
│   └── ...
├── summary.md                # 最後一步的完整輸出（最終成果）
└── metadata.json             # 基本元數據
```

metadata.json 格式：
```json
{
  "name": "工作流名稱",
  "date": "2026-03-22",
  "success": true,
  "steps": [
    {"id": "step_id", "role": "category/agent", "status": "completed"},
    ...
  ]
}
```

執行完畢後，向用戶展示：
1. 最終成果（summary.md 的內容）
2. 文件保存位置
3. 執行了幾個步驟

## 重要規則

<HARD-GATE>
- 每個步驟都必須真正扮演對應角色，使用該角色的專業知識和溝通風格，不能泛泛回答
- 角色切換必須明確——每步開始時標註角色名
- 不要跳過步驟或合併步驟，嚴格按 DAG 層級順序執行
- 如果角色文件找不到，告知用戶並建議安裝 agency-agents-zh
- 不要在沒有讀取角色 .md 文件的情況下執行步驟——必須先 Read 再執行
</HARD-GATE>

## 沒有 YAML 文件時的快捷模式

如果用戶沒有指定 YAML 文件，但描述了需要多角色協作的任務：

1. 根據用戶描述，**自動生成** YAML 工作流定義
2. 展示給用戶確認
3. 確認後按上述流程執行

示例：
- 用戶說"幫我用敘事學家和心理學家寫個故事" → 生成 story-creation 類似的工作流
- 用戶說"讓產品經理和架構師評審這個 PRD" → 生成 product-review 類似的工作流

## 故障處理

- **角色文件不存在**：提示用戶運行 `ao init` 或 `npm install agency-agents-zh`
- **模板變量未定義**：檢查上下文，如果是必填輸入則向用戶詢問
- **步驟執行失敗**：標記該步驟為失敗，跳過所有依賴它的下游步驟，繼續執行其他獨立步驟
