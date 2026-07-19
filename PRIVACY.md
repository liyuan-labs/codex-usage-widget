# 隐私与本地数据边界

Codex 额度悬浮窗在本机处理额度与 Token 统计。程序自身不包含遥测上传功能，也不会把本地会话内容、凭据或统计结果提交到本仓库；它会启动用户已安装的 Codex CLI `app-server`，Codex 本身的网络与隐私行为仍受用户的 OpenAI/Codex 配置和条款约束。

## 程序读取什么

- 通过本机 Codex CLI 的 `app-server` 调用 `account/rateLimits/read` 获取额度窗口；
- 在额度接口不可用时，从本机 Codex 会话日志读取最近的 `rate_limits` 快照；
- 为计算 Token 消耗，会顺序扫描本地 JSONL，但仅提取并在内存中保留用于统计的 `token_count`、模型标识、会话边界和时间戳元数据。

## 程序不会做什么

- 不读取、复制或上传 `auth.json` 中的登录凭据；
- 不展示、持久化或上传提示词和回复正文；
- 不把本地会话日志、设置文件、数据库或统计结果写入项目目录；
- 不向第三方统计服务发送遥测。

窗口设置仅保存在 `%LOCALAPPDATA%\CodexQuotaWidget\settings.json`；删除该文件即可重置本工具的本地设置。金额为依据公开 Standard API 单价计算的本地等价估算，不是订阅账单。

## 提交前检查

仓库的 `.gitignore` 会排除常见的 Codex 会话、认证、环境变量、SQLite 数据库、日志和构建产物。提交者仍应在推送前执行秘密扫描，并确认暂存区中没有本地配置或会话文件。
