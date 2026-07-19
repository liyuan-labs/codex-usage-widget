# 安全说明

## 报告安全问题

请不要在公开 Issue 中粘贴 Token、登录信息、会话日志、个人路径或其他敏感数据。若仓库启用了 GitHub Private Vulnerability Reporting，请通过仓库的 **Security** 页面私下报告；否则只提交不含真实敏感值的最小复现说明。

## 提交者检查清单

提交前请确认：

- `git status --short` 中没有 `auth.json`、`.env`、`settings.json`、JSONL、SQLite、日志或构建目录；
- 搜索结果中没有 GitHub/OpenAI Token、私钥、邮箱、用户名或绝对用户路径；
- 测试 fixture 只使用合成数据，不包含真实会话文本、额度或金额；
- 二进制发布物来自干净源码构建，并在 GitHub Release 中提供 SHA-256。
