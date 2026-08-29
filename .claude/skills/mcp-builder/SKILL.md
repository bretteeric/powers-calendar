---
name: mcp-builder
description: MCP 服務器構建方法論 — 系統化構建生產級 MCP 工具，讓 AI 助手連接外部能力
version: "1.0.0"
license: MIT
metadata:
  hermes:
    tags: [mcp, development]
---

# MCP 服務器構建

系統化設計、實現、測試和部署 Model Context Protocol 服務器的方法論。

## 1. 協議核心概念

MCP 定義三種原語：

- **Tools（工具）**：AI 助手主動調用的函數，有副作用。如搜索、創建、刪除操作。
- **Resources（資源）**：AI 助手只讀訪問的數據源，用 URI 標識。如 `users://{id}/profile`。
- **Prompts（提示詞模板）**：預定義交互模板，引導用戶觸發工作流。

**選擇原則：** 執行操作 → Tool | 讀取數據 → Resource | 引導交互 → Prompt

## 2. 項目結構規範

### TypeScript
```
my-mcp-server/
├── src/
│   ├── index.ts          # 入口，註冊 tools/resources
│   ├── tools/             # 按功能拆分
│   ├── resources/
│   └── lib/               # 客戶端封裝、校驗邏輯
├── tests/
├── package.json
└── tsconfig.json
```

關鍵依賴：`@modelcontextprotocol/sdk` + `zod`

### Python
```
my-mcp-server/
├── src/my_mcp_server/
│   ├── server.py
│   ├── tools/
│   └── lib/
├── tests/
└── pyproject.toml
```

關鍵依賴：`mcp` + `pydantic`

## 3. Tool 設計原則

### 命名
- `snake_case` 格式，動詞開頭：`search_users`、`create_issue`、`delete_file`
- 名稱自解釋，AI 助手靠名稱選工具，模糊命名導致誤調用

### 參數
- 每個參數有類型約束和 `.describe()` 描述
- 可選參數給默認值，減少 AI 決策負擔
- 用枚舉代替布爾開關

```typescript
server.tool("search_issues", {
  query: z.string().describe("搜索關鍵詞"),
  status: z.enum(["open", "closed", "all"]).default("open").describe("狀態篩選"),
  limit: z.number().min(1).max(100).default(20).describe("返回上限"),
}, async ({ query, status, limit }) => { /* ... */ });
```

### 描述
說明**用途 + 返回內容 + 限制**，這是 AI 選擇工具的關鍵依據：

```typescript
server.tool("search_users",
  "根據姓名或郵箱搜索用戶。返回 ID、姓名、郵箱列表。模糊匹配，最多 50 條。",
  schema, handler);
```

### 輸出
- 結構化數據 → JSON，人類可讀內容 → Markdown
- 始終用 `content: [{ type: "text", text: "..." }]` 格式返回

## 4. 輸入驗證和錯誤處理

用 Zod/Pydantic 做 Schema 級校驗，業務級校驗放 handler 開頭：

```typescript
server.tool("get_user", { id: z.string() }, async ({ id }) => {
  try {
    const user = await db.getUser(id);
    if (!user) {
      return {
        content: [{ type: "text", text: `用戶 ${id} 不存在，請檢查 ID。` }],
        isError: true,
      };
    }
    return { content: [{ type: "text", text: JSON.stringify(user, null, 2) }] };
  } catch (err) {
    return {
      content: [{ type: "text", text: `查詢失敗：${err.message}` }],
      isError: true,
    };
  }
});
```

**錯誤處理四原則：**
1. 永遠不讓服務器崩潰 — try/catch 包裹所有外部調用
2. 返回可操作的錯誤信息 — 告訴 AI 問題是什麼、能做什麼
3. 使用 `isError: true` — 讓 AI 知道調用失敗
4. 區分錯誤類型 — 參數錯誤、權限不足、資源不存在、服務不可用

## 5. 資源管理和生命週期

```typescript
// 資源註冊
server.resource("user-profile", "users://{userId}/profile", async (uri) => {
  const profile = await db.getProfile(extractId(uri));
  return { contents: [{ uri: uri.href, mimeType: "application/json", text: JSON.stringify(profile) }] };
});

// 生命週期：先初始化 → 再 connect → 監聽關閉信號
const db = await Database.connect(config.dbUrl);
await server.connect(new StdioServerTransport());
process.on("SIGINT", async () => { await db.disconnect(); await server.close(); process.exit(0); });
```

關鍵點：使用連接池、所有外部調用設超時、優雅關閉清理資源。

## 6. 測試策略

### 單元測試 — 業務邏輯與 MCP 註冊分離
```typescript
// tools/search.ts 導出純函數
export async function searchUsers(query: string, limit: number) { /* ... */ }

// search.test.ts 獨立測試
test("返回匹配結果", async () => {
  const results = await searchUsers("alice", 10);
  expect(results[0].name).toContain("Alice");
});
```

### 集成測試 — 用 SDK Client 做端到端驗證
```typescript
const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
await server.connect(serverTransport);
const client = new Client({ name: "test", version: "1.0.0" });
await client.connect(clientTransport);
const result = await client.callTool("search_users", { query: "test" });
expect(result.isError).toBeFalsy();
```

### MCP Inspector — 交互式調試
```bash
npx @modelcontextprotocol/inspector node dist/index.js
```

在瀏覽器中查看所有 tools/resources，手動調用並查看結果。

**測試要點：** 每個 Tool 覆蓋正常 + 異常路徑、邊界值、外部服務失敗模擬。

## 7. 安全考慮

**權限控制：**
- 最小權限原則，讀寫 Tool 分離
- 危險操作要求確認參數（如 `confirm: true`）

**輸入安全：**
- SQL 注入 → 參數化查詢，絕不拼接
- 路徑遍歷 → 校驗路徑，禁止 `../`
- 命令注入 → 用 `execFile` 而非 `exec`

**敏感數據：**
- 密鑰通過環境變量傳入，不硬編碼
- 日誌不打印完整敏感信息
- 返回數據做脫敏處理

**沙箱：** 文件操作限制目錄、網絡請求限制白名單、設置資源配額。

## 8. 部署和分發

### npm 發佈
```json
{ "bin": { "mcp-server-myservice": "dist/index.js" }, "files": ["dist"] }
```

用戶配置：
```json
{ "mcpServers": { "myservice": { "command": "npx", "args": ["@yourorg/mcp-server-myservice"], "env": { "API_KEY": "xxx" } } } }
```

### pip 發佈
```toml
[project.scripts]
mcp-server-myservice = "my_mcp_server.server:main"
```

### Docker — 適用於複雜依賴或隔離場景
```dockerfile
FROM node:20-slim
WORKDIR /app
COPY package*.json ./ && RUN npm ci --production
COPY dist ./dist
ENTRYPOINT ["node", "dist/index.js"]
```

## 9. 調試技巧

**關鍵：MCP 用 stdio 通信，不能用 `console.log`，會破壞協議流。**

```typescript
// 錯誤
console.log("debug");
// 正確
console.error("[DEBUG]", info);
// 更好
server.sendLoggingMessage({ level: "info", data: "處理中" });
```

**常見問題：**

| 症狀 | 原因 | 解決 |
|------|------|------|
| 啟動無響應 | transport 未連接 | 檢查 `server.connect()` |
| Tool 不出現 | 註冊在 connect 之後 | 先註冊再 connect |
| AI 不調用 Tool | 描述不清晰 | 改善名稱和描述 |
| 參數總錯 | Schema 不明確 | 添加 `.describe()` |
| 調用超時 | 外部服務慢 | 加超時和緩存 |

**調試流程：** Inspector 驗證基本功能 → 手動調用確認輸入輸出 → 連接真實 AI 客戶端觀察調用模式 → 根據實際行為調整設計。

## 10. 構建檢查清單

### 設計
- [ ] 明確 Tools vs Resources vs Prompts 分工
- [ ] Tool 命名 `動詞_名詞`，描述說明用途和返回內容
- [ ] 參數簡潔，可選參數有合理默認值

### 實現
- [ ] 輸入用 Zod/Pydantic 校驗
- [ ] 外部調用有 try/catch 和超時
- [ ] 錯誤返回 `isError: true` 並附可操作信息
- [ ] 不用 `console.log`（用 stderr 或 SDK 日誌）
- [ ] 敏感數據走環境變量

### 測試
- [ ] 核心邏輯有單元測試
- [ ] 有集成測試驗證 MCP 協議交互
- [ ] 用 MCP Inspector 手動驗證過
- [ ] 用真實 AI 客戶端測試過

### 部署
- [ ] README 含安裝和配置說明
- [ ] 提供客戶端配置 JSON 示例
- [ ] 遵循 semver，無硬編碼密鑰
