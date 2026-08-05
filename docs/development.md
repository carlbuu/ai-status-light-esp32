# 开发、构建与发布

## 项目结构

```text
.
├─ .github/workflows/       GitHub Actions
├─ assets/                  应用图标
├─ docs/                    用户和开发文档
├─ portable/                便携安装、卸载脚本
├─ sketch_jul16a/           ESP32-C3 固件
└─ windows/                 Windows 桥接程序、界面和构建脚本
```

Windows 程序使用 .NET Framework 自带编译器构建，目标电脑不需要额外安装 .NET SDK。

## 本地构建

在项目根目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\windows\build.ps1
```

指定发布版本：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\windows\build.ps1 -Version 4.1.3
```

版本必须为 `dev` 或 `X.Y.Z` 格式。

## 构建产物

```text
windows\publish\CodexStatusBridge.exe
CodexStatusLight-OneClick.exe
CodexStatusLight-portable.zip
```

这些文件由构建生成，已在 `.gitignore` 中排除，不应直接提交。

## 自检范围

构建脚本同时检查：

- Codex/Cursor 状态事件映射；
- Windows UTF-8/BOM Hook 输入和 Cursor 权限 JSON 输出；
- 多任务计数、双闪/三闪映射和状态优先级；
- Hooks 合并、备份及无关配置保留；
- Codex 与 Cursor 双向切换；
- 亮度、任务动画和平台设置持久化；
- 未读项目解析、确认过滤和自动熄灯；
- 窗口自适应参数；
- 串口被占用时的非阻塞退出；
- 根目录 EXE 与 `windows\publish` EXE 的 SHA-256 一致性。

只有自检输出以 `PASS` 开头时，构建才算成功。

## GitHub Actions 与发布

`.github/workflows/build-release.yml` 会在以下情况构建项目：

- 推送到 `main`；
- 向 `main` 提交 Pull Request；
- 推送 `vX.Y.Z` 标签；
- 手动触发 workflow。

普通构建会上传 Actions artifact。版本标签或手动选择 patch/minor/major 时，workflow 会创建或更新 GitHub Release，并上传单文件版和便携包。

## 发布前检查

1. 固件能够被 Windows 程序识别。
2. Codex 与 Cursor 均可配置、切换和清理 Hooks。
3. 原有的无关 Hooks 保持不变。
4. 工作、权限、完成、错误、熄灯和休眠状态映射正确。
5. 任务数量动画在关闭和开启时均符合预期。
6. 亮度在重连和切换平台后保持。
7. 窗口缩放、换行、滚动和托盘退出正常。
8. 开机启动可以添加并完整移除。
9. 单文件版和便携 ZIP 内容完整。
10. README 中的下载、安装和故障排查链接有效。
