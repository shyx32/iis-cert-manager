# IIS 证书管家

[下载版本](https://github.com/shyx32/iis-cert-manager/releases) · [GitHub 仓库](https://github.com/shyx32/iis-cert-manager) · [Windows 构建](https://github.com/shyx32/iis-cert-manager/actions/workflows/windows.yml)

Windows 原生 WPF 客户端 + Windows Service。两者安装在同一台 IIS 服务器，通过仅限本机管理员和 SYSTEM 的命名管道通信。关闭界面后，服务继续自动签发和续期。

## 已实现

- 枚举 IIS 站点与 HTTP/HTTPS 绑定：站点状态、目录、主机名、IP、端口、证书指纹、到期时间。
- Let's Encrypt ACME v2 签发；HTTP-01、阿里云 DNS-01；SAN 和泛域名证书。
- 自动模式：阿里云主域及密钥完整时使用 DNS-01，否则使用 HTTP-01。DNS 验证失败会记录错误，不会悄悄切换验证方式。
- 正式证书导入 `LocalMachine\My`、中间证书导入 `LocalMachine\CA`（不新增根信任），校验 SAN/有效期/EKU 后创建或更新所选站点的 HTTPS SNI 绑定，保留其他绑定及旧证书。
- 每 15 分钟检查托管规则；默认到期前 30 天续期。失败按 15、30、60 分钟等递增重试，最多间隔 24 小时。
- 测试 CA 默认开启：可以验证完整签发流程，但测试证书不会安装或绑定，也不会自动周期签发。
- Windows DPAPI 加密配置与 ACME 账户私钥；配置目录仅管理员/SYSTEM 可读写；接口不回传 DNS 密钥；客户端验证管道服务进程来自同一安装目录。
- DNS TXT 传播检测；按 RecordId 清理本程序新增的 TXT；清理失败记录在磁盘，下次扫描继续重试。
- 绑定快照、部署失败回滚、日志、服务崩溃后自动重启、安装/卸载/手工回滚脚本。

## 运行环境

Windows Server 2016/2019/2022/2025（带桌面体验）+ IIS 10，或仍获支持的 Windows 11 + IIS 10。请启用 IIS 管理脚本和工具。首版发布 `win-x64` 自包含程序，运行时无需另外安装 .NET。开发/构建需要 .NET 10 SDK；生成安装 EXE 还需要 Windows 与 Visual Studio C++ Build Tools（包含 Windows SDK）。

更新已有 HTTPS 绑定仅支持普通 SNI；可读取非 SNI 和集中证书存储绑定，但不会自动改写这些特殊绑定。空主机名/IP 站点须先在 IIS 中配置实际域名。一个全局配置支持一个阿里云 DNS 主域及其子域，可管理多个 IIS 站点。

## 使用发布包

1. 下载 `IisCertManager-Setup-win-x64.exe`，双击运行，在 Windows 管理员权限提示中选择“是”。安装窗口使用 Win32 原生界面，不依赖 .NET 启动。无需解压、无需安装 .NET、无需输入命令。
2. 点击“安装 / 更新”，完成后点击“打开证书管家”。已有版本也可直接运行新安装程序更新；更新前关闭管理界面。安装失败会显示原因或日志位置。
3. 从桌面打开“IIS Certificate Manager”（程序界面为中文）。界面要求管理员权限。
4. 在“全局配置”填写联系邮箱，阅读并同意 Let's Encrypt 服务条款。建议保留测试 CA 做首次验证。
5. 使用 DNS-01 时填写阿里云 DNS 主域，例如 `example.com`、RAM AccessKey ID 和 Secret；留空已有密钥表示保留。保存全局配置。
6. 在“IIS 站点与证书”选择一个有主机名的绑定；填写证书域名、验证方式、目标 HTTPS 端口。证书必须包含或覆盖所选绑定主机名。
7. 点击“立即签发 / 更新绑定”。测试成功后，关闭测试 CA 并保存全局配置，再签发正式证书。
8. 勾选自动续期的规则会由后台持续处理。**正式 CA 下保存启用的托管规则，即授权后台自动签发和部署该规则。** 可在“托管与续期”编辑、使用“暂停 / 启用续期”按钮暂停或恢复、或移除规则。即使原 IIS 绑定已删除，也可以直接暂停规则。

每条规则更新一个具体 IIS 主机名/IP/HTTPS 端口。SAN 列表不会自动生成其他域名的 IIS 绑定；需要部署到另一个绑定时，为它建立另一条规则。新建规则即使原来已有第三方证书，也会按托管配置签发新证书。

## 两种验证的前提

### HTTP-01

域名 A/AAAA 必须指向当前服务器或能够转发验证请求的入口；公网 TCP 80 必须可达。服务通过 HTTP.sys 临时注册 `/.well-known/acme-challenge/` 路径，不写站点目录、不改业务重写规则；监听器在签发结束后关闭。该路径与 IIS 共存仍需在目标服务器验收，端口若被非 HTTP.sys 程序独占会失败并显示错误。

同时存在 IPv4/IPv6、CDN、WAF、反向代理或多个服务器时，所有公网验证入口都须将挑战路径转发到本服务。安装脚本不会自动改防火墙或路由器规则。泛域名不能使用 HTTP-01。

### 阿里云 DNS-01

DNS 主域须由阿里云权威 DNS 托管，AccessKey 须有指定主域的新增/删除 DNS 记录权限。建议使用 RAM 子账号，示例策略位于 `docs/aliyun-ram-policy.json`，请替换账号 ID 和主域。

程序调用 `AddDomainRecord` 创建 `_acme-challenge` TXT，通过 `1.1.1.1` 和 `8.8.8.8` 检测记录可见后通知 CA 验证。需要出站 HTTPS 443 和到两个公共解析器的 DNS 53；两个解析器都能查询到才继续。清理仅删除本次创建的 RecordId，不删除同名其他 TXT。存在未清理记录时禁止清除配置或更换 DNS 主域；允许轮换同一主域的密钥，以便恢复清理权限。

首版不支持 `_acme-challenge` CNAME 委派、多 DNS 服务商、多阿里云主域配置，也不支持 TLS-ALPN-01。需求中的 `https-1` 按 `HTTP-01` 实现。

## 构建

在 Windows PowerShell 执行：

```powershell
.\scripts\Build.ps1
```

构建解决方案，执行协议/调度/签名检查，发布自包含客户端与服务，再生成可双击的 `artifacts/IisCertManager-Setup-win-x64.exe` 和备用 ZIP 包 `artifacts/IisCertManager-win-x64.zip`。安装包包含 `client/`、`service/`、`scripts/`、`docs/` 和本说明。`.github/workflows/windows.yml` 提供 Windows CI 工作流，在推送及拉取请求时执行编译、核心检查与发布包构建。CI 结果见仓库 Actions；构建成功不代表真实 IIS 或公网签发验收通过。

项目结构：

```text
src/IisCertManager.Contracts   配置/命令/返回模型，域名与验证方式规则，阿里云签名
src/IisCertManager.Service     IIS、ACME、DNS/HTTP 验证、续期、持久化、命名管道服务
src/IisCertManager.Client      WPF 原生界面
tests/IisCertManager.Tests    无第三方测试框架的核心逻辑检查
tests/IisCertManager.WindowsTests Windows 原生 DPAPI、模拟 DNS API、IIS/TLS、HTTP.sys、WPF 检查
scripts/                      构建、安装、卸载、Windows 检查、绑定恢复
```

主依赖：Certes 4.1.0、DnsClient 1.8.0、Microsoft.Web.Administration 11.1.0，版本和传递依赖记录在 `packages.lock.json`。阿里云使用 HTTPS RPC API 与 HMAC-SHA1 官方请求签名；证书私钥使用 RSA。

## GitHub 自动打包与版本发布

- 推送到 `main` 或提交 PR：Windows Actions 自动编译、执行核心检查和原生 Windows/IIS 检查，打包 Windows 自包含程序、源码包和 `SHA256SUMS.txt`，保存为 Actions Artifact。原生检查会在一次性 Windows runner 上启用 IIS，创建并清理临时站点/本地测试证书，通过发布的 EXE 安装程序验证安装、升级和卸载。
- 推送版本标签：自动创建 GitHub Release 并上传安装 EXE、程序 ZIP、源码 ZIP 和校验文件。含后缀的版本（如 `v0.1.0-beta.1`）自动标记为预发布，正式版本（如 `v0.1.0`）正常发布。
- 手动发布：仓库 Actions → **Publish release** → Run workflow，先推送版本标签，选择该标签并输入不带 `v` 的版本号。标签必须指向本次构建提交；已发布 Release 不会被覆盖。
- 可执行程序内的产品版本跟随 Release 版本；发布说明记录确切源码 SHA 和构建链接。

```powershell
git tag v0.1.0-beta.1
git push origin v0.1.0-beta.1
# 或：gh workflow run release.yml -f version=0.1.0-beta.1
```

首次版本以预发布交付，Windows CI 构建通过仍不代表已完成真实 IIS 和证书签发验收。

## 状态和恢复

- 程序默认安装于 `%ProgramFiles%\IisCertManager`。
- 配置及私钥位于 `%ProgramData%\IisCertManager`：`state.dpapi`、`state.dpapi.bak`、`account-*.dpapi`。状态主文件损坏或缺失时恢复可验证的同机加密备份，损坏文件保留用于诊断；无法恢复时拒绝静默清空配置。DPAPI 绑定当前 Windows 机器；不可直接迁移到另一台服务器解密。
- `service.log` 保留近期日志，单文件约 2 MB 后轮换，另保留一个旧文件。
- `bindings/*.json` 记录每次正式部署前的原 HTTPS 绑定。旧证书和私钥不会自动清理。
- `dns-cleanup.json` 为尚需清理的 TXT 记录。进程在 DNS API 成功、清理日志尚未落盘的极短窗口崩溃时，可能遗留 TXT；请根据 `_acme-challenge` 记录人工检查。
- 签发过程中关闭客户端，不会取消后台任务；重新打开/刷新查看结果。服务签发期间串行处理命令，其他请求可能等待或提示繁忙。
- 外部管理员修改托管规则关联的 IIS 绑定前，请先关闭该规则自动续期，避免后续续期再次覆盖。

手工恢复原绑定（若部署后证书已被另外修改，脚本拒绝覆盖，需人工审查）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Restore-Binding.ps1 `
  -Backup 'C:\ProgramData\IisCertManager\bindings\某个快照.json'
```

脚本先停止续期服务，再恢复快照对应绑定。请审查托管规则后再启动服务。自动回滚采用所选绑定的证书指纹检查，避免覆盖部署期间由管理员写入的其他证书；严重回滚失败会写入日志。

卸载服务：

```powershell
.\scripts\Uninstall.ps1
```

卸载保留 IIS 绑定、证书、加密状态和程序文件。

## 验证记录与上线验收

本地验证包括 .NET 10 交叉编译、44 项核心规则检查和 PowerShell 脚本/工作流语法解析。Windows CI 另外执行 DPAPI、目录 ACL、模拟阿里云 RPC、真实 IIS SNI/TLS、HTTP.sys 与 IIS 共存、WPF 窗口构造、服务安装/升级/卸载检查；以对应源码提交的 Actions 结果为准。**真实域名/阿里云凭据、CA 签发、公网 TLS 和长时间无人值守续期仍须在目标服务器验收。** 发布包未做 Authenticode 签名，首版以预发布交付。

Windows 管理员可执行 `scripts/Verify-Windows.ps1` 检查服务、命名管道、IIS 枚举与密钥脱敏。正式验收请完成：

1. 枚举结果与 IIS 管理器一致，包含现有证书指纹、有效期。
2. 无 DNS 配置时测试 CA HTTP-01 成功；80 端口不可达时明确失败且原证书不变。
3. 配置阿里云后测试 CA DNS-01 成功；SAN 包含根域和泛域名时 TXT 多值均通过且清理后其他记录仍在。
4. 正式签发后 IIS 指纹回读一致；从外部客户端验证目标域名 TLS 握手、信任链和 SAN。
5. 关闭界面、重启 Windows 服务后托管规则和密钥仍可用，测试待续期规则可以自动运行。
6. 验证错误 DNS 密钥、传播超时、CA 拒绝、IIS 冲突时保留现有 HTTPS；检查重试时间和失败日志。
7. 验证普通用户及远程机器无法连接控制管道；管理员可正常操作。
8. 测试服务器上验证绑定恢复脚本，再部署到业务服务器。

## 官方参考

- [Let's Encrypt 验证协议](https://letsencrypt.org/docs/challenge-types/)
- [Certes ACME 客户端](https://github.com/fszlin/certes)
- [IIS SSL 绑定](https://learn.microsoft.com/en-us/iis/configuration/system.applicationhost/sites/sitedefaults/bindings/binding)
- [阿里云 AddDomainRecord](https://www.alibabacloud.com/help/en/dns/api-alidns-2015-01-09-adddomainrecord)
- [阿里云 DeleteDomainRecord](https://www.alibabacloud.com/help/en/dns/api-alidns-2015-01-09-deletedomainrecord)
