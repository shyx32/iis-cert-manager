# 验证记录

- .NET SDK：10.0.401（macOS arm64 交叉编译）。
- 完整解决方案 Release 编译通过，0 错误、0 警告。
- 30 项核心逻辑检查通过：验证方式选择、泛域名限制、域名校验、DNS 主域边界、阿里云 POST 签名、续期窗口、失败重试门限、IPC JSON。
- 最终客户端和后台服务均成功发布为 win-x64 自包含 EXE；客户端为 PE32+ GUI，服务为 PE32+ console。
- XAML、项目 XML、manifest、RAM 策略 JSON 均可解析。
- 未在 Windows 运行安装/卸载/回滚脚本、WPF 界面、命名管道身份校验、IIS COM 或 HTTP.sys。
- 未完成真实阿里云 DNS、Let's Encrypt staging/production、证书链、外部 TLS 握手及后台定时续期验收。
- GitHub Windows CI 状态见 https://github.com/shyx32/iis-cert-manager/actions ；发布程序未进行 Authenticode 签名。

实际运行验收方法见 README.md。
