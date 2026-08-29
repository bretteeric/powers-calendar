---
name: using-superpowers
description: 在開始任何對話時使用——確立如何查找和使用技能，要求在任何響應（包括澄清性問題）之前調用 Skill 工具
version: "1.0.0"
license: MIT
metadata:
  hermes:
    tags: [meta, getting-started]
---

<SUBAGENT-STOP>
如果你是作為子智能體被分派來執行特定任務的，忽略此技能。
</SUBAGENT-STOP>

<EXTREMELY-IMPORTANT>
如果你認為哪怕只有 1% 的可能性某個技能適用於你正在做的事情，你絕對必須調用該技能。

如果一個技能適用於你的任務，你沒有選擇。你必須使用它。

這不可協商。你不能通過合理化來逃避。
</EXTREMELY-IMPORTANT>

## 規則

**在任何響應或操作之前調用相關或被請求的技能**——包括澄清性問題、探索代碼庫、或查看文件之前。如果調用後發現技能不適合當前情況，你不需要使用它。

**在進入 EnterPlanMode 之前：** 如果你還沒有頭腦風暴過，先調用頭腦風暴技能。

然後宣佈"使用 [技能] 來 [目的]"，並嚴格遵循該技能。如果它有檢查清單，為每個條目創建一個待辦。

## 技能優先級

當多個技能都適用時，流程技能優先——它們決定處理方式，然後由實現技能（前端設計等）負責執行。頭腦風暴和系統化調試是 Superpowers 中最常見的流程技能，但這條規則適用於任何流程技能。

- "讓我們構建 X" → 先用 superpowers:brainstorming，再用實現技能。
- "修復這個 bug" → 先用 superpowers:systematic-debugging，再用領域技能。

## 紅線

這些想法意味著停下——你在合理化：

| 想法 | 現實 |
|------|------|
| "這只是一個簡單的問題" | 問題就是任務。檢查技能。 |
| "我需要先了解更多上下文" | 技能檢查在澄清性問題之前。 |
| "讓我先探索一下代碼庫" | 技能告訴你如何探索。先檢查。 |
| "我可以快速查一下 git/文件" | 文件缺少對話上下文。檢查技能。 |
| "讓我先收集信息" | 技能告訴你如何收集信息。 |
| "這不需要正式的技能" | 如果技能存在，就使用它。 |
| "我記得這個技能" | 技能會迭代更新。閱讀當前版本。 |
| "這不算一個任務" | 行動 = 任務。檢查技能。 |
| "技能太小題大做了" | 簡單的事會變複雜。使用它。 |
| "讓我先做這一件事" | 在做任何事之前先檢查。 |
| "這樣做感覺很高效" | 無紀律的行動浪費時間。技能防止這一點。 |
| "我知道那是什麼意思" | 知道概念 ≠ 使用技能。調用它。 |

## 平臺適配

如果你的運行環境在下面列出，請閱讀對應的參考文件獲取特殊說明：

- Codex：`references/codex-tools.md`
- Pi：`references/pi-tools.md`
- Antigravity：`references/antigravity-tools.md`
- Copilot CLI：`references/copilot-tools.md`
- Hermes Agent：`references/hermes-tools.md`
- Qoder：`references/qoder-tools.md`

Gemini CLI 用戶通過 GEMINI.md 自動獲得 `references/gemini-tools.md` 的工具映射。

## 中國特色技能路由

> 🇨🇳 **本節是 superpowers-zh 的增量內容，上游 obra/superpowers 沒有。**
> 用於把中文場景路由到本 fork 原創的 chinese-* 系列 skill。其餘各節均為逐節翻譯。

當檢測到以下場景時，**必須**優先調用對應的中國特色技能：

| 場景 | 調用技能 |
|------|---------|
| 代碼審查且團隊使用中文溝通 | **superpowers:chinese-code-review** |
| 使用 Gitee/Coding/極狐 GitLab | **superpowers:chinese-git-workflow** |
| 編寫中文技術文檔或 README | **superpowers:chinese-documentation** |
| 編寫 git commit message（中文項目） | **superpowers:chinese-commit-conventions** |
| 構建 MCP 服務器/工具 | **superpowers:mcp-builder** |

**判斷依據：**
- 項目中有中文註釋、中文 README、或 .gitee 目錄 → 啟用中文系列技能
- commit 歷史中有中文 → 使用中文提交規範
- 用戶用中文交流 → 所有輸出使用中文，優先考慮中國特色技能

中國特色技能與翻譯技能**疊加使用**，不互斥。例如：做代碼審查時，同時使用 requesting-code-review（流程）+ chinese-code-review（風格）。

## 用戶指令

用戶指令（CLAUDE.md、AGENTS.md、GEMINI.md 等、直接請求）優先於技能，技能又優先於默認行為。只有當你的人類夥伴明確告訴你跳過時，才能跳過技能工作流或指令。
