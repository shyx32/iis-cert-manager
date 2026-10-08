# 验证范围

- 本地：.NET SDK 10.0.401 完整解决方案交叉编译；44 项核心逻辑检查；PowerShell 7.6.6 解析安装/恢复/打包脚本及工作流内嵌脚本。
- Windows CI：编译、核心规则、DPAPI 状态保存和损坏备份恢复、目录权限与所有者、模拟阿里云 RPC/签名/清理日志、真实 IIS SNI 绑定和 TLS 握手、证书链安装和 SAN 拒绝、HTTP.sys 验证路径与 IIS 业务路径共存、WPF 资源和窗口构造、服务安装/升级/控制管道/卸载。
- Windows 检查的执行结果以对应源码 SHA 的 Actions 为准：https://github.com/shyx32/iis-cert-manager/actions 。Release 仅在其原生检查成功后创建。
- 未覆盖：真实阿里云账户 API、真实 CA 签发、公网 DNS/WAF/IPv6/代理拓扑、长时间到期调度和实际用户桌面交互。
- 发布包未做 Authenticode 签名。首次版本使用预发布标签，在业务服务器投用前完成 README 的环境验收。

完整复核记录见 REVIEW.md，安装及验收说明见 README.md。
