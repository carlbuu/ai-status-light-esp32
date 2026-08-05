# 安装、接线与卸载

本文面向首次安装或迁移到另一台 Windows 电脑的用户。

## 准备工作

- Windows 10 或 Windows 11
- ESP32-C3 开发板
- 红、黄、绿 LED 各一个
- 每路 LED 对应的限流电阻
- 支持数据传输的 USB 线
- Arduino IDE
- Codex 或 Cursor

## 烧录固件

1. 在 Arduino IDE 中打开 [`sketch_jul16a/sketch_jul16a.ino`](../sketch_jul16a/sketch_jul16a.ino)。
2. 选择实际使用的 ESP32-C3 开发板和端口。
3. 将 **USB CDC On Boot** 设置为 **Enabled**。
4. 上传固件。
5. 上传完成后关闭串口监视器，避免串口被占用。

当前固件设备签名为 `CODEX_STATUS_LIGHT:5`。Windows 程序仍可识别版本 1～4，但部分亮度、动画或休眠功能会降级。

## LED 接线

| LED | ESP32-C3 引脚 | 点亮方式 |
| --- | --- | --- |
| 红灯 | GPIO2 | 高电平 |
| 黄灯 | GPIO3 | 高电平 |
| 绿灯 | GPIO4 | 高电平 |

每路 LED 都应串联与 LED 和供电电压匹配的限流电阻，并与开发板共地。固件使用 2 kHz、8 位 LEDC 硬件 PWM，不需要为亮度调节改变接线。

## 单文件版（推荐）

1. 从 [GitHub Releases](https://github.com/yzy9527/ai-status-light-esp32/releases/latest) 下载 `CodexStatusLight-OneClick.exe`。
2. 双击 EXE 打开控制面板。
3. 选择 Codex 或 Cursor。
4. 按需启用“按运行任务数量闪烁黄灯”和“开机自动启动”。
5. 点击“应用并配置”。
6. 完全退出并重新打开所选平台，使 Hooks 生效。
7. 选择自动扫描或指定 COM 口，然后点击“连接设备”。
8. 使用灯光测试和亮度滑块验证设备。

单文件版也可以复制到另一台电脑直接运行。

## 便携安装包

1. 下载并解压 `CodexStatusLight-portable.zip`。
2. 双击 `安装到此电脑.cmd`。
3. 首次安装默认选择 Codex；再次安装会沿用当前电脑保存的平台设置。
4. 安装完成后可在控制面板中切换平台。

安装脚本会：

- 将 EXE 复制到 `%LOCALAPPDATA%\CodexStatusLight`。
- 配置当前选中的 Codex 或 Cursor Hooks。
- 移除旧平台中属于本程序的 Hooks。
- 保留两个平台中的其他 Hooks，并在修改前创建备份。
- 按当前设置添加或取消用户级开机启动。

## 切换平台

1. 打开控制面板。
2. 选择另一个平台。
3. 点击“应用并配置”。
4. 完全退出并重新打开新平台。

程序会从旧平台移除自身 Hooks，只向新平台添加 Hooks，不会删除用户或其他软件的 Hooks。

## 卸载

便携版可双击 `卸载.cmd`。卸载过程会清理：

- Codex 和 Cursor 中属于本程序的 Hooks；
- 当前用户的开机启动项；
- 安装到 `%LOCALAPPDATA%\CodexStatusLight` 的 EXE。

其他 Hooks 不会被删除。

单文件版用户应在 EXE 所在目录打开 PowerShell，先执行：

```powershell
.\CodexStatusLight-OneClick.exe --remove-integrations
.\CodexStatusLight-OneClick.exe --uninstall-startup
```

然后退出托盘中的程序并删除 EXE。直接删除正在被 Hooks 引用的 EXE 会留下无效配置。
