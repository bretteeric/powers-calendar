---
name: chinese-git-workflow
description: 國內 Git 平臺配置參考——Gitee、Coding.net、極狐 GitLab、CNB 的 SSH/HTTPS/憑據/CI 接入差異與鏡像同步配置。僅在用戶顯式 /chinese-git-workflow 時調用，不要根據上下文自動觸發。
version: "1.0.0"
license: MIT
metadata:
  hermes:
    tags: [git, chinese]
---

# 國內 Git 工作流規範

## 概述

國內團隊用 Git 經常踩的坑：GitHub 訪問不穩定、CI/CD 方案照搬國外水土不服、commit message 中英混雜沒有規範。本技能提供一套**完整適配國內平臺和團隊習慣的 Git 工作流**。

**核心原則：** 工作流服務於團隊效率，不是為了流程而流程。選適合團隊規模的，別硬套大廠方案。

## 國內 Git 平臺適配

### 平臺對比

| 特性 | Gitee | Coding.net | 極狐 GitLab | CNB | GitHub |
|------|-------|------------|-------------|-----|--------|
| 國內訪問 | 快 | 快 | 快 | 快 | 不穩定 |
| 免費私有倉庫 | 有 | 有 | 有 | 有 | 有 |
| CI/CD | Gitee Go | Coding CI | 內置 GitLab CI | 內置（.cnb.yml） | GitHub Actions |
| 代碼審查 | PR | MR | MR | MR | PR |
| 製品庫 | 有限 | 完整 | 完整 | 完整 | Packages |
| 適合場景 | 開源/小團隊 | 中大型團隊 | 企業私有化 | 雲原生 / Docker 流水線 | 國際項目 |

### Gitee 特有配置

```bash
# 設置 Gitee 遠程倉庫
git remote add origin https://gitee.com/<org>/<repo>.git

# Gitee 的 SSH 配置
# ~/.ssh/config
Host gitee.com
    HostName gitee.com
    User git
    IdentityFile ~/.ssh/gitee_rsa
    PreferredAuthentications publickey

# 同時推送到 Gitee 和 GitHub（鏡像同步）
git remote set-url --add --push origin https://gitee.com/<org>/<repo>.git
git remote set-url --add --push origin https://github.com/<org>/<repo>.git
```

### Coding.net 特有配置

```bash
# Coding 的倉庫地址格式
git remote add origin https://e.coding.net/<team>/<project>/<repo>.git

# Coding 支持的 SSH 地址
git remote add origin git@e.coding.net:<team>/<project>/<repo>.git
```

### 極狐 GitLab 特有配置

```bash
# 極狐 GitLab 私有化部署常見地址格式
git remote add origin https://jihulab.com/<group>/<repo>.git

# 或者企業內部部署
git remote add origin https://gitlab.yourcompany.com/<group>/<repo>.git
```

### CNB（Cloud Native Build）特有配置

```bash
# CNB 倉庫地址（僅支持 HTTPS，不提供 SSH 協議）
git remote add origin https://cnb.cool/<org>/<repo>.git

# HTTPS 認證：用戶名固定為 cnb，密碼為個人訪問令牌（Access Token）
# 在 CNB 平臺 → 個人設置 → 訪問令牌 中生成
git config credential.helper store
```

## 工作流選擇

### 方案一：主幹開發（Trunk-Based Development）

**適合：** 小團隊（2-8 人）、迭代速度快、有完善的自動化測試。

```
main ──●──●──●──●──●──●──●──●──●──
        \   /  \   /       \   /
feat/x  ●─●   ●─●    fix/y ●─●
（短命分支，1-2 天內合回）
```

**規則：**
- 主幹（main）始終保持可發佈狀態
- 功能分支生命週期不超過 2 天
- 每天至少合併一次到主幹
- 用 Feature Flag 控制未完成功能的可見性

```bash
# 從 main 拉分支
git checkout -b feat/user-login main

# 開發完成後，rebase 到最新 main
git fetch origin
git rebase origin/main

# 提交 PR/MR，合併後刪除分支
```

### 方案二：Git Flow（經典分支模型）

**適合：** 中大團隊、版本發佈節奏固定（如雙週迭代）、需要維護多個版本。

```
main     ──●────────────────●────────────── 生產環境
            \              / \
release     ●──●──●──●──●    ●──●──●──●── 發佈分支
            \              /
develop  ──●──●──●──●──●──●──●──●──●──●── 開發主線
             \   /  \       /
feat/x       ●─●    ●─────●               功能分支
                      \   /
                  fix/y ●─●                修復分支
```

**分支說明：**
- `main` — 生產環境代碼，只接受 release 和 hotfix 的合併
- `develop` — 開發主線，功能分支從這裡拉出，合回這裡
- `release/*` — 發佈分支，從 develop 拉出，只修 bug 不加功能
- `feat/*` — 功能分支
- `hotfix/*` — 緊急修復，從 main 拉出，同時合回 main 和 develop

### 方案三：國內團隊常用簡化流程

**適合：** 大多數國內中小團隊的實際情況。

```
main     ──●──────●──────●──── 生產環境（受保護）
            \    / \    /
dev      ──●──●─●──●──●─●──── 開發/測試環境
             \  /    \  /
feat/x       ●●      ●●       功能分支
```

**規則：**
- `main` 分支受保護，只能通過 PR/MR 合併
- `dev` 分支對應測試環境，自動部署
- 功能分支從 `dev` 拉出，合回 `dev`
- `dev` 測試通過後，合併到 `main` 進行發佈

## 分支命名規範

### 國內團隊常用命名

```bash
# 功能分支
feat/user-login              # 新功能
feat/JIRA-1234-order-refund  # 關聯任務編號

# 修復分支
fix/payment-callback         # Bug 修復
fix/JIRA-5678-null-pointer   # 關聯 Bug 編號

# 發佈分支
release/v2.1.0               # 版本發佈
release/2024-03-sprint       # 按迭代命名

# 緊急修復
hotfix/v2.0.1                # 線上緊急修復
hotfix/fix-login-crash       # 描述性命名

# 個人分支（部分團隊使用）
dev/zhangsan/feat-login      # 個人開發分支
```

### 命名規則

1. 全部小寫，用 `-` 連接單詞（不用下劃線或駝峰）
2. 前綴明確分支類型：`feat/`、`fix/`、`hotfix/`、`release/`
3. 關聯任務管理平臺的編號（如有）：`feat/TAPD-12345-description`
4. 長度適中，能看出分支目的即可

## 中文 Commit Message 規範

### 約定式提交（Conventional Commits）中文版

```
<類型>(<範圍>): <簡要描述>
                                    ← 空行
<正文（可選）>
                                    ← 空行
<腳註（可選）>
```

### 類型清單

| 類型 | 說明 | emoji（可選） |
|------|------|--------------|
| feat | 新增功能 | ✨ |
| fix | 修復 Bug | 🐛 |
| docs | 文檔更新 | 📝 |
| style | 代碼格式（不影響邏輯） | 💄 |
| refactor | 重構（不是新功能也不是修 Bug） | ♻️ |
| perf | 性能優化 | ⚡ |
| test | 測試相關 | ✅ |
| build | 構建系統或外部依賴 | 📦 |
| ci | CI/CD 配置 | 👷 |
| chore | 其他雜項 | 🔧 |
| revert | 回滾 | ⏪ |

### 好的 commit message

```
feat(購物車): 支持批量刪除商品

- 新增全選/反選功能
- 刪除操作增加二次確認彈窗
- 批量刪除接口使用 POST /cart/batch-delete

關聯需求：TAPD-12345
```

```
fix(支付): 修復微信支付在 iOS 16 上無法喚起的問題

原因：微信 SDK 8.0.33 版本在 iOS 16 上 Universal Links 校驗邏輯變更，
導致 openURL 回調失敗。

方案：升級 SDK 至 8.0.38，並更新 Associated Domains 配置。

Closes #567
```

### 不好的 commit message

```
# 太籠統
update code
fix bug
修改了一些東西

# 沒有上下文
fix: 修復問題
feat: 新增功能

# 中英混雜無規範
fix：修復了一個bug，因為user login的時候會crash
```

## CI/CD 平臺適配

### Gitee Go

```yaml
# .gitee/pipelines/pipeline.yml
name: 構建與測試
displayName: '構建與測試流水線'

triggers:
  push:
    branches:
      include:
        - main
        - dev

stages:
  - name: 測試
    jobs:
      - name: 單元測試
        steps:
          - step: npmbuild@1
            name: install_and_test
            displayName: '安裝依賴並執行測試'
            inputs:
              nodeVersion: 20
              commands:
                - npm ci
                - npm test
```

### Coding CI

```groovy
// Jenkinsfile（Coding CI 支持 Jenkinsfile 語法）
pipeline {
    agent any

    stages {
        stage('安裝依賴') {
            steps {
                sh 'npm ci'
            }
        }

        stage('單元測試') {
            steps {
                sh 'npm test'
            }
        }

        stage('構建') {
            steps {
                sh 'npm run build'
            }
        }

        stage('部署到測試環境') {
            when {
                branch 'dev'
            }
            steps {
                sh './scripts/deploy-staging.sh'
            }
        }

        stage('部署到生產環境') {
            when {
                branch 'main'
            }
            steps {
                sh './scripts/deploy-production.sh'
            }
        }
    }

    post {
        failure {
            // 企業微信/釘釘通知
            sh './scripts/notify-failure.sh'
        }
    }
}
```

### 極狐 GitLab CI

```yaml
# .gitlab-ci.yml
stages:
  - test
  - build
  - deploy

variables:
  NODE_IMAGE: node:20-alpine
  # 使用國內鏡像加速
  NPM_REGISTRY: https://registry.npmmirror.com

單元測試:
  stage: test
  image: $NODE_IMAGE
  script:
    - npm config set registry $NPM_REGISTRY
    - npm ci
    - npm test
  coverage: '/Lines\s*:\s*(\d+\.?\d*)%/'

構建:
  stage: build
  image: $NODE_IMAGE
  script:
    - npm config set registry $NPM_REGISTRY
    - npm ci
    - npm run build
  artifacts:
    paths:
      - dist/

部署測試環境:
  stage: deploy
  script:
    - ./scripts/deploy-staging.sh
  only:
    - dev
  environment:
    name: staging

部署生產環境:
  stage: deploy
  script:
    - ./scripts/deploy-production.sh
  only:
    - main
  environment:
    name: production
  when: manual  # 生產環境手動觸發
```

### CNB（Cloud Native Build）

```yaml
# .cnb.yml — branch-first 結構，直接指定 Docker 鏡像跑流水線
main:
  push:
    - docker:
        image: node:20
      stages:
        - npm ci
        - npm test
        - npm run build
  pull_request:
    - docker:
        image: node:20
      stages:
        - npm run lint
        - npm test
```

**特點：**
- 每個流水線獨立指定 Docker 鏡像，天然雲原生
- 支持 `push` / `pull_request` 觸發
- 同一事件可並行多條流水線
- `stages` 也支持 `- name: xxx` + `script:` 的展開形式，複雜場景見官方文檔

### GitHub Actions 國內替代方案對照

| GitHub Actions 功能 | Gitee Go | Coding CI | 極狐 GitLab CI | CNB |
|---------------------|----------|-----------|----------------|-----|
| 觸發條件 | triggers | Jenkinsfile triggers | only/rules | push / pull_request |
| 緩存依賴 | cache step | stash/unstash | cache | 見官方文檔 |
| 製品存儲 | artifacts | 製品庫 | artifacts | 見官方文檔 |
| 環境變量 | env | environment | variables | env |
| 密鑰管理 | 環境變量配置 | 憑據管理 | CI/CD Variables | Access Token |
| 手動觸發 | 手動運行 | 手動觸發 | when: manual | 頁面手動運行 |

## PR/MR 描述模板

### 中文模板

在倉庫中創建 PR/MR 模板文件：

**Gitee：** `.gitee/PULL_REQUEST_TEMPLATE.md`

**Coding / GitLab：** `.gitlab/merge_request_templates/default.md`

```markdown
## 變更說明

<!-- 簡要描述這次改動做了什麼，解決了什麼問題 -->

## 變更類型

- [ ] 新功能（feat）
- [ ] Bug 修復（fix）
- [ ] 重構（refactor）
- [ ] 性能優化（perf）
- [ ] 文檔更新（docs）
- [ ] 其他：

## 關聯信息

- 需求/Bug 鏈接：
- 設計文檔：

## 改動範圍

<!-- 列出主要改動的模塊和文件 -->

## 測試情況

- [ ] 單元測試通過
- [ ] 手動測試通過
- [ ] 相關模塊迴歸測試通過

## 測試方法

<!-- 描述如何驗證這次改動 -->

## 影響範圍

<!-- 這次改動可能影響哪些功能？是否需要通知其他團隊？ -->

## 部署注意事項

- [ ] 需要執行數據庫遷移
- [ ] 需要更新配置文件
- [ ] 需要更新環境變量
- [ ] 無特殊注意事項

## 截圖/錄屏

<!-- 如果涉及 UI 變更，貼截圖或錄屏 -->
```

## 常用 Git 配置

### 國內環境優化

```bash
# 設置用戶信息
git config --global user.name "張三"
git config --global user.email "zhangsan@company.com"

# commit message 編輯器設置為 VS Code
git config --global core.editor "code --wait"

# 解決中文文件名顯示為轉義字符的問題
git config --global core.quotepath false

# 設置默認分支名
git config --global init.defaultBranch main

# 代理設置（如果需要同時使用 GitHub）
git config --global http.https://github.com.proxy socks5://127.0.0.1:7890

# NPM 使用國內鏡像
npm config set registry https://registry.npmmirror.com
```

### .gitignore 國內項目常見配置

```gitignore
# IDE
.idea/
.vscode/
*.swp

# 依賴
node_modules/
vendor/

# 構建產物
dist/
build/
*.exe

# 環境配置
.env
.env.local
.env.*.local

# 系統文件
.DS_Store
Thumbs.db
desktop.ini

# 國內平臺特有
.coding/
```

## 檢查清單

在推送代碼前，確認：

- [ ] 分支命名符合團隊規範
- [ ] commit message 格式正確，類型和範圍準確
- [ ] 關聯了對應的需求/Bug 編號
- [ ] PR/MR 描述填寫完整
- [ ] CI 流水線通過
- [ ] 已請求相關同事 Review
