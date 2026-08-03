# AI 工作状态指示灯

这个项目把 Codex 或 Cursor 的工作状态显示到 ESP32-C3 的红、黄、绿 LED 上。Windows 端是单文件程序，界面中只能选择一个当前平台；未选中的平台不会再控制灯光。

## 最简单的使用方法

1. 给 ESP32-C3 烧录 `sketch_jul16a.ino`，并在 Arduino IDE 中把 **USB CDC On Boot** 设为 **Enabled**。
2. 关闭 Arduino IDE 的串口监视器。
3. 双击 `CodexStatusLight-OneClick.exe`。
4. 在“选择 AI 平台”中选 **Codex** 或 **Cursor**。
5. 根据需要勾选“按运行任务数量闪烁黄灯”和“开机自动启动”，然后点击“应用并配置”。
6. 完全退出并重新打开刚刚选择的平台。
7. 在“灯光测试”区域拖动“亮度”滑块，可在 5%～100% 之间实时调节并自动保存。

以后切换平台时，只需重新打开控制面板、选择另一个平台并点击“应用并配置”。程序会删除自身在旧平台中的 Hook，只在新平台中添加 Hook，并保留两边其他软件或用户自己的 Hook。

## 灯光含义

| 状态 | LED |
| --- | --- |
| 任务正在运行，未开启按数量闪烁 | 黄灯常亮 |
| 开启按数量闪烁，1 个任务正在运行 | 黄灯常亮 |
| 开启按数量闪烁，2 个任务正在运行 | 黄灯连续闪 2 次，然后停顿并重复 |
| 开启按数量闪烁，3 个及以上任务正在运行 | 黄灯连续闪 3 次，然后停顿并重复 |
| 任一任务等待用户允许 | 绿灯闪烁 |
| 没有运行任务，但有未点击的完成项目（项目带黄点） | 绿灯常亮 |
| 完成项目均已点击，且没有运行或待检查项目 | 自动熄灭 |
| 报错、断线或心跳超时 | 红灯常亮 |
| 关闭显示 | 全部熄灭 |

GPIO2、GPIO3、GPIO4 分别连接红、黄、绿 LED，均为高电平点亮。串口波特率为 115200。
固件版本 4 使用 2 kHz、8 位硬件 PWM 调节三路 LED 亮度，不需要改变现有接线。亮度设置由 Windows 程序保存并在每次连接设备后恢复；“关闭显示”仍会把 LED 完全熄灭。
关闭显示后桥接程序仍会发送心跳，因此设备不会因心跳超时重新亮红灯。

## Codex 与 Cursor 的区别

### Codex

- 用户 Hook 文件：`%USERPROFILE%\.codex\hooks.json`
- 程序还会监测 `%USERPROFILE%\.codex\sessions`，用于补充桌面版的开始和完成状态。
- 会从多个 Codex 会话中统计当前运行任务数量。
- 等待授权时显示绿灯闪烁，并优先于任务数量动画。
- 从 `%USERPROFILE%\.codex\.codex-global-state.json` 读取带黄点的未读项目；点击项目、黄点消失后会自动熄灯（前提是没有其他运行或待检查项目）。

### Cursor

- 用户 Hook 文件：`%USERPROFILE%\.cursor\hooks.json`
- 使用 Cursor 官方的会话、提示词、工具、Shell、MCP 和停止事件。
- 支持按 Cursor 会话和任务标识统计并行任务，完成事件会立即清除对应任务。
- 为了让“等待允许”与绿色闪烁严格同步，选择 Cursor 后，Shell、MCP、网页搜索和网页读取操作会通过 Hook 返回 `ask`，因此 Cursor 会弹出允许提示；选择回 Codex 后，这些 Hook 会自动移除。
- Windows 上会按 UTF-8/BOM 安全解析 Cursor Hook 输入；遇到损坏的 JSON 时会恢复会话元数据，无法恢复则忽略，避免产生无法结束的虚假任务。Hook 外层直接返回权限 JSON，避免图形 EXE 丢失标准输出。
- 从 Cursor 的 `composerHeaders.hasUnreadMessages` 读取待检查项目；点击对应项目后自动更新。
- 如果完成时 Cursor 对话已经打开，绿灯会亮约 3 秒作为完成提示；未读项目仍会让绿灯保持常亮直到查看。
- Cursor Hooks 官方说明：<https://cursor.com/cn/docs/hooks>

## 安全切换与卸载

- 当前平台、亮度和“按运行任务数量闪烁黄灯”开关保存在 `%LOCALAPPDATA%\CodexStatusLight\settings.json`。
- 修改现有 Hook 文件前会生成带时间戳的备份。
- 只删除命令中包含本程序 EXE 的 Hook，其他 Hook 会原样保留。
- 便携版卸载会同时清理 Codex 和 Cursor 中属于本程序的 Hook。
- “开机自动启动”只作用于当前 Windows 用户，可以随时在界面取消；取消时也会清理旧版本遗留的启动项。

## 界面与托盘

- 窗口放大时，字体、按钮和间距会同步放大。
- 窗口变窄时，状态区和设置区会自动换行；高度不足时可以滚动。
- 点击窗口右上角关闭会隐藏到系统托盘。
- 真正退出请点击界面中的“退出程序”，或右键托盘图标选择“退出”。

## 分发文件

- `CodexStatusLight-OneClick.exe`：推荐，复制这一个文件即可使用。
- `CodexStatusLight-portable.zip`：包含 EXE、安装和卸载脚本。
- `windows/publish/CodexStatusBridge.exe`：构建输出。
- `sketch_jul16a.ino`：ESP32-C3 固件。

界面会显示“待检查项目”数量，并提供“全部标记已检查”作为备用操作。该按钮只让状态灯忽略当前黄点，不会修改或删除 Codex/Cursor 项目。

## 构建与自检

在项目根目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\windows\build.ps1
```

构建会生成上述三个 Windows 分发文件，并自动检查：

- Codex/Cursor 状态事件映射
- Windows UTF-8/BOM Hook 输入和 Cursor 权限 JSON 输出
- 多任务数量统计、可选双闪/三闪映射和状态优先级
- 保留无关 Hook
- Codex 与 Cursor 双向切换
- 窗口自适应参数
- 串口锁被占用时仍能快速退出

## 常见问题

- **一直红灯**：确认固件已上传、USB 数据线正常、串口监视器已关闭，然后点击“刷新端口”和“连接设备”。
- **状态不变化**：点击“应用并配置”后，必须完全退出并重新打开所选平台。
- **亮度滑块无效**：设备必须烧录签名为 `CODEX_STATUS_LIGHT:4` 的新固件；界面显示旧固件时请重新烧录本项目固件。
- **Cursor 频繁询问工具权限**：这是 Cursor 模式为了显示绿色“等待允许”闪烁而对 Shell、MCP、网页搜索和网页读取启用的行为。切回 Codex 即会移除。
- **无法取消开机启动**：取消勾选后点击“应用并配置”。新版会同时清理注册表和旧启动文件夹项目。
- **窗口右上角关闭后仍在运行**：这是托盘常驻设计；使用“退出程序”或托盘菜单退出。
- **查看日志**：右键托盘图标选择“打开日志”，文件位于 `%LOCALAPPDATA%\CodexStatusLight\bridge.log`。
