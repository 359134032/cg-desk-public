# 更新配置与发布

官方仓库：[359134032/cg-desk-public](https://github.com/359134032/cg-desk-public)。首个公开版本为 0.1.7，源码使用独立初始提交，不继承其他仓库历史。

## 使用更新检测

首次使用请下载完整 ZIP，解压后运行 CgDesk.exe。程序默认使用本仓库的公开 Releases；无需登录 GitHub、填写 token 或安装开发工具。设置中的「软件更新」可关闭自动检查、立即检查或调整主备源。升级时会迁移空地址及旧项目地址，保留自定义镜像和关闭自动检查的选择。

启动后延迟 8 秒检查，成功后每 6 小时检查，失败后至少间隔 1 小时；发现新版后显示界面与托盘提示。下载支持进度、取消、备用镜像、签名、大小与 SHA256 校验。ZIP 保存在 `%LocalAppData%\CgDesk\updates`，安装仍由用户手动解压到独立目录，不自动替换运行文件、重启窗口台或关闭游戏。

主源也可使用直接 HTTPS 签名清单地址。发布方可以在程序目录放置 `update-sources.json`，字段见 `update-sources.example.json`；只在用户未配置自定义主源时载入。部署文件和镜像不能修改内置可信公钥。

## GitHub Release 附件

稳定通道直接通过 `releases/latest/download/cgdesk-update-stable.json` 读取最新正式 Release 的签名清单，不调用 REST API，不消耗匿名接口额度。测试通道为发现预发布版本，仍搜索最近 20 个已发布 Release 的 `cgdesk-update-beta.json`。草稿不参与检测；稳定通道不接收预发布版本。

GitHub 匿名 REST API 按出口 IP 共享每小时 60 次额度。0.1.7 可能把限流 403 误报为更新源不公开；0.1.8 已修复。测试通道若被限流，会显示恢复时间，并在恢复前避免重复请求；配置的签名备用源仍会尝试。其他 403、401 与 404 分别提示访问拒绝、需要授权和发布/地址不存在。详见[GitHub 限流说明](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api)。

每个正式版本上传完整 ZIP、稳定清单和测试通道清单，使测试通道也能接收最终版本。清单和 ZIP 都需要公开可读。GitHub Release 权限与下载说明见[官方文档](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)。

## 国内用户的主备源

国内连接不畅时，建议将签名清单和 ZIP 同时镜像到自己控制的对象存储/CDN；国内可达地址优先，GitHub 备用。仅镜像 ZIP 而清单仍不可达，程序无法发现更新。本项目不内置第三方免费 GitHub 代理，不保证任何运营商上的访问速度。

国内 CDN 的域名及区域接入要求以服务商当前规则为准，例如[腾讯 COS CDN 配置](https://cloud.tencent.cn/document/product/436/18670)、[阿里云 CDN 接入说明](https://help.aliyun.com/en/cdn/add-a-domain-name)。目前未部署额外 CDN。

## 维护者签名工具

客户端仅包含公钥 `src/CgDesk/Assets/update-public-keys.json`。发布身份由这把公钥及对应私钥决定，不绑定某台电脑。原来的 `artifacts/signing/cgdesk-update-key.dpapi` 是绑定本机用户的私钥保护副本；密码加密的 `.p8` 则可以直接在其他 Windows 电脑签名，不需要原电脑或导入 DPAPI。私钥和加密文件均不进入源码或发布包。

单独复制 DPAPI 文件到其他机器不足以解密。应先在原电脑、原 Windows 用户下导出密码加密备份，之后即可在其他电脑直接签名；如需本机保护副本，也可导入到目标电脑。两种方式均沿用原密钥，不修改客户端公钥。备份使用 .NET 的加密 PKCS#8 格式（AES-256-CBC、PBKDF2-SHA256、600,000 次迭代），不依赖原电脑的 DPAPI 环境。参见 [Microsoft API 说明](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.asymmetricalgorithm.exportencryptedpkcs8privatekey?view=net-10.0)。

### 推荐：直接使用可迁移加密密钥发布

换电脑时，拉取最新源码，安装 .NET 10 SDK，将已有 `.p8` 文件放到自己控制的位置，并从独立保存的恢复密码中取得密码即可。不需要重新生成密钥，不需要原来的 Windows 用户，也不需要先生成本机 `.dpapi` 文件。现有备份文件可直接使用，无需重新导出。

先验证密码和签名身份，按提示输入密码（不回显）：

```powershell
dotnet run --project tools/CgDesk.UpdatePublisher -- check-portable-key artifacts/signing/cgdesk-update-key.backup.p8 src/CgDesk/Assets/update-public-keys.json
```

为对应版本的实际 ZIP 生成清单。下面以 0.1.9 为例，发布其他版本时替换版本号、包路径和下载 URL：

```powershell
dotnet run --project tools/CgDesk.UpdatePublisher -- manifest-portable artifacts/signing/cgdesk-update-key.backup.p8 src/CgDesk/Assets/update-public-keys.json release/CgDesk-0.1.9-win-x64.zip 0.1.9 stable release/cgdesk-update-stable.json https://github.com/359134032/cg-desk-public/releases/download/v0.1.9/CgDesk-0.1.9-win-x64.zip
dotnet run --project tools/CgDesk.UpdatePublisher -- manifest-portable artifacts/signing/cgdesk-update-key.backup.p8 src/CgDesk/Assets/update-public-keys.json release/CgDesk-0.1.9-win-x64.zip 0.1.9 beta release/cgdesk-update-beta.json https://github.com/359134032/cg-desk-public/releases/download/v0.1.9/CgDesk-0.1.9-win-x64.zip
```

两条签名命令均会提示输入密码。解密仅发生在进程内存中，不生成明文私钥或本机 DPAPI 副本。每次签名前，工具都会确认密钥匹配客户端现有可信公钥；密码不对或拿错密钥时拒绝生成清单。之后仍须离线验签并将两份清单和对应 ZIP 上传到同一个 Release。

客户端继续使用内置公钥验证清单签名，再验证下载 ZIP 的大小和 SHA256，用户无需密码。换发布电脑不会改变客户端的信任公钥。`check-portable-key` 和 `manifest-portable` 均支持将 `--password-stdin` 放在最后，由安全管道提供密码，不接受明文密码命令参数。

加密密钥文件与恢复密码应分别备份；同时拥有两者即可代表发布者签名。原来的 DPAPI 发布命令仍可继续使用，下面的导入流程仅用于需要保留本机保护副本的情况。

### 已有密钥：备份和迁移

在原电脑检查密钥可读，并确认它与源码中的可信公钥一致：

```powershell
dotnet run --project tools/CgDesk.UpdatePublisher -- check-key artifacts/signing/cgdesk-update-key.dpapi src/CgDesk/Assets/update-public-keys.json
```

在原电脑导出可跨机器恢复的加密备份。命令会隐藏输入，并要求输入两次至少 12 个字符的密码；建议使用密码管理器生成并单独保存长随机密码。

```powershell
dotnet run --project tools/CgDesk.UpdatePublisher -- export-key artifacts/signing/cgdesk-update-key.dpapi artifacts/signing/cgdesk-update-key.backup.p8 src/CgDesk/Assets/update-public-keys.json
```

将 `.p8` 文件通过自己控制的安全渠道复制到新电脑。不要把备份或密码放进 Git、GitHub Releases 或软件 ZIP。建议至少保留两份独立备份，密码与文件分开存储；重装系统、删除原用户前先完成恢复演练。`.gitignore` 已忽略 `artifacts/`、`*.dpapi` 和 `*.p8`，不要使用强制添加来绕过。

在新电脑拉取包含迁移命令的源码，放好备份后执行以下命令，输入原备份密码：

```powershell
dotnet run --project tools/CgDesk.UpdatePublisher -- import-key artifacts/signing/cgdesk-update-key.backup.p8 artifacts/signing/cgdesk-update-key.dpapi src/CgDesk/Assets/update-public-keys.json
dotnet run --project tools/CgDesk.UpdatePublisher -- check-key artifacts/signing/cgdesk-update-key.dpapi src/CgDesk/Assets/update-public-keys.json
```

导入会检查备份是否匹配源码中已有的可信公钥，再用目标 Windows 用户的 DPAPI 重新保护私钥。导出和导入都拒绝覆盖已有目标文件。如果新电脑已有无法解密的 `.dpapi`，先将它改名保留，再导入到原路径。测试恢复时也可以指定另一个全新的输出路径，不必覆盖当前工作密钥。

默认在交互式终端运行，密码不作为命令参数、不回显。自动化调用可显式添加 `--password-stdin`，由安全输入管道提供一行密码（导出不再重复确认）；不要把密码字面量写在命令、脚本、日志或 GitHub 配置文件中。

### 首次生成密钥与丢失处理

只有新项目尚未建立信任公钥时才使用 `keygen`，并在生成后立即执行上面的备份及恢复检查：

```powershell
dotnet run --project tools/CgDesk.UpdatePublisher -- keygen artifacts/signing/cgdesk-update-key.dpapi src/CgDesk/Assets/update-public-keys.json
```

生成器拒绝覆盖现有私钥或非空公钥文件。已发布项目缺少私钥时，不要删除公钥后重新生成；旧客户端只信任原公钥，会拒绝新密钥签出的更新。如果原电脑、原 Windows 用户和可解密的备份全部丢失，则不能从公钥恢复私钥，需要人工分发包含新公钥的客户端。本版没有远程信任根轮换功能。

### 签名发布

发布步骤：构建并验证 → 打包和扫描 → 上传版本化 ZIP → 生成两份签名清单 → 离线验签 → 上传清单 → 确认匿名下载 → 最后正式发布。清单输出旁可放同名 `.notes.txt` 作为更新说明。

```powershell
dotnet publish src/CgDesk/CgDesk.csproj -c Release -r win-x64 --self-contained true -o release/CgDesk-0.1.7
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-SourcePrivacy.ps1 -PackagePath release/CgDesk-0.1.7-win-x64.zip
dotnet run --project tools/CgDesk.UpdatePublisher -- manifest artifacts/signing/cgdesk-update-key.dpapi release/CgDesk-0.1.7-win-x64.zip 0.1.7 stable release/cgdesk-update-stable.json https://github.com/359134032/cg-desk-public/releases/download/v0.1.7/CgDesk-0.1.7-win-x64.zip
dotnet run --project tools/CgDesk.UpdatePublisher -- manifest artifacts/signing/cgdesk-update-key.dpapi release/CgDesk-0.1.7-win-x64.zip 0.1.7 beta release/cgdesk-update-beta.json https://github.com/359134032/cg-desk-public/releases/download/v0.1.7/CgDesk-0.1.7-win-x64.zip
dotnet run --project tests/CgDesk.Regression -c Release -- --verify-update release/cgdesk-update-stable.json stable
```

正式版本使用自己的版本号和 URL。镜像应提供相同文件，摘要由工具读取 ZIP 自动计算。签名采用 ECDSA P-256/SHA256；私钥不能上传 GitHub 或写入客户端。

仅推送源码、上传 ZIP 或新建 Release 不会生成更新签名。必须为对应 ZIP 使用原密钥生成两份清单，离线验签后，将 `cgdesk-update-stable.json` 和 `cgdesk-update-beta.json` 一起上传到该版本的 Release。已经发布但缺少清单的版本，也可在导入原密钥后为相同 ZIP 补签并补传附件。
