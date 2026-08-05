# 架构与协议

## 系统概览

```text
Codex Hooks / Sessions ─┐
                       ├─> Windows 单实例桥接程序 ─USB Serial─> ESP32-C3 ─> 红黄绿 LED
Cursor Hooks ──────────┘              │
                                     └─ 图形控制面板 / 系统托盘
```

用户在 Windows 程序中选择 Codex 或 Cursor。只有当前平台的事件可以改变 LED，来自另一平台的旧 Hook 消息会被忽略。

## 组成

| 文件 | 用途 |
| --- | --- |
| `sketch_jul16a/sketch_jul16a.ino` | ESP32-C3 固件 |
| `windows/CodexStatusBridge.cs` | 单实例、IPC、状态聚合、串口与安装逻辑 |
| `windows/IntegrationManager.cs` | 平台配置切换、合并、备份与清理 |
| `windows/StatusForm.cs` | WinForms 控制面板和响应式布局 |
| `windows/build.ps1` | 编译、打包和自检 |
| `portable/install.ps1` | 便携安装与卸载 |

## 串口协议

- 115200 bit/s、8N1
- ASCII 文本
- 一行一条命令

| 命令 | 含义 |
| --- | --- |
| `IDENTIFY` | 查询设备签名 |
| `PING` | 刷新心跳 |
| `THINKING` / `WORKING` / `WORKING1` | 1 个任务，黄灯常亮 |
| `WORKING2` | 2 个任务，黄灯双闪 |
| `WORKING3` / `WORKING3PLUS` | 3 个及以上任务，黄灯三闪 |
| `PERMISSION` / `WAITING` | 绿灯闪烁 |
| `COMPLETE` / `IDLE` | 绿灯常亮 |
| `ERROR` | 红灯常亮 |
| `OFF` | 全部熄灭 |
| `SUSPEND` | 熄灯并暂停心跳超时，直到收到下一个状态命令 |
| `BRIGHTNESS 5-100` | 设置三路 LED 的 PWM 亮度百分比 |
| `BRIGHTNESS?` | 查询当前固件亮度 |

当前设备签名为 `CODEX_STATUS_LIGHT:5`。桥接程序兼容版本 1～5：版本 1/2 不支持双闪和三闪，版本 1/2/3 不支持亮度命令，版本 4 不支持 `SUSPEND`。

## Windows 桥接程序

### 单实例与 IPC

- 互斥量：`Local\CodexStatusLightBridge`
- UDP：`127.0.0.1:38451`
- Hook 客户端只向常驻实例发送事件，不直接访问串口。
- 再次打开 EXE 会显示已有实例的窗口。

### 状态聚合

状态按会话保存。Hooks 和 Sessions 同时提供信息时使用两者计数的较大值，避免重复计算同一任务。过期记录会被清理，手动“关闭显示”优先于所有 AI 状态。

状态优先级：

```text
等待允许 > 工作中 > 报错 > 完成未查看 > 已查看/空闲
```

### 待检查项目

- Codex：读取 `.codex-global-state.json` 中的 `unread-thread-ids-by-host-v1`。
- Cursor：只读查询 `state.vscdb` 中的 `composerHeaders.hasUnreadMessages`，并兼容旧版合并 JSON。
- 每秒轮询一次；临时读取失败时保留上一次状态。
- “全部标记已检查”只写入本程序的确认列表，不修改平台数据。

### 串口发现和休眠

自动模式优先尝试 COM15，再扫描其他端口。连接前发送 `IDENTIFY`，只接受本项目设备签名；连接后每 2 秒发送 `PING`。

休眠前发送 `SUSPEND`。唤醒后，如果串口仍打开则重发完整状态，否则立即重新扫描设备。退出路径不等待串口锁，确保托盘退出不会被占用中的串口阻塞。

## 平台配置

设置保存在 `%LOCALAPPDATA%\CodexStatusLight\settings.json`。合法平台值为 `Codex` 或 `Cursor`，缺失时默认使用 Codex；同一文件还保存亮度和任务数量动画开关。

应用平台设置时，程序会：

1. 从 Codex 和 Cursor 配置中移除属于本程序的 Hooks。
2. 只向新平台添加 Hooks。
3. 保留其他 Hooks。
4. 在写入前创建带时间戳的备份。
5. 原子写入配置并清理旧平台的运行时状态。

### Codex 集成

Codex Hooks 位于 `%USERPROFILE%\.codex\hooks.json`，主要事件映射如下：

| 事件 | 状态 |
| --- | --- |
| `UserPromptSubmit` | 工作中 |
| `PermissionRequest` | 等待允许 |
| `PostToolUse` | 工作中 |
| `Stop` | 已完成 |

桥接程序还会增量监测 `%USERPROFILE%\.codex\sessions` 中近期的 JSONL 文件，补充桌面版可能未触发的开始、完成和授权状态。Cursor 模式会停止这项轮询。

### Cursor 集成

Cursor Hooks 位于 `%USERPROFILE%\.cursor\hooks.json`，使用的事件包括：

- `sessionStart`、`sessionEnd`
- `beforeSubmitPrompt`
- `preToolUse`、`postToolUse`、`postToolUseFailure`
- `beforeShellExecution`、`afterShellExecution`
- `beforeMCPExecution`、`afterMCPExecution`
- `stop`

Windows 版 Cursor 的 Hook 输入经标准输入传入。桥接程序按字节读取并识别 UTF-8 BOM 或 UTF-16 BOM，避免系统代码页破坏会话标识。损坏且无法恢复会话元数据的事件会被忽略，不会创建无法结束的 `unknown` 任务。

Shell、MCP、`WebSearch` 和 `WebFetch` 的执行前事件进入等待允许状态，并返回 `{"permission":"ask"}`；执行后恢复工作状态。权限 JSON 由 PowerShell 外层直接写入标准输出，不依赖图形 EXE 的控制台输出句柄。

## 命令行接口

| 参数 | 用途 |
| --- | --- |
| `hook <STATE>` | Codex Hook 客户端 |
| `cursor-hook` | Cursor 普通事件 Hook |
| `cursor-permission-hook` | Cursor 权限事件 Hook |
| `--configure-platform Codex` | 配置 Codex |
| `--configure-platform Cursor` | 配置 Cursor |
| `--remove-integrations` | 清理两个平台中属于本程序的 Hooks |
| `--install-startup` | 启用当前用户开机启动 |
| `--uninstall-startup` | 取消并清理开机启动 |
| `--display-off` | 保持串口心跳并关闭全部 LED |
| `--background` | 后台启动 |
