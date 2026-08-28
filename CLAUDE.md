<!-- superpowers-zh:begin (do not edit between these markers) -->
# Superpowers-ZH 中文增強版

本項目已安裝 superpowers-zh 技能框架（20 個 skills）。

## 核心規則

1. **收到任務時，先檢查是否有匹配的 skill** — 哪怕只有 1% 的可能性也要檢查
2. **設計先於編碼** — 收到功能需求時，先用 brainstorming skill 做需求分析
3. **測試先於實現** — 寫代碼前先寫測試（TDD）
4. **驗證先於完成** — 聲稱完成前必須運行驗證命令

## 可用 Skills

Skills 位於 `.claude/skills/` 目錄，每個 skill 有獨立的 `SKILL.md` 文件。

- **brainstorming**: 在任何創造性工作之前必須使用此技能——創建功能、構建組件、添加功能或修改行為。在實現之前先探索用戶意圖、需求和設計。
- **chinese-code-review**: 中文 review 溝通參考——話術模板、分級標註（必須修復/建議修改/僅供參考）、國內團隊常見反模式應對。僅在用戶顯式 /chinese-code-review 時調用，不要根據上下文自動觸發。
- **chinese-commit-conventions**: 中文 commit 與 changelog 配置參考——Conventional Commits 中文適配、commitlint/husky/commitizen 中文模板、conventional-changelog 中文配置。僅在用戶顯式 /chinese-commit-conventions 時調用，不要根據上下文自動觸發。
- **chinese-documentation**: 中文文檔排版參考——中英文空格、全半角標點、術語保留、鏈接格式、中文文案排版指北約定。僅在用戶顯式 /chinese-documentation 時調用，不要根據上下文自動觸發。
- **chinese-git-workflow**: 國內 Git 平臺配置參考——Gitee、Coding.net、極狐 GitLab、CNB 的 SSH/HTTPS/憑據/CI 接入差異與鏡像同步配置。僅在用戶顯式 /chinese-git-workflow 時調用，不要根據上下文自動觸發。
- **dispatching-parallel-agents**: 當面對 2 個以上可以獨立進行、無共享狀態或順序依賴的任務時使用
- **executing-plans**: 當你有一份書面實現計劃需要在單獨的會話中執行，並設有審查檢查點時使用
- **finishing-a-development-branch**: 當實現完成、所有測試通過、需要決定如何集成這份工作時使用
- **mcp-builder**: MCP 服務器構建方法論 — 系統化構建生產級 MCP 工具，讓 AI 助手連接外部能力
- **receiving-code-review**: 收到代碼審查反饋後、實施建議之前使用，尤其當反饋不明確或技術上有疑問時——需要技術嚴謹性和驗證，而非敷衍附和或盲目執行
- **requesting-code-review**: 完成任務、實現重要功能或合併前使用，用於驗證工作成果是否符合要求
- **subagent-driven-development**: 當在當前會話中執行包含獨立任務的實現計劃時使用
- **systematic-debugging**: 遇到任何 bug、測試失敗或異常行為時使用，在提出修復方案之前執行
- **test-driven-development**: 在實現任何功能或修復 bug 時使用，在編寫實現代碼之前
- **using-git-worktrees**: 當需要開始與當前工作區隔離的功能開發，或在執行實現計劃之前使用——通過原生工具或 git worktree 回退機制確保隔離工作區存在
- **using-superpowers**: 在開始任何對話時使用——確立如何查找和使用技能，要求在任何響應（包括澄清性問題）之前調用 Skill 工具
- **verification-before-completion**: 在宣稱工作完成、已修復或測試通過之前使用，在提交或創建 PR 之前——必須運行驗證命令並確認輸出後才能聲稱成功；始終用證據支撐斷言
- **workflow-runner**: 在 Claude Code / OpenClaw / Cursor 中直接運行 agency-orchestrator YAML 工作流——無需 API key，使用當前會話的 LLM 作為執行引擎。當用戶提供 .yaml 工作流文件或要求多角色協作完成任務時觸發。
- **writing-plans**: 當你有規格說明或需求用於多步驟任務時使用，在動手寫代碼之前
- **writing-skills**: 當創建新技能、編輯現有技能或在部署前驗證技能是否有效時使用

## 如何使用

當任務匹配某個 skill 時，使用 `Skill` 工具加載對應 skill 並嚴格遵循其流程。絕不要用 Read 工具讀取 SKILL.md 文件。

如果你認為哪怕只有 1% 的可能性某個 skill 適用於你正在做的事情，你必須調用該 skill 檢查。
<!-- superpowers-zh:end -->
