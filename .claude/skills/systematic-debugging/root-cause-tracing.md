# 根因追蹤

## 概述

Bug 通常表現在調用棧深處（在錯誤目錄執行 git init、在錯誤位置創建文件、用錯誤路徑打開數據庫）。你的本能是在錯誤出現的地方修復，但那只是治標。

**核心原則：** 沿著調用鏈反向追蹤，直到找到最初的觸發點，然後在源頭修復。

## 何時使用

```dot
digraph when_to_use {
    "Bug 出現在調用棧深處？" [shape=diamond];
    "能反向追蹤嗎？" [shape=diamond];
    "在症狀處修復" [shape=box];
    "追蹤到最初的觸發點" [shape=box];
    "更好的做法：同時添加縱深防禦" [shape=box];

    "Bug 出現在調用棧深處？" -> "能反向追蹤嗎？" [label="是"];
    "能反向追蹤嗎？" -> "追蹤到最初的觸發點" [label="是"];
    "能反向追蹤嗎？" -> "在症狀處修復" [label="否——死衚衕"];
    "追蹤到最初的觸發點" -> "更好的做法：同時添加縱深防禦";
}
```

**適用場景：**
- 錯誤發生在執行深處（不在入口點）
- 堆棧跟蹤顯示很長的調用鏈
- 不清楚無效數據從哪裡來
- 需要找到是哪個測試/代碼觸發了問題

## 追蹤流程

### 1. 觀察症狀
```
Error: git init failed in /Users/jesse/project/packages/core
```

### 2. 找到直接原因
**哪段代碼直接導致了這個錯誤？**
```typescript
await execFileAsync('git', ['init'], { cwd: projectDir });
```

### 3. 問：誰調用了它？
```typescript
WorktreeManager.createSessionWorktree(projectDir, sessionId)
  → 被 Session.initializeWorkspace() 調用
  → 被 Session.create() 調用
  → 被測試中的 Project.create() 調用
```

### 4. 繼續向上追蹤
**傳入了什麼值？**
- `projectDir = ''`（空字符串！）
- 空字符串作為 `cwd` 會解析為 `process.cwd()`
- 那就是源代碼目錄！

### 5. 找到最初的觸發點
**空字符串從哪裡來的？**
```typescript
const context = setupCoreTest(); // 返回 { tempDir: '' }
Project.create('name', context.tempDir); // 在 beforeEach 之前就訪問了！
```

## 添加堆棧跟蹤

當無法手動追蹤時，添加診斷埋點：

```typescript
// 在有問題的操作之前
async function gitInit(directory: string) {
  const stack = new Error().stack;
  console.error('DEBUG git init:', {
    directory,
    cwd: process.cwd(),
    nodeEnv: process.env.NODE_ENV,
    stack,
  });

  await execFileAsync('git', ['init'], { cwd: directory });
}
```

**重要：** 在測試中使用 `console.error()`（而非 logger——可能不會顯示）

**運行並捕獲：**
```bash
npm test 2>&1 | grep 'DEBUG git init'
```

**分析堆棧跟蹤：**
- 找測試文件名
- 找觸發調用的行號
- 識別模式（同一個測試？同一個參數？）

## 找出導致汙染的測試

如果某些現象在測試期間出現，但你不知道是哪個測試造成的：

使用本目錄下的二分查找腳本 `find-polluter.sh`：

```bash
./find-polluter.sh '.git' 'src/**/*.test.ts'
```

逐個運行測試，在第一個"汙染者"處停止。詳見腳本中的使用說明。

## 真實案例：空的 projectDir

**症狀：** `.git` 被創建在 `packages/core/`（源代碼目錄）中

**追蹤鏈：**
1. `git init` 在 `process.cwd()` 中執行 ← cwd 參數為空
2. WorktreeManager 被傳入空的 projectDir
3. Session.create() 傳遞了空字符串
4. 測試在 beforeEach 之前訪問了 `context.tempDir`
5. setupCoreTest() 初始返回 `{ tempDir: '' }`

**根本原因：** 頂層變量初始化時訪問了空值

**修復：** 將 tempDir 改為 getter，在 beforeEach 之前訪問時拋出異常

**同時添加了縱深防禦：**
- 第 1 層：Project.create() 校驗目錄
- 第 2 層：WorkspaceManager 校驗非空
- 第 3 層：NODE_ENV 守衛拒絕在 tmpdir 之外執行 git init
- 第 4 層：git init 前記錄堆棧跟蹤

## 關鍵原則

```dot
digraph principle {
    "找到了直接原因" [shape=ellipse];
    "能向上追蹤一層嗎？" [shape=diamond];
    "反向追蹤" [shape=box];
    "這就是源頭嗎？" [shape=diamond];
    "在源頭修復" [shape=box];
    "在每一層添加校驗" [shape=box];
    "Bug 不可能再發生" [shape=doublecircle];
    "絕不只修症狀" [shape=octagon, style=filled, fillcolor=red, fontcolor=white];

    "找到了直接原因" -> "能向上追蹤一層嗎？";
    "能向上追蹤一層嗎？" -> "反向追蹤" [label="是"];
    "能向上追蹤一層嗎？" -> "絕不只修症狀" [label="否"];
    "反向追蹤" -> "這就是源頭嗎？";
    "這就是源頭嗎？" -> "反向追蹤" [label="否——繼續追蹤"];
    "這就是源頭嗎？" -> "在源頭修復" [label="是"];
    "在源頭修復" -> "在每一層添加校驗";
    "在每一層添加校驗" -> "Bug 不可能再發生";
}
```

**絕不只在錯誤出現的地方修復。** 反向追蹤，找到最初的觸發點。

## 堆棧跟蹤技巧

**在測試中：** 使用 `console.error()` 而非 logger——logger 可能被抑制
**操作之前：** 在危險操作之前記錄日誌，而不是在失敗之後
**包含上下文：** 目錄、cwd、環境變量、時間戳
**捕獲堆棧：** `new Error().stack` 能顯示完整的調用鏈

## 實際效果

來自調試實踐（2025-10-03）：
- 通過 5 層追蹤找到了根本原因
- 在源頭修復（getter 校驗）
- 添加了 4 層縱深防禦
- 1847 個測試通過，零汙染
