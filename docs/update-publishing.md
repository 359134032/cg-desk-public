# 更新配置与发布

官方仓库：[359134032/cg-desk-public](https://github.com/359134032/cg-desk-public)。首个公开版本为 0.1.7，源码使用独立初始提交，不继承其他仓库历史。

## 使用更新检测

首次使用请下载完整 ZIP，解压后运行 CgDesk.exe。程序默认使用本仓库的公开 Releases；无需登录 GitHub、填写 token 或安装开发工具。设置中的「软件更新」可关闭自动检查、立即检查或调整主备源。升级时会迁移空地址及旧项目地址，保留自定义镜像和关闭自动检查的选择。

启动后延迟 8 秒检查，成功后每 6 小时检查，失败后至少间隔 1 小时；发现新版后显示界面与托盘提示。下载支持进度、取消、备用镜像、签名、大小与 SHA256 校验。ZIP 保存在 `%LocalAppData%\CgDesk\updates`，安装仍由用户手动解压到独立目录，不自动替换运行文件、重启窗口台或关闭游戏。

主源也可使用直接 HTTPS 签名清单地址。发布方可以在程序目录放置 `update-sources.json`，字段见 `update-sources.example.json`；只在用户未配置自定义主源时载入。部署文件和镜像不能修改内置可信公钥。

## GitHub Release 附件

稳定通道读取最新正式 Release 的 `cgdesk-update-stable.json`。测试通道搜索最近 20 个已发布 Release 的 `cgdesk-update-beta.json`。草稿不参与检测；稳定通道不接收预发布版本。

每个正式版本上传完整 ZIP、稳定清单和测试通道清单，使测试通道也能接收最终版本。清单和 ZIP 都需要公开可读。GitHub Release 权限与下载说明见[官方文档](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)。

## 国内用户的主备源

国内连接不畅时，建议将签名清单和 ZIP 同时镜像到自己控制的对象存储/CDN；国内可达地址优先，GitHub 备用。仅镜像 ZIP 而清单仍不可达，程序无法发现更新。本项目不内置第三方免费 GitHub 代理，不保证任何运营商上的访问速度。

国内 CDN 的域名及区域接入要求以服务商当前规则为准，例如[腾讯 COS CDN 配置](https://cloud.tencent.cn/document/product/436/18670)、[阿里云 CDN 接入说明](https://help.aliyun.com/en/cdn/add-a-domain-name)。目前未部署额外 CDN。

## 维护者签名工具

客户端仅包含公钥 `src/CgDesk/Assets/update-public-keys.json`。维护者私钥由 Windows DPAPI 保护，保存在本地忽略目录 `artifacts/signing/cgdesk-update-key.dpapi`，不进入源码或发布包。公开克隆不包含此文件，也不能代表官方签名发布。

维护者应备份受保护文件及可恢复的 Windows 用户环境；单独复制 DPAPI 文件到其他机器不足以解密。密钥丢失需通过人工分发新客户端迁移公钥，本版没有远程信任根轮换或 CI 私钥导出功能。

发布步骤：构建并验证 → 打包和扫描 → 上传版本化 ZIP → 生成两份签名清单 → 离线验签 → 上传清单 → 确认匿名下载 → 最后正式发布。清单输出旁可放同名 `.notes.txt` 作为更新说明。

```powershell
dotnet publish src/CgDesk/CgDesk.csproj -c Release -r win-x64 --self-contained true -o release/CgDesk-0.1.7
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-SourcePrivacy.ps1 -PackagePath release/CgDesk-0.1.7-win-x64.zip
dotnet run --project tools/CgDesk.UpdatePublisher -- manifest artifacts/signing/cgdesk-update-key.dpapi release/CgDesk-0.1.7-win-x64.zip 0.1.7 stable release/cgdesk-update-stable.json https://github.com/359134032/cg-desk-public/releases/download/v0.1.7/CgDesk-0.1.7-win-x64.zip
dotnet run --project tools/CgDesk.UpdatePublisher -- manifest artifacts/signing/cgdesk-update-key.dpapi release/CgDesk-0.1.7-win-x64.zip 0.1.7 beta release/cgdesk-update-beta.json https://github.com/359134032/cg-desk-public/releases/download/v0.1.7/CgDesk-0.1.7-win-x64.zip
dotnet run --project tests/CgDesk.Regression -c Release -- --verify-update release/cgdesk-update-stable.json stable
```

正式版本使用自己的版本号和 URL。镜像应提供相同文件，摘要由工具读取 ZIP 自动计算。签名采用 ECDSA P-256/SHA256；私钥不能上传 GitHub 或写入客户端。
