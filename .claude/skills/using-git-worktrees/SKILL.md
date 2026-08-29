---
name: using-git-worktrees
description: 當需要開始與當前工作區隔離的功能開發，或在執行實現計劃之前使用——通過原生工具或 git worktree 回退機制確保隔離工作區存在
version: "1.0.0"
license: MIT
metadata:
  hermes:
    tags: [git, workflow]
---

# 使用 Git 工作樹

## 概述

確保工作發生在隔離的工作區中。優先使用你的平臺的原生 worktree 工具。僅在沒有原生工具可用時，再回退到手動 git worktree。

**核心原則：** 先檢測現有隔離。然後用原生工具。再回退到 git。絕不與 harness 對抗。

**開始時宣佈：** "我正在使用 using-git-worktrees 技能來建立一個隔離的工作區。"

## 步驟 0：檢測現有隔離

**創建任何東西之前，先檢查你是否已經在一個隔離的工作區裡。**

```bash
GIT_DIR=$(cd "$(git rev-parse --git-dir)" 2>/dev/null && pwd -P)
GIT_COMMON=$(cd "$(git rev-parse --git-common-dir)" 2>/dev/null && pwd -P)
BRANCH=$(git branch --show-current)
```

**Submodule 守衛：** 在 git submodule 內 `GIT_DIR != GIT_COMMON` 也為真。在判定"已經在 worktree 內"之前，先確認你不在 submodule 裡：

```bash
# 如果這條命令返回路徑，說明你在 submodule 裡，不是 worktree —— 按普通倉庫處理
git rev-parse --show-superproject-working-tree 2>/dev/null
```

**如果 `GIT_DIR != GIT_COMMON`（且不是 submodule）：** 你已經在一個 linked worktree 內。跳到步驟 2（項目設置）。**不要**再創建一個 worktree。

按分支狀態報告：

- 在某個分支上："已經在隔離工作區 `<path>`，分支 `<name>`。"
- 分離 HEAD："已經在隔離工作區 `<path>`（分離 HEAD，由外部管理）。完成時需要創建分支。"

**如果 `GIT_DIR == GIT_COMMON`（或在 submodule 內）：** 你在一個普通的倉庫檢出裡。

用戶是否已經在你的 instructions 裡表明過 worktree 偏好？如果沒有，創建 worktree 之前先徵求同意：

> "你希望我搭一個隔離的 worktree 嗎？它能保護你當前分支不被改動。"

如果用戶已聲明過偏好，直接遵循，不再詢問。如果用戶拒絕同意，原地工作並跳到步驟 2。

## 步驟 1：創建隔離工作區

**你有兩種機制。按這個順序嘗試。**

### 1a. 原生 Worktree 工具（首選）

用戶已經請求隔離工作區（步驟 0 已獲同意）。你是否已經有創建 worktree 的方法？可能是名為 `EnterWorktree`、`WorktreeCreate` 的工具、`/worktree` 命令，或 `--worktree` 標誌。如果有，用它，然後跳到步驟 2。

原生工具自動處理目錄放置、分支創建和清理。在你已經有原生工具的情況下使用 `git worktree add`，會創建你的 harness 看不到也無法管理的"幻影狀態"。

只有在沒有原生 worktree 工具可用時，才進入步驟 1b。

### 1b. Git Worktree 回退

**只在步驟 1a 不適用時使用** —— 你沒有可用的原生 worktree 工具。手動用 git 創建 worktree。

#### 目錄選擇

按以下優先級。明確的用戶偏好始終優先於觀察到的文件系統狀態。

1. **檢查你的 instructions 裡是否聲明過 worktree 目錄偏好。** 如果用戶已指定，不再詢問直接用。

2. **檢查是否存在項目本地的 worktree 目錄：**

   ```bash
   ls -d .worktrees 2>/dev/null     # 首選（隱藏目錄）
   ls -d worktrees 2>/dev/null      # 備選
   ```

   找到就用。如果兩者都存在，`.worktrees` 優先。

3. **如果沒有其他可參考的信息**，默認用項目根目錄下的 `.worktrees/`。

#### 安全驗證（僅項目本地目錄）

**創建 worktree 前必須驗證目錄已被忽略：**

```bash
git check-ignore -q .worktrees 2>/dev/null || git check-ignore -q worktrees 2>/dev/null
```

**如果未被忽略：** 添加到 .gitignore，提交該改動，然後繼續。

**為什麼關鍵：** 防止 worktree 內容被意外提交到倉庫。

#### 創建工作樹

```bash
# 根據選定位置確定路徑
path="$LOCATION/$BRANCH_NAME"

git worktree add "$path" -b "$BRANCH_NAME"
cd "$path"
```

**沙盒回退：** 如果 `git worktree add` 因權限錯誤（沙盒拒絕）失敗，告訴用戶沙盒阻止了 worktree 創建，你將在當前目錄原地工作。然後原地運行 setup 和基線測試。

## 步驟 2：項目設置

自動檢測並運行相應的設置命令：

```bash
# Node.js
if [ -f package.json ]; then npm install; fi

# Rust
if [ -f Cargo.toml ]; then cargo build; fi

# Python
if [ -f requirements.txt ]; then pip install -r requirements.txt; fi
if [ -f pyproject.toml ]; then poetry install; fi

# Go
if [ -f go.mod ]; then go mod download; fi
```

## 步驟 3：驗證基線乾淨

運行測試確保工作區初始狀態乾淨：

```bash
# 使用項目對應的命令
npm test / cargo test / pytest / go test ./...
```

**如果測試失敗：** 報告失敗，詢問是繼續還是排查。

**如果測試通過：** 報告就緒。

### 報告

```
工作樹已就緒：<full-path>
測試通過（<N> 個測試，0 個失敗）
準備實現 <feature-name>
```

## 快速參考

| 情況 | 操作 |
|------|------|
| 已在 linked worktree 內 | 跳過創建（步驟 0） |
| 在 submodule 內 | 按普通倉庫處理（步驟 0 守衛） |
| 有原生 worktree 工具 | 用它（步驟 1a） |
| 沒有原生工具 | git worktree 回退（步驟 1b） |
| `.worktrees/` 存在 | 用它（驗證已忽略） |
| `worktrees/` 存在 | 用它（驗證已忽略） |
| 兩者都存在 | 用 `.worktrees/` |
| 都不存在 | 檢查 instructions 文件，再默認 `.worktrees/` |
| 目錄未被忽略 | 添加到 .gitignore + 提交 |
| 創建時權限錯誤 | 沙盒回退，原地工作 |
| 基線測試失敗 | 報告失敗 + 詢問 |
| 無 package.json/Cargo.toml | 跳過依賴安裝 |

## 常見的合理化藉口

| 藉口 | 現實 |
|------|------|
| "我顯然不在 worktree 裡，不用檢查" | 跑步驟 0。宿主環境創建的隔離和 submodule 都能騙過肉眼；只有檢測命令能定論。 |
| "`git worktree add` 比去找原生工具快" | 原生工具（如 `EnterWorktree`）掌管位置、分支和清理。繞過它是**第一大錯誤** —— 會造出你的宿主環境看不見也管不了的幽靈狀態。 |
| "這個 worktree 目錄肯定已經被忽略了" | 跑 `git check-ignore`。一個沒被忽略的 worktree 目錄會把整棵樹提交進倉庫。 |
| "目錄名隨便取都行" | 明確指示 > 已存在的項目內目錄 > `.worktrees/` 默認值。 |
| "工作區是全新的，基線測試可以先放放" | 基線不乾淨會讓之後每一次失敗都含義不明。現在就跑測試；越過失敗繼續是你人類夥伴的決定。 |
