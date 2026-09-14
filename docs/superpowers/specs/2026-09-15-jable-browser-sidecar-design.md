# Jable 浏览器 Sidecar 设计

## 目标

解决 NAS 上 Jellyfin 插件无法稳定读取 Jable 页面的问题，同时保留现有的检索、排序、分类、缓存和 Jellyfin 元数据逻辑。

本阶段只修复作品表格及详情元数据的网络读取。封面写入视频目录仍按已确认规则单独实现：只补缺失或零字节文件，不覆盖已有非空封面。

## 已验证事实

- NAS 默认 DNS 会把 `jable.tv` 解析到错误地址；Jellyfin 的普通 `HttpClient` 即使连接正确 IP，也会收到 Cloudflare `403`。
- 持久化 Chromium 使用正确地址后可正常返回 Jable 页面，不需要绕过 CAPTCHA。
- 2026-09-15 的验证容器在重启后仍保留浏览器配置和当前页面。
- 完整加载 `https://jable.tv/latest-updates/` 后，页面状态为 `complete`，可读取 24 个作品卡片和 195 个链接。
- 页面需要同时正确解析以下域名：
  - `jable.tv`
  - `www.jable.tv`
  - `assets.jable.tv`
  - `assets-cdn.jable.tv`
- 当前 Chromium 构建的命令行 DoH 参数未生效，因此第一版使用可配置的静态 `extra_hosts`，并在健康检查中明确报告失效域名。

## 架构

新增一个很小的 Node.js bridge，与持久化 Chromium 放在同一 Docker 项目中：

```text
Jellyfin.Plugin.Jable
        |
        | POST /v1/render  JSON
        v
jable-browser-bridge
        |
        | Chrome DevTools Protocol（Docker 内网）
        v
jable-browser (Chromium + 持久化 /config)
        |
        v
     jable.tv
```

职责保持单一：

- Chromium 负责 DNS 映射、TLS、Cookie、Cloudflare 浏览器会话和页面渲染。
- bridge 负责受控导航、等待页面完成、读取最终 DOM，并通过 JSON 返回。
- 插件继续使用现有 `JableParser` 解析 HTML。Node 端不复制番号、女优、标签、日期或统计数字解析规则。

这比让插件直接实现完整 CDP 协议更容易测试，也避免同时维护 C# 和 JavaScript 两套 Jable 解析器。

## 容器部署

浏览器使用已验证的 `jlesage/chromium:v26.08.3`。镜像可通过镜像加速地址拉取，但运行时访问 Jable 不经过代理。

最终部署要求：

- Chromium `/config` 持久化。
- Chromium GUI 映射到 NAS `3100`，供管理员首次验证或重新完成 Cloudflare 检查。
- Chromium CDP `9222` 不映射到宿主机，只允许 bridge 通过 Docker 内网访问。
- bridge 不映射到局域网；Jellyfin、bridge、Chromium 加入同一个外部 Docker 网络。
- Chromium GUI 恢复 HTTPS 和登录保护。当前无认证 HTTP 仅用于本次可行性验证。
- Jable 域名 IP 从 Compose 环境文件读取，便于失效后更新并重建浏览器容器。

当前验证可用的映射为：

```yaml
extra_hosts:
  - "jable.tv:104.20.42.172"
  - "www.jable.tv:104.20.42.172"
  - "assets.jable.tv:104.20.42.172"
  - "assets-cdn.jable.tv:15.235.118.31"
```

IP 不是代码常量。部署文件通过 `.env` 注入，健康检查失败时从可信 DoH 重新确认后再更新。

## Bridge API

### `GET /healthz`

返回 bridge 与 Chromium 的可用性，不访问 Jable：

```json
{
  "ok": true,
  "browser": "ready"
}
```

### `POST /v1/render`

请求：

```json
{
  "url": "https://jable.tv/latest-updates/"
}
```

成功响应：

```json
{
  "url": "https://jable.tv/latest-updates/",
  "html": "<!doctype html>..."
}
```

约束：

- 只接受 `https`。
- 主机必须是 `jable.tv` 或其子域名。
- 最终跳转地址必须再次通过同一校验。
- 响应 HTML 最大 4 MiB，与插件现有限制一致。
- bridge 一次只执行一个导航；插件现有请求节流继续保留。
- 每次请求创建临时标签页，完成后关闭；Cookie 和浏览器配置由持久化 profile 共享。
- 请求和响应均为 JSON；bridge 不提供播放、下载或任意 URL 抓取能力。

错误响应统一为：

```json
{
  "code": "browser_timeout",
  "message": "Jable page did not finish loading before the deadline."
}
```

状态码：

- `400`：URL 非法或不在允许域名内。
- `401`：bridge token 不正确。
- `502`：Chromium/CDP 不可用或导航失败。
- `504`：页面加载超时。

Cloudflare 挑战页仍作为成功渲染的 HTML 返回，由现有 `JableParser.IsChallengePage` 识别并记录 `Challenge` 错误。管理员随后通过 `3100` 的持久化浏览器手动完成验证，再重新触发同步。

## 插件改动

`PluginConfiguration` 新增：

- `BrowserBridgeUrl`：例如 `http://jable-browser-bridge:3000/`。
- `BrowserBridgeToken`：仅写入配置，不通过配置 JSON 回显，处理方式复用现有代理密码规则。

`JableHttpClient.GetHtmlAsync` 的行为：

1. 配置了 bridge 时，调用 `/v1/render` 并读取 `html`。
2. 未配置 bridge 时，保留现有直接 HTTP/代理路径，避免破坏已有安装。
3. 两条路径共用现有 URI 白名单、4 MiB 限制、超时、挑战页识别和错误分类。

`GetImageAsync` 暂不经过 bridge。本阶段优先恢复表格和元数据同步；封面下载与写入目录在后续封面同步任务中一起处理。

配置页新增 bridge 地址、token 和“测试连接”按钮。测试只调用 bridge 健康检查和一次 Jable 首页渲染，不写缓存。

## 安全边界

- CDP 不暴露到 NAS 局域网。
- bridge 只在共享 Docker 网络监听，并要求 bearer token。
- bridge 对请求 URL 和最终 URL 都执行严格 allowlist 校验，防止 SSRF。
- 日志不记录 token、完整 HTML、Cookie 或代理凭据。
- 不自动点击 CAPTCHA，不注入 Cloudflare 绕过脚本。
- Chromium GUI 只供管理员使用，并启用 HTTPS 与登录保护。

## 错误处理与可观测性

- bridge 日志记录请求 ID、目标路径、耗时、最终状态和错误代码，不记录查询结果正文。
- `/healthz` 区分 bridge 未启动、CDP 不可达和 Chromium 未就绪。
- 插件继续把最近错误写入 `CatalogSnapshot.LastError`，Jable 页面沿用现有状态展示。
- 若静态域名映射失效，错误信息包含失败域名，管理员更新 `.env` 后只重建浏览器项目，不重启 Jellyfin。

## 测试

最小测试集：

- bridge URL 白名单、最终跳转白名单、token 和 4 MiB 上限。
- bridge 使用假的 CDP WebSocket 验证成功、超时和导航错误。
- `JableHttpClient` 验证 bridge 成功响应、非法 JSON、超大 HTML、401、502、504 和调用方取消。
- 现有 parser、catalog、授权和前端测试全部继续通过。
- NAS 冒烟测试：重启 Chromium 后同步第一页，确认至少一个作品并验证 `LastSuccessfulSync` 更新。

## 不在本阶段实现

- 自动破解或绕过 Cloudflare/CAPTCHA。
- 多浏览器池、并行标签页或任务队列。
- Node 端重复实现 Jable 元数据解析。
- 视频播放、下载或转码。
- 自动覆盖已有非空封面。
