---
name: finishing-a-development-branch
description: 當實現完成、所有測試通過、需要決定如何集成這份工作時使用
version: "1.0.0"
license: MIT
metadata:
  hermes:
    tags: [git, workflow]
---

# 收尾一個開發分支

## 概述

**核心原則：** 驗證測試 → 檢測環境 → 展示選項 → 執行選擇 → 清理。

**開始時宣告：** "我正在使用 finishing-a-development-branch 技能來收尾這份工作。"

## 步驟 1：驗證測試

運行項目的完整測試套件（`npm test` / `cargo test` / `pytest` / `go test ./...`）。

**如果測試失敗**，報告失敗並停下——菜單是在測試全綠之後才出現的：

```
測試失敗（<N> 個）。完成之前必須先修：

[展示失敗詳情]
```

**如果測試通過：** 繼續步驟 2。

## 步驟 2：檢測環境

```bash
GIT_DIR=$(cd "$(git rev-parse --git-dir)" 2>/dev/null && pwd -P)
GIT_COMMON=$(cd "$(git rev-parse --git-common-dir)" 2>/dev/null && pwd -P)
# 現在就捕獲 —— 此刻還在工作區裡面。步驟 5 會切換目錄，
# 而清理（步驟 6）需要這個值
WORKTREE_PATH=$(git rev-parse --show-toplevel)
```

這決定了展示哪種菜單、以及清理方式：

| 狀態 | 菜單 | 清理 |
|------|------|------|
| `GIT_DIR == GIT_COMMON`（普通倉庫） | 標準 3 個選項 | 無 worktree 可清理 |
| `GIT_DIR != GIT_COMMON`，命名分支 | 標準 3 個選項 | 按來源判斷（見步驟 6） |
| `GIT_DIR != GIT_COMMON`，分離 HEAD | 收斂為 2 個選項（不含合併） | 由外部管理——原地別動 |

## 步驟 3：確定基礎分支

基礎分支就是這份工作從哪兒分出來的那個——通常在計劃裡、對話裡，或者分支的 upstream 裡已經寫明瞭。如果還不知道，就問："這個分支是從 <你的最佳猜測> 分出來的，對嗎？"**合併之前先確認：合併到錯誤的基礎分支，代價很高。**

## 步驟 4：展示選項

**普通倉庫和命名分支 worktree——精確展示這 3 個選項：**

```
實現已完成。你想怎麼做？

1. 本地合併回 <base-branch>
2. 推送並創建 Pull Request
3. 保留分支不動（我稍後自己處理）

選哪個？
```

**分離 HEAD——精確展示這 2 個選項：**

```
實現已完成。你當前處於分離 HEAD（由外部管理的工作區）。

1. 作為新分支推送並創建 Pull Request
2. 保持原樣（我稍後自己處理）

選哪個？
```

**照原文展示菜單**——簡潔，每個選項都來自上面的列表。**丟棄工作只在你的人類夥伴明確提出時才發生**（見下方"如果你的人類夥伴要求丟棄這份工作"）。等他們回答；集成與否是他們的決定。

## 步驟 5：執行選擇

### 選項 1：本地合併

```bash
# 切到主倉庫根目錄，保證 CWD 安全
MAIN_ROOT=$(git -C "$(git rev-parse --git-common-dir)/.." rev-parse --show-toplevel)
cd "$MAIN_ROOT"

# 先合併 —— 在刪除任何東西之前先驗證合併成功
git checkout <base-branch>
git pull
git merge <feature-branch>

# 在合併結果上驗證測試
<測試命令>
```

如果測試在**合併結果**上失敗：停下，把 worktree 和分支原地留著，去排查——什麼都還沒推送，所以這次合併是本地的、可恢復的。

一旦合併結果全綠：清理 worktree（步驟 6），然後刪除分支：

```bash
git branch -d <feature-branch>
```

### 選項 2：推送並創建 PR

```bash
git push -u origin <feature-branch>
# 從分離 HEAD 出發時，在遠端指定新分支名：
# git push origin HEAD:refs/heads/<new-branch>
```

然後用**代碼託管平臺**（forge）的工具針對 <base-branch> 創建 pull/merge request——有 CLI 就用它，沒有就用推送時大多數平臺會打印出來的創建 URL——遵循倉庫裡已有的 PR 模板與約定（如果有），並把 URL 報告給你的人類夥伴。

**保留 worktree**——你的人類夥伴要在那裡根據 PR 反饋繼續迭代。

### 選項 3：保持原樣

報告："保留分支 <name>。工作樹保留在 <path>。"

### 如果你的人類夥伴要求丟棄這份工作

**這條路只作為對"明確要求把工作扔掉"的響應而存在。** 先確認：

```
這將永久刪除：
- 分支 <name>
- 所有 commit：<commit 列表>
- 位於 <path> 的工作樹

輸入 'discard' 以確認。
```

等待**這個精確的**確認詞。收到之後：

```bash
MAIN_ROOT=$(git -C "$(git rev-parse --git-common-dir)/.." rev-parse --show-toplevel)
cd "$MAIN_ROOT"
```

然後清理 worktree（步驟 6），再強制刪除分支：

```bash
git branch -D <feature-branch>
```

## 步驟 6：清理工作區

**只對選項 1 和已確認的丟棄執行。** 選項 2 和 3 始終保留 worktree。兩個調用方都已經切到主倉庫根目錄了——移除 worktree 必須從 worktree 外面執行——因此這裡使用**步驟 2 裡捕獲的** `GIT_DIR` / `GIT_COMMON` / `WORKTREE_PATH`，也就是那次目錄切換之前的值。

> ⚠️ **不要在這裡重新計算這些值。** 此刻 `git rev-parse --show-toplevel` 返回的是主倉庫根目錄，不是 worktree 路徑 —— 溯源判斷會永遠匹配不上，清理會靜默空轉，隨後分支刪除還會因為 worktree 仍掛著而失敗。

**如果 `GIT_DIR == GIT_COMMON`：** 普通倉庫，無 worktree 可清理。結束。

**如果 `WORKTREE_PATH` 在 `.worktrees/` 或 `worktrees/` 之下：** 這是 Superpowers 創建的 worktree——我們負責清理：

```bash
git worktree remove "$WORKTREE_PATH"
git worktree prune  # 自愈：清理任何過期的註冊記錄
```

**否則：** 這個工作區歸宿主環境所有——原地別動。如果你的平臺提供了工作區退出工具，用它。

## 快速參考

| 選項 | 合併 | 推送 | 保留工作樹 | 清理分支 |
|------|------|------|-----------|---------|
| 1. 本地合併 | 是 | - | - | 是 |
| 2. 創建 PR | - | 是 | 是 | - |
| 3. 保持原樣 | - | - | 是 | - |
| 丟棄（僅在明確要求時） | - | - | - | 是（強制） |

## 常見的合理化藉口

| 藉口 | 現實 |
|------|------|
| "測試這個會話早先通過過" | 在**你即將集成的那棵樹上**跑測試套件。一次綠色運行只能證明它當時跑的那棵樹。 |
| "他們顯然是想合併的" | 集成是你人類夥伴的決定。把菜單擺出來，然後等。 |
| "他們看起來對這個功能收工了——我提議丟棄吧" | 菜單就是原文那樣，不多不少。丟棄只在你的人類夥伴用明確的話提出時才發生。 |
| "'嗯，刪掉吧'算確認了" | 只有輸入 `discard` 這個詞才授權刪除。 |
| "PR 已經開了，worktree 現在是礙事的垃圾" | PR 反饋要在那個 worktree 裡修。它得留到工作落地為止。 |
| "另外那個 worktree 看著像過期的——我順手也清了" | 只清理 `.worktrees/` 或 `worktrees/` 之下的 worktree。其餘的都屬於宿主環境。 |
| "合併結果的失敗大概是偶發的" | 合併結果失敗會讓一切停下。在你排查期間，分支和 worktree 原地不動。 |
| "基礎分支明顯就是 main" | 確認分叉點，或者直接問。合併到錯誤的基礎分支，代價很高。 |
| "推送被拒了——force-push 一下就好" | 推送被拒意味著遠端動過了。去排查；只有在你人類夥伴明確要求時才 force-push。 |
