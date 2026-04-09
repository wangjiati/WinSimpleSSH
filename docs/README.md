# WinSimpleSSH 文档目录

本目录存放项目的**设计过程文档**——特性规划、架构决策、迭代记录。与 `头脑风暴.txt`（最初的需求 brain dump）和 `README.md`（面向用户的使用说明）的区别：

- `../README.md` — 用户视角，怎么用这个工具
- `../头脑风暴.txt` — 项目最初的需求清单
- `./` — 开发过程中的设计决策、每个 feature 的推进记录

## 目录结构

```
docs/
├── README.md                         # 本文件，索引
└── features/                         # 按 feature 划分的设计文档
    └── <feature-name>/
        ├── 00-context.md             # 业务背景与使用场景
        ├── 01-current-state.md       # 代码现状分析
        ├── 02-design-decisions.md    # 设计决策点（给用户拍板的）
        ├── 03-change-list.md         # 精确改动清单
        └── 04-implementation-plan.md # 分阶段落地计划
```

## 约定

- **文件名前缀编号**（`01-`, `02-`…）表达阅读顺序，新增迭代在末尾追加而不是修改已有文件
- 每个 feature 有独立子目录，避免跨 feature 互相污染
- 决策类文档（`02-design-decisions.md`）写明**谁决定、何时决定、决定结果**，而不是空谈方案

## 当前推进中的 Feature

| Feature | 状态 | 目录 |
|---------|------|------|
| 客户端非交互 CLI 模式 | ✅ 全部完成（exec / start / upload / download + UTF-8 + JSON + Ctrl+C） | [features/non-interactive-cli/](./features/non-interactive-cli/) |
