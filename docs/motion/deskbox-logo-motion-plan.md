# DeskBox Logo Motion Plan

## Motion Brief

DeskBox 是 Windows 11 桌面整理工具，品牌动效应当是 clean, precise, lightweight。现有 logo 是三层斜叠的格子/文件层，最适合表达“把散乱内容收进有序层级”的动作。

动效策略：以层级错峰、轻微归位、低幅度循环为主，避免夸张弹跳、粒子和强装饰。最终帧必须回到当前产品静态 SVG。

## Static SVG Assets

- `deskbox-logo-static.svg`: 当前 `src/DeskBox/Assets/deskbox.svg` 的复制版，作为视觉基准。
- `deskbox-logo-motion-ready.svg`: 同一静态图，但给三层 path 增加了稳定 id：`layer-back`, `layer-middle`, `layer-front`。

## Candidate Motions

1. `deskbox-motion-01-layer-assemble.svg`
   - 场景：首次启动、Onboarding 左侧 logo、官网首屏、发布视频开头。
   - 动作：三层格子错峰进入并归位，整体有很轻的 settle。
   - 推荐度：最高，最贴合“桌面格子整理”的产品表达。

2. `deskbox-motion-02-calm-breathe.svg`
   - 场景：设置页空状态、等待权限、加载中、托盘常驻状态提示。
   - 动作：三层低幅度呼吸，周期较长，长时间观看不打扰。
   - 推荐度：适合作为 idle/loading，不建议当主品牌 reveal。

3. `deskbox-motion-03-focus-sheen.svg`
   - 场景：保存设置、完成整理、拖入文件后的成功反馈、菜单 hover。
   - 动作：轻微扫光经过 logo，表达“完成/聚焦/确认”。
   - 推荐度：适合作为一次性微反馈，不建议持续循环。

4. `deskbox-motion-04-tray-summon.svg`
   - 场景：从系统托盘打开窗口、显示全部格子、临时置顶提示。
   - 动作：从右下角缩小状态唤起，再展开三层。
   - 推荐度：适合托盘相关路径，语义很明确。

## Product Placement

- Onboarding: 用 `01-layer-assemble` 替换首屏静态 logo 或右侧演示区里出现的品牌标识。
- Tray show/hide all: 用 `04-tray-summon` 作为短反馈，播放一次即可。
- Settings saved / organize completed: 用 `03-focus-sheen` 做 300-700ms 的微反馈。
- Empty/loading state: 用 `02-calm-breathe`，循环最多保持低幅度。

## Implementation Notes

- 当前产物是 SVG/CSS 预览，未接入 WPF/WinUI。
- 后续接入 Windows App SDK 时，建议把三层 path 保持独立，使用 Composition animation 或 XAML Storyboard 控制 opacity/scale/translate。
- 减少动画用户应直接显示 `deskbox-logo-static.svg` 的最终帧。
- 不建议在高频操作里反复播放 1 秒以上的 reveal；微交互应控制在 150-450ms。
