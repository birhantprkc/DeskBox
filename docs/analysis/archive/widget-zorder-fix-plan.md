# 格子 z-order 一致性整改方案

## 一、问题现象

从托盘临时置顶所有格子后，点击其他窗口时：
- **文件格子**：保持在桌面层上方（正确）
- **随记格子**：被推到最底层，所有窗口之下（错误）

两个格子的显示/消失行为不一致。

---

## 二、完整根因分析

### 2.1 核心机制：三标志状态机

两个格子都使用相同的三标志协议管理 z-order：

| 标志 | 含义 |
|------|------|
| `_isAtDesktopLayer` | 当前是否在桌面层（最底部） |
| `_keepRaisedUntilDeactivate` | 是否保持置顶直到失焦 |
| `_restoreDesktopLayerWhenIdle` | 是否在空闲时恢复桌面层 |

### 2.2 恢复桌面层的三条路径

**路径 A — 鼠标钩子（WidgetManager 集中控制）**
```
托盘唤起 → 安装鼠标钩子 → 用户点击外部 → RestoreRaisedWidgetsForExternalMouseDown
→ RestoreRaisedWidgetsToDesktopLayer(force: true) → 遍历所有格子 PushToBottom
```
- 触发条件：鼠标点击非 DeskBox/非任务栏区域
- 作用范围：所有格子统一处理

**路径 B — 各窗口 Deactivated 事件（各自独立）**
```
格子失焦 → QueueRestoreDesktopLayerIfForegroundLeavesDeskBox → 80ms 延迟
→ 检查前台是否仍是 DeskBox → widgetManager.RequestRestoreRaisedWidgetsToDesktopLayer
→ StartTrayLayerRestoreMonitor → 鼠标钩子 → 路径 A
```
- 触发条件：窗口失去焦点（点击外部、Alt+Tab、Win+D 等）
- 特点：**只对失焦的那个格子触发**，不是对所有格子

**路径 C — Timer 监控**
```
TrayLayerRestoreTimer_Tick（每 40ms）→ InstallTrayLayerMouseHook
```
- 作用：确保鼠标钩子被安装
- 不直接执行恢复

### 2.3 问题 1：`RestoreDesktopLayer` guard 条件不一致

**WidgetWindow** (`WidgetWindow.xaml.cs:1224-1250`):
```csharp
private void RestoreDesktopLayer(bool force = false)
{
    if (!force && !_restoreDesktopLayerWhenIdle && _keepRaisedUntilDeactivate) return;
    if (_isDragging || _isResizing ||
        TitleEditBox.Visibility == Visibility.Visible ||  // ← 额外
        _deletePending ||                                  // ← 额外
        _isDeleteWidgetFlyoutOpen ||                       // ← 额外
        _isInlineFlyoutOpen)                               // ← 额外
    {
        if (force || _restoreDesktopLayerWhenIdle)
            _restoreDesktopLayerWhenIdle = true;
        return;  // ← 跳过 PushToBottom！
    }
    PushToBottom();
}
```

**QuickCaptureWidgetWindow** (`QuickCaptureWidgetWindow.xaml.cs:2289-2310`):
```csharp
private void RestoreDesktopLayer(bool force = false)
{
    if (!force && !_restoreDesktopLayerWhenIdle && _keepRaisedUntilDeactivate) return;
    if (_isDragging || _isResizing)  // ← 只检查这两个
    {
        if (force || _restoreDesktopLayerWhenIdle)
            _restoreDesktopLayerWhenIdle = true;
        return;
    }
    PushToBottom();
}
```

**关键差异**：当文件格子有 flyout 打开（右键菜单、删除确认、重命名等）时，即使 `force=true`，`PushToBottom()` 也会被跳过。但 QuickCapture 没有这些额外检查，总是执行 `PushToBottom()`。

**结果**：鼠标钩子调用 `RestoreRaisedWidgetsToDesktopLayer(force: true)` 时：
- 文件格子：有 flyout → guard 拦截 → 不执行 PushToBottom → 留在原位（看起来"正常"）
- QuickCapture：无 flyout → 执行 PushToBottom → 被推到最底层（异常）

### 2.4 问题 2：`ForceRestoreDesktopLayerFromManager` 实现不一致

**WidgetWindow** (`WidgetWindow.xaml.cs:1218-1222`):
```csharp
public void ForceRestoreDesktopLayerFromManager()
{
    ForceCancelTransientState();  // 清除 _isDeleteWidgetFlyoutOpen, _isInlineFlyoutOpen 等
    RestoreDesktopLayer(force: true);
}
```

**QuickCaptureWidgetWindow** (`QuickCaptureWidgetWindow.xaml.cs:356-361`):
```csharp
public void ForceRestoreDesktopLayerFromManager()
{
    _restoreDesktopLayerWhenIdle = true;
    _keepRaisedUntilDeactivate = false;
    RestoreDesktopLayer(force: true);
}
```

WidgetWindow 版本通过 `ForceCancelTransientState()` 清除了 flyout 标志，理论上应该能让 `PushToBottom()` 执行。但 `ForceCancelTransientState` 不清除 `_deletePending`，所以如果有删除操作正在进行，`PushToBottom()` 仍然会被跳过。

### 2.5 问题 3：Deactivated 触发范围不对称

`Deactivated` 事件**只对失去焦点的那个窗口触发**。如果用户最后交互的是文件格子：
- 文件格子收到 Deactivated → 触发路径 B
- QuickCapture 不收到 Deactivated → 不触发路径 B

如果此时鼠标钩子（路径 A）因为某种原因没有正确触发（例如 `_suppressTrayLayerRestoreUntilUtc` 的 160ms 窗口内），QuickCapture 就不会被恢复。

### 2.6 问题 4：路径竞争

鼠标钩子（路径 A）和 Deactivated handler（路径 B）可以同时触发：
1. 用户点击外部窗口
2. 鼠标钩子立即触发 → `RestoreRaisedWidgetsToDesktopLayer(force: true)` → 所有格子 PushToBottom → `_widgetsRaisedFromTray = false`
3. 80ms 后 Deactivated handler 的回调到达 → 检查 `_widgetsRaisedFromTray` → 已为 false → 请求被忽略

正常情况下路径 A 先执行完，路径 B 被跳过。但如果路径 B 的 80ms 延迟内路径 A 还没执行（例如消息泵繁忙），两条路径可能各自独立执行 PushToBottom，导致重复操作。

### 2.7 问题 5：`_suppressTrayLayerRestoreUntilUtc` 只保护非强制恢复

```csharp
// WidgetManager.cs:1297-1305
private void RestoreRaisedWidgetsToDesktopLayer(bool force)
{
    if (!force &&
        (_isTogglingWidgetsDesktopLayer ||
         DateTime.UtcNow < _suppressTrayLayerRestoreUntilUtc))
    {
        return;  // 非强制恢复被跳过
    }
    // ... 强制恢复不受此限制
}
```

160ms 的抑制窗口只对 `force=false` 的恢复有效。鼠标钩子使用 `force=true`，所以不受抑制。这意味着如果在托盘唤起后的 160ms 内用户快速点击，鼠标钩子可能立即恢复格子。

---

## 三、完整整改方案

### 3.1 修改 1：统一 `RestoreDesktopLayer` 的 guard 条件

**目标**：让两个格子在 `force=true` 时行为一致——都跳过所有 guard 直接执行 `PushToBottom()`。

**WidgetWindow.xaml.cs** — `RestoreDesktopLayer` 方法：

```csharp
private void RestoreDesktopLayer(bool force = false)
{
    if (!force && !_restoreDesktopLayerWhenIdle && _keepRaisedUntilDeactivate)
        return;

    if (!force && (
        _isDragging || _isResizing ||
        TitleEditBox.Visibility == Visibility.Visible ||
        _deletePending ||
        _isDeleteWidgetFlyoutOpen ||
        _isInlineFlyoutOpen))
    {
        _restoreDesktopLayerWhenIdle = true;
        return;
    }

    // force=true 时直接 PushToBottom，不受任何 guard 阻挡
    _keepRaisedUntilDeactivate = false;
    _restoreDesktopLayerWhenIdle = false;
    PushToBottom();
    ApplyBackdropPreference();
}
```

**QuickCaptureWidgetWindow.xaml.cs** — `RestoreDesktopLayer` 方法：

```csharp
private void RestoreDesktopLayer(bool force = false)
{
    if (!force && !_restoreDesktopLayerWhenIdle && _keepRaisedUntilDeactivate)
        return;

    if (!force && (_isDragging || _isResizing))
    {
        _restoreDesktopLayerWhenIdle = true;
        return;
    }

    _keepRaisedUntilDeactivate = false;
    _restoreDesktopLayerWhenIdle = false;
    PushToBottom();
    ApplyBackdropPreference();
}
```

**核心变化**：所有 guard 条件都加 `!force &&` 前缀，确保 `force=true` 时绕过所有 guard。

### 3.2 修改 2：统一 `ForceRestoreDesktopLayerFromManager` 实现

**QuickCaptureWidgetWindow.xaml.cs**：

```csharp
public void ForceRestoreDesktopLayerFromManager()
{
    _restoreDesktopLayerWhenIdle = true;
    _keepRaisedUntilDeactivate = false;
    RestoreDesktopLayer(force: true);
}
```

此实现已经正确。WidgetWindow 的实现也正确（通过 `ForceCancelTransientState` 清除 flyout 标志后调用 `RestoreDesktopLayer(force: true)`）。修改 1 确保了 `force=true` 时两者行为一致。

### 3.3 修改 3：Deactivated handler 中避免独立的 `RestoreDesktopLayer` 兜底

当前两个格子的 `QueueRestoreDesktopLayerIfForegroundLeavesDeskBox` 都有：
```csharp
if (!widgetManager.RequestRestoreRaisedWidgetsToDesktopLayer(...))
{
    RestoreDesktopLayer(force: true);  // WidgetManager 不可用时的兜底
}
```

这个兜底在 WidgetManager 不可用时（例如初始化阶段）仍然需要保留。但要确保它也遵循修改 1 的 `force=true` 行为。由于修改 1 已经让 `force=true` 跳过所有 guard，这里不需要额外改动。

### 3.4 修改 4：WidgetManager 恢复时统一使用 `Force` 路径

**WidgetManager.cs** — `RestoreRaisedWidgetsToDesktopLayer` 方法：

确保鼠标钩子路径（已是 `force=true`）和 Timer/Deactivated 路径都使用一致的行为。当前实现已经正确——鼠标钩子使用 `force: true`，其他路径根据情况选择。

### 3.5 不需要修改的部分

- `PushToBottom()`：两个格子实现完全一致，无需改动
- `HoldTemporaryTopMost()`：两个格子实现完全一致
- `EnsureRaisedFromTrayTopMost()`：两个格子实现完全一致
- `ActivateRaisedFromTrayBatch()`：两个格子实现完全一致
- 鼠标钩子逻辑：已经正确地统一处理所有格子
- `_suppressTrayLayerRestoreUntilUtc`：160ms 抑制窗口设计合理

---

## 四、修改范围汇总

| 文件 | 方法 | 改动 |
|------|------|------|
| `WidgetWindow.xaml.cs` | `RestoreDesktopLayer` | guard 条件加 `!force &&` 前缀 |
| `QuickCaptureWidgetWindow.xaml.cs` | `RestoreDesktopLayer` | guard 条件加 `!force &&` 前缀 |

总共 **2 处修改**，改动量极小，风险可控。

---

## 五、验证场景

| 场景 | 预期行为 |
|------|---------|
| 托盘唤起 → 点击外部窗口 | 所有格子同时回到桌面层 |
| 托盘唤起 → Alt+Tab | 所有格子同时回到桌面层 |
| 托盘唤起 → 右键格子 → 点击外部 | 菜单关闭，所有格子同时回到桌面层 |
| 托盘唤起 → 拖拽格子 → 松手 | 格子在拖拽结束后回到桌面层 |
| 托盘唤起 → 删除格子确认中 → 点击外部 | 删除操作完成后所有格子回到桌面层 |
| 托盘唤起 → 点击任务栏 | 格子保持置顶（不恢复） |
| 托盘唤起 → 点击其他格子 | 格子保持置顶（不恢复） |
