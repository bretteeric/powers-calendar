# 縱深防禦校驗

## 概述

當你修復了一個由無效數據引起的 bug 時，在一個地方加校驗似乎就夠了。但這個單點檢查可能會被不同的代碼路徑、重構或 mock 繞過。

**核心原則：** 在數據經過的每一層都做校驗。讓這個 bug 在結構上不可能發生。

## 為什麼需要多層校驗

單層校驗："我們修了這個 bug"
多層校驗："我們讓這個 bug 不可能再發生"

不同層級能捕獲不同問題：
- 入口校驗捕獲大多數 bug
- 業務邏輯校驗捕獲邊界情況
- 環境守衛防止特定上下文的危險操作
- 調試日誌在其他層級失效時提供幫助

## 四個層級

### 第 1 層：入口校驗
**目的：** 在 API 邊界拒絕明顯無效的輸入

```typescript
function createProject(name: string, workingDirectory: string) {
  if (!workingDirectory || workingDirectory.trim() === '') {
    throw new Error('workingDirectory cannot be empty');
  }
  if (!existsSync(workingDirectory)) {
    throw new Error(`workingDirectory does not exist: ${workingDirectory}`);
  }
  if (!statSync(workingDirectory).isDirectory()) {
    throw new Error(`workingDirectory is not a directory: ${workingDirectory}`);
  }
  // ... 繼續處理
}
```

### 第 2 層：業務邏輯校驗
**目的：** 確保數據對當前操作是合理的

```typescript
function initializeWorkspace(projectDir: string, sessionId: string) {
  if (!projectDir) {
    throw new Error('projectDir required for workspace initialization');
  }
  // ... 繼續處理
}
```

### 第 3 層：環境守衛
**目的：** 防止在特定環境中執行危險操作

```typescript
async function gitInit(directory: string) {
  // 在測試中，拒絕在臨時目錄之外執行 git init
  if (process.env.NODE_ENV === 'test') {
    const normalized = normalize(resolve(directory));
    const tmpDir = normalize(resolve(tmpdir()));

    if (!normalized.startsWith(tmpDir)) {
      throw new Error(
        `Refusing git init outside temp dir during tests: ${directory}`
      );
    }
  }
  // ... 繼續處理
}
```

### 第 4 層：調試埋點
**目的：** 記錄上下文信息以便事後分析

```typescript
async function gitInit(directory: string) {
  const stack = new Error().stack;
  logger.debug('About to git init', {
    directory,
    cwd: process.cwd(),
    stack,
  });
  // ... 繼續處理
}
```

## 應用模式

當你發現一個 bug 時：

1. **追蹤數據流** —— 錯誤值從哪裡產生的？在哪裡被使用？
2. **標註所有檢查點** —— 列出數據經過的每一個節點
3. **在每一層添加校驗** —— 入口、業務邏輯、環境、調試
4. **測試每一層** —— 嘗試繞過第 1 層，驗證第 2 層能否捕獲

## 實際案例

Bug：空的 `projectDir` 導致 `git init` 在源代碼目錄執行

**數據流：**
1. 測試準備 → 空字符串
2. `Project.create(name, '')`
3. `WorkspaceManager.createWorkspace('')`
4. `git init` 在 `process.cwd()` 中執行

**添加的四層防禦：**
- 第 1 層：`Project.create()` 校驗非空/存在/可寫
- 第 2 層：`WorkspaceManager` 校驗 projectDir 非空
- 第 3 層：`WorktreeManager` 在測試中拒絕在 tmpdir 之外執行 git init
- 第 4 層：git init 前記錄堆棧跟蹤

**結果：** 全部 1847 個測試通過，bug 不可能再復現

## 關鍵洞察

四個層級缺一不可。在測試過程中，每一層都捕獲了其他層遺漏的 bug：
- 不同的代碼路徑繞過了入口校驗
- mock 繞過了業務邏輯檢查
- 不同平臺的邊界情況需要環境守衛
- 調試日誌發現了結構性誤用

**不要止步於一個校驗點。** 在每一層都添加檢查。
