# 参与贡献

欢迎提交问题、改进建议和 Pull Request。

## 提交问题

请先确认问题在最新 Release 和最新固件中仍然存在，并尽量提供：

- 软件版本和固件版本；
- Windows 版本；
- 使用的平台（Codex 或 Cursor）；
- ESP32-C3 型号和串口号；
- 可重复的操作步骤；
- 预期结果与实际结果；
- 脱敏后的 `%LOCALAPPDATA%\CodexStatusLight\bridge.log` 相关内容。

请勿提交包含用户名、文件路径、会话内容、Token 或其他敏感信息的完整配置和日志。

## 提交代码

1. 从最新的 `main` 创建功能分支。
2. 将改动限制在一个明确主题内。
3. 如行为发生变化，同步更新 README 或 `docs/` 中的相关说明。
4. 在项目根目录运行完整构建与自检：

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\windows\build.ps1
   ```

5. 在 Pull Request 中说明变更原因、验证方法和可能影响。

生成的 EXE、ZIP 和 `windows/publish/` 文件不应提交。

## 文档约定

- 面向用户的说明使用简体中文和 UTF-8 编码。
- 文档文件名使用小写英文和连字符。
- 命令、路径、文件名和配置键使用反引号标记。
- README 保持简洁，细节放入 `docs/` 并互相链接。
