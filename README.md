# Codex 额度悬浮窗

一个轻量的 Windows 置顶悬浮窗，用环形或卡片视图显示 Codex 当前额度窗口的**剩余百分比**、重置倒计时，以及本地会话中的 Token 消耗和 Standard API 等价金额估算。

本仓库只包含源码、测试和设计预览，不包含任何用户会话、额度快照、个人设置、登录凭据或本机构建产物。详细边界见 [PRIVACY.md](./PRIVACY.md)。本项目是社区工具，不是 OpenAI 官方产品，也不代表 OpenAI 的隶属、认可或背书。

![环形额度与可选累计周期预览](./design/circular-style-period-options-preview-v3.png)

_预览图中的额度、Token、金额与时间均为虚构示例数据。_

## 直接使用

双击 [`启动悬浮窗.cmd`](./启动悬浮窗.cmd)。首次从源码启动需要 .NET 8 SDK 自动构建，之后会直接打开本地生成的单文件程序。若仓库发布了预编译版本，请从 GitHub Releases 下载，不要从源码分支获取未知二进制文件。

窗口支持：

- 在“环形”和“卡片”两种额度视图间切换；环形视图同时显示短周期与长周期剩余额度；
- 实时显示短周期与长周期额度（窗口名根据 Codex 返回的分钟数生成，不写死）；
- 显示重置倒计时、套餐类型、可用重置卡，以及存在时的 Credits/个人额度；
- Token 面板支持“当日”与“累积”口径，累积周期可选近 7/30/90 天、全部或自定义日期；
- Token 分为普通输入、缓存读取、可见输出、推理输出四个互斥类别，并显示总量、分布条与金额估算；
- Token 面板可以单独隐藏，之后可从右键菜单或外观设置恢复；
- 无边框、始终置顶、拖动定位；四条边和四个角都可以直接拉伸；默认约为完整设计的 50%（`230 × 244`），最小可缩至 `160 × 120`；
- 主界面通过等比布局缩放；无论横向、纵向拉伸还是压缩，圆环、文字和图标都不会被单轴拉扁；
- 点击齿轮可调整视图风格、Token 面板、累积周期、宽度、高度、整体透明度、强调色和背景色，支持预设色与 `#RRGGBB` 自定义颜色；
- 透明度限制在 50%–100%，浅色背景会自动切换为深色文字，保证基本可读性；
- 右键可刷新、打开外观设置、切换置顶或打开官方 Usage 面板；
- 记住窗口位置、尺寸、透明度、颜色与置顶设置；
- 单实例运行，关闭窗口时会回收后台子进程。

## 额度数据来源与安全

首选数据源是本机 Codex CLI 的 `app-server` 协议：初始化后调用 `account/rateLimits/read`，并监听 `account/rateLimits/updated`。悬浮窗动态寻找当前有效的 `%LOCALAPPDATA%\OpenAI\Codex\bin\*\codex.exe`，不会依赖可能失效的 PATH shim，也不会读取 `auth.json` 或复制登录令牌。

Codex 返回 `usedPercent`（已用比例），窗口显示：

```text
remainingPercent = clamp(100 - usedPercent, 0, 100)
```

如果 app-server 暂时不可用，程序会降级读取 `~\.codex\sessions` 中最近一次 `rate_limits` 快照，并将状态明确标为“缓存”。该本地日志格式属于兼容兜底，不被当作主接口。

官方资料：

- [Codex app-server 协议](https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md#protocol)
- [Codex 的 ChatGPT rate limits 接口](https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md#7-rate-limits-chatgpt)
- [使用 Codex 与套餐额度说明](https://help.openai.com/en/articles/11369540-using-codex-with-chatgpt)

Codex 消耗取决于任务大小和复杂度，因此工具显示服务端给出的剩余百分比，不伪造“还剩多少条消息”。

## Token 统计与金额估算

Token 统计会顺序扫描本机 `~\.codex\sessions` 与 `~\.codex\archived_sessions` 中的 JSONL，但仅提取并在内存中保留用于统计的 `token_count`、模型标识、会话边界及时间戳元数据；不会展示、持久化或上传提示词与回复正文。程序按本地日历日汇总，并对活动/归档重复文件及派生任务继承的父会话历史做去重；未变化的文件会使用内存缓存，避免每次刷新重新扫描全部日志。

四个显示类别互不重叠：

- 普通输入：输入 Token 扣除缓存读取后的部分，其中缓存写入仍属于普通输入显示；
- 缓存读取：`cached_input_tokens`；
- 可见输出：输出 Token 扣除推理 Token；
- 推理输出：`reasoning_output_tokens`。

金额是按每个请求的模型、输入/缓存读取/缓存写入/输出 Token 和公开 Standard API 单价换算的**等价估算**，不是 ChatGPT/Codex 订阅账单或 Credits 实际扣款，也不包含 Web Search、容器等工具费。推理 Token 是输出 Token 的子集并按输出费率计价；缓存读取 Token 是输入 Token 的子集并按缓存价计价。遇到没有公开匹配单价的模型时，界面会显示“部分估算”以及未定价 Token，而不会猜测价格。额度与金额结果均不提供准确性或持续可用性保证，应以 OpenAI 官方界面和账单为准。

内置价表核验日期为 `2026-07-18`；价格会变化，正式计费请始终以官方页面为准：

- [OpenAI API Pricing](https://developers.openai.com/api/docs/pricing)
- [Prompt Caching：缓存 Token 关系与计费](https://developers.openai.com/api/docs/guides/prompt-caching#requirements)
- [Reasoning：推理 Token 关系与计费](https://developers.openai.com/api/docs/guides/reasoning#how-reasoning-works)
- [Codex Tokens 与 Credits 说明](https://learn.chatgpt.com/docs/pricing#what-are-tokens-and-credits)

## 构建、测试与发布

要求 Windows 10/11 与 .NET 8 Desktop Runtime；从源码构建需要 .NET 8 SDK。

```powershell
dotnet build .\src\CodexUsageWidget\CodexUsageWidget.csproj -c Release
dotnet run --project .\tests\CodexUsageWidget.SmokeTests\CodexUsageWidget.SmokeTests.csproj -c Release -- --integration
.\发布.ps1
```

`发布.ps1` 默认生成依赖本机 .NET 8 Desktop Runtime 的小体积单文件。若要发给未安装 .NET 8 的 Windows 电脑：

```powershell
.\发布.ps1 -SelfContained
```

发布结果位于 `artifacts\publish\CodexUsageWidget.exe`。窗口位置、尺寸、样式、Token 口径和外观设置保存在 `%LOCALAPPDATA%\CodexQuotaWidget\settings.json`。

## 许可证

本项目采用 [MIT License](./LICENSE)。
