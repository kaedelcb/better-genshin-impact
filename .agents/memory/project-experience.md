# 项目经验记忆

> 每次完成有意义的任务后，自动记录关键经验和模式，供后续任务复用。
> 格式：`- [日期] 场景：经验要点`

## 公版赶路优选

### 公版 vs 茶包版赶路结构
- **公版赶路文件**：`GameTask/AutoPathing/Handler/SkillBoostHelper.cs`（partial class PathExecutor）
- **茶包版赶路文件**：`GameTask/AutoPathing/Handler/TeapotHurryOnHelper.cs`（partial class PathExecutor，不动）
- **路由分叉**：`PathExecutor.cs` 的 MoveTo 主循环中，`PartyConfig.UseNewHurrySystem` 决定走哪套
- **公版配置字段**：`PathingPartyConfig.cs` 中 `UseNewHurrySystem == true` 时生效的字段
- **茶包版配置字段**：**注意**茶包版有独立的 `MwkFlyJumpDistance`（茶包版字段名），公版的是 `MwkJumpFlyDistance`（发音不同：Fly vs JumpFly）

### 公版优选通用步骤
1. 定位公版提交 diff（`git show <commit>`）
2. 将 diff 应用到 `Handler/` 下的对应文件（注意路径差异：公版源在 `AutoPathing/SkillBoostHelper.cs`，你的版本在 `AutoPathing/Handler/SkillBoostHelper.cs`）
3. 公版源文件编码通常为 UTF-16 LE（BOM: FF FE），需转码为 UTF-8 无 BOM 再写入
4. 检查 using 冲突：
   - `using AutoFightOfficial.Model` 与 `using AutoFight.Model` 会产生 `Avatar` 歧义 → 改用完全限定名 `AutoFightOfficial.Model.XXX`
   - `ESkillCdTracker.ApplyFallback` 签名差异：公版有 `log` 参数，当前分支无 → 去掉 `log: false`
5. 检查 `_hurryOnAvatar` 字段是否已在 `PathExecutor.cs` 中声明 → 删除 `SkillBoostHelper.cs` 中的重复声明
6. 配套补全 `PathingPartyConfig.cs` 缺失的字段、XAML 控件、ViewModel 可见性属性
7. 编译验证：`dotnet build BetterGenshinImpact/BetterGenshinImpact.csproj -c Debug`

### 历史记录
- [2026-08-16] 98d7cfd40 refactor: 调整玛薇卡跳飞逻辑细节
  - 玛薇卡跳飞从 `GetMavikaColorDifference` 颜色判定升级为 `GetMavikaESkillIconState` 三态图标识别
  - 新增冲刺跳飞（6命玛薇卡）：`_mavikaSprintJumpCount` + `MwkJumpFlySprintCount` 配置
  - 上车间隔 700ms→300ms，续技能时重置冲刺计数
  - 安全降落条件扩展：接近节点时即使间隔未到也强制落地
  - 下车块从 case 入口下移到跳飞块后
  - 新增 `GetMavikaIconState()` 惰性缓存（跳飞/骑行/禁用冲刺三处共用）
  - 需补字段：`MwkJumpFlyDistance`（int, 75）、`MwkDisableSprintEnabled`（bool, false）、`MwkJumpFlySprintCount`（int, 0）
  - 注意：`ImageFeatureScorer` 依赖 `AutoFightOfficial.Model` 命名空间