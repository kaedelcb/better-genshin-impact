# 公版优选工作流（自动执行）

## 触发条件

用户说"优选公版提交"、"优选公版"或类似含义时，**自动执行**本流程，无需用户额外指示。

## 前置检查

1. 定位目标文件：公版提交 diff 涉及的文件列表
2. 对每个文件，检查头部是否有以下标记：
   - `TeaBag Originals`（#region 标注的公版原始代码区）
   - `TeaBag Extensions`（#region 标注的茶包本地扩展区）
   - 基线 commit 注释（`基线上游 commit:`）

## 流程

### 如果文件**未规范化**（没有 region 标记）

1. **先做规范化**：
   - 在文件头部添加注释块，标注基线 commit
   - 将茶包新增的方法/字段用 `#region TeaBag Extensions` 包裹
   - 将公版原始方法保持为 `#region TeaBag Originals`
   - **不改变任何逻辑行为**
2. 再执行优选（见下方）

### 如果文件**已规范化**（有 region 标记）

1. 读取文件头标注的基线 commit
2. `git diff <基线commit> <新commit> -- <文件>` 拿到公版自己的 diff
3. 将 diff 应用到 `#region TeaBag Originals` 区域
4. `#region TeaBag Extensions` 区域**不动**
5. 如果有共享配置/UI/I18n 文件的冲突，按需解决

### 编译验证

- `dotnet build BetterGenshinImpact/BetterGenshinImpact.csproj -c Debug`
- 0 error 为通过

### 提交

用户说"优选"时自动提交，格式：
```
fix: 优选公版<提交描述>(优选公版)
```

## 已规范化的文件清单

| 文件 | 基线 commit | 最近优选 |
|------|-------------|----------|
| BetterGenshinImpact/GameTask/AutoTrackPath/TpTaskOfficial.cs | 9f82e8234 | 494126996 |