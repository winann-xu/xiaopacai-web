# 贡献指南

## 协作模式

- **Claude@50.53**（主开发）：代码编写与 git 提交
- **Codex@50.20**（主测试）：构建验证与回归测试

## 开发流程

1. Claude 在 `50.53:/home/winann/xiaopacai-web` 开发并提交
2. 每次里程碑完成产出 `git bundle` → 同步至 50.20
3. Codex 在 50.20 拉取 bundle、构建、测试
4. 缺陷经 `docs/bridge-out/` 信件回传

## 提交规范

- Commit message 包含阶段标记：`[TASK-WEB-Pn]`
- 中文注释
- 提交前更新 `CHECKPOINT.json`、`PROGRESS.md`、`CHANGELOG.md`

## 代码风格

- 后端：C# 标准命名（PascalCase 公共成员、camelCase 私有成员）
- 前端：Vue 3 Composition API + `<script setup>` + TypeScript
- 中文注释说明业务逻辑
- SQL：大写关键字 + 小写标识符

## 分支策略

- `main` — 主分支（稳定版本）
- 功能分支按阶段创建：`p2-api`、`p3-frontend`、`p4-p2p`、`p5-testing`
