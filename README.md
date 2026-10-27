# 魔力窗口台

Windows 桌面程序，用来同时照看多个魔力宝贝客户端。游戏从官方登录器进入后，窗口台把主客户端嵌进台面，其余窗口竖排在右侧，并用热键切换。

## 环境

发布包解压后即可运行。包内带有 .NET 10 桌面运行时、画质组件，以及 WebView2 安装器。双击 `CgDesk.exe` 会直接请求管理员权限。这台电脑如果还没有 WebView2，第一次打开攻略时会自动安装，这一步需要联网。

从源码构建需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

## 构建

在仓库根目录执行：

```powershell
dotnet build src/CgDesk/CgDesk.csproj -c Release
```

程序在 `src/CgDesk/bin/Release/net10.0-windows10.0.19041.0/CgDesk.exe`。构建时会把 `ddraw-proxy.dll`、`enhance-inject.exe` 和 `dgv\ddraw_dgv.dll` 复制到输出目录。

## 使用

1. 首次打开点「选择登录器并启动」，选择官方登录器。已有路径时，点顶部「启动游戏」。
2. 在官方登录器中完成登录和角色选择，按官方流程进入游戏，不单独运行客户端 EXE。
3. 游戏窗口出现后会自动接入主区域。未接入时先点「重新扫描」，仍在桌面上的客户端可通过「接入窗口」手动选择。
4. 有多个窗口时，小窗竖排在主区域右侧。点小窗，即可和主窗口对调。

密码只在登录器里输入。窗口台的配置不保存密码。

配置写在 `%AppData%\CgDesk\config.json`。设置面板在首次启动时默认展开；旧版升级后也会展开一次，此后记住用户手动收起的选择。工具栏右侧固定显示带齿轮和高亮边框的「设置」按钮，并标明「展开 / 收起」，可随时找回登录器路径、画质、主题和快捷键选项。

最小化保留在任务栏，关闭按钮默认退出窗口台。可在设置中开启「关闭时收进托盘」，之后左键单击托盘图标即可找回。退出窗口台会将游戏放回桌面，不关闭游戏。

### 窗口

- 「重新接入」把当前主窗口重新嵌进台面。
- 「迷你控制台」把控制台收到左下角，游戏留在桌面上。点预览切换窗口，点「展开控制台」恢复大小并自动接入。
- 「实时预览」打开后，小窗跟着游戏变化；关掉后停在当前这一帧。
- 「深色模式」切换界面颜色。选择会记住，攻略网页保留百科自己的颜色。
- 右键小窗或主画面标题，可以为本次运行的窗口添加备注，或将它移回桌面并停止自动接入。之后可通过「接入窗口」重新接入。
- 其他客户端关闭时，现有窗口编号保持不变。按不存在的窗口编号不会切换到其他窗口。
- 底部持续显示已识别窗口数、接入状态和增强是否实际生效。嵌入失败时会显示原因，可复制诊断信息用于排查。
- 游戏关闭后，窗口台会立即释放自己的鼠标捕获、清空失效的输入目标并恢复指针，无需切出应用；原始画面和 FSR 模式均适用。
- 主窗口按屏幕可用空间布局。空间不足时游戏区域支持滚动，可收起设置或最大化以增加可见区域。

默认热键是 `Ctrl+Alt` 加上数字或右方向键：

| 热键 | 作用 |
| --- | --- |
| `Ctrl+Alt+1` … `Ctrl+Alt+9` | 切到对应窗口 |
| `Ctrl+Alt+→` | 下一个窗口 |
| `Ctrl+Alt+0` | 重新接入主画面 |

可在「设置 → 编辑热键」里修改。热键必须带上 Ctrl、Alt、Shift 或 Win。

窗口较多时，普通界面保留单列小窗并纵向滚动；迷你控制台使用换行网格并支持滚动。0.1.5 已用 4、8、12、20 个独立测试窗口验证，屏幕外的实时预览会释放，滚动返回时重建，部分可见预览按视口裁剪。真实游戏的多账号性能还需单独验证。

### 攻略

攻略是按需打开的独立窗口，显示魔力百科；之后会记住是否打开。它可以拖动、缩放、置顶，并记住位置和大小。分类直接在百科页面中切换，窗口台不再额外显示分类下拉框；输入关键词并回车可搜索当前浏览的分类。页面失败时可以重试，也可以用系统浏览器打开。点攻略窗口关闭只是收起，顶部再点「攻略」会回到原处。

### 画质增强

只作用于主窗口，不改游戏目录，也不把游戏窗口拉大。游戏仍按 800×600 绘制。打开后，窗口台采集已经画好的画面，用 FSR 铺进主区域里最大的 4:3 矩形，画面不拉伸。窗口变宽时左右留出台面，变高时上下留出台面。

开启增强时，游戏源窗口保持在控制台后方，主区域呈现 FSR 处理后的画面；关闭增强后恢复原生嵌入。底部会显示实际输入、输出分辨率，例如 `800×600 → 1280×960`，便于确认是否真正放大。诊断信息包含实际渲染帧数。

「锐化强度」只调节锐度，不改变这块矩形的大小；值过高可能产生白边。采集不到画面时，主窗口保持原来的 800×600，状态栏会注明增强尚未生效。

增强文件放在 `%LocalAppData%\CgDesk\enhance`。程序会把画质入口、注入器和随包的画质组件复制到这里：

```text
%LocalAppData%\CgDesk\enhance\dgv\ddraw_dgv.dll
```

这个文件来自 dgVoodoo 2 的 32 位 DirectDraw 包装，用来让游戏按原来的大小稳定绘制。放大由窗口台自己完成：

```text
.tools\dgv\MS\x86\DDraw.dll
```

发布包里已经带上这个文件，第一次打开增强时会复制过去。从源码运行时，如果数据目录里还没有它，把上面的 `DDraw.dll` 复制成 `ddraw_dgv.dll`。绘制参数由窗口台写入同目录的 `dgVoodoo.conf`。

已打好的程序在 [GitHub Releases](https://github.com/359134032/cg-desk-public/releases) 的附件里，文件名是 `CgDesk-<版本>-win-x64.zip`。公开 Release 的附件可直接下载，无需登录 GitHub。解压后双击 `CgDesk.exe`，确认管理员权限后就能用。

发布包统一通过 GitHub Releases 附件维护，不提交到源码仓库。仓库根目录的 `release/` 仅供本地临时打包使用，已加入 Git 忽略规则。

## 回归检查

```powershell
dotnet run --project tests/CgDesk.Regression/CgDesk.Regression.csproj -c Release
```

检查会创建并清理独立的测试客户端窗口，验证接入、切换、编号稳定、桌面恢复、攻略生命周期、中文配置编码及深浅色切换。FSR 检查会实际运行窗口采集和 GPU 着色器，读取输出像素，并验证连续帧、调整窗口后的输出尺寸、鼠标映射与键盘转发，需要可用的图形桌面。检查不保存用户设置或安装攻略运行环境。测试客户端不代替真实游戏验证；真实游戏必须通过官方登录器登录并选择角色后检查。

多开压力与发布前扫描：

```powershell
dotnet run --project tests/CgDesk.Regression/CgDesk.Regression.csproj -c Release -- --stress-layout --enforce-clipping
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-SourcePrivacy.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-SourcePrivacy.ps1 -PackagePath release/CgDesk-0.1.7-win-x64.zip
```

详见[首发验收说明](docs/reports/release-0.1.7.md)。当前分发目标为 Windows x64，不能仅凭编译目标认定所有 Windows 10 版本受支持。

### 更新检测（0.1.6）

在「设置 → 软件更新」开启自动检测，点「更新源…」配置主、备用 HTTPS 清单地址。启动后自动检查，成功后每 6 小时检查一次，失败后间隔至少 1 小时；也可手动点「检查更新」。发现新版后在底部保留提醒，并尝试托盘通知，点击后下载并校验安装包，支持进度和取消。安装由用户自行解压新版本，不会自动关闭游戏或替换正在运行的程序。

默认更新源为[本项目的公开 Releases](https://github.com/359134032/cg-desk-public/releases)，无需 GitHub 凭据。旧版空地址或旧项目地址会自动迁移，自定义镜像和关闭自动检查的选择会保留。国内连接不畅时，可配置国内可达的对象存储/CDN；清单和 ZIP 都需要镜像。配置、签名清单生成和发布流程见[更新发布文档](docs/update-publishing.md)，架构见[更新源设计](docs/update-source-design.md)。

重新生成 DirectDraw 入口时显式指定输出位置：

```powershell
dotnet run --project src/CgDesk/Native/ProxyBuilder/ProxyBuilder.csproj -c Release -- src/CgDesk/Native/ddraw-proxy.dll
```

## 仓库里的资源

| 路径 | 内容 |
| --- | --- |
| `src/CgDesk` | 窗口台本体 |
| `src/CgDesk/Shaders` | 画质增强使用的 FSR 1.0 着色器 |
| `src/CgDesk/Native/ddraw-proxy.dll` | 随程序发布的 DirectDraw 入口 |
| `src/CgDesk/Native/enhance-inject.exe` | 随程序发布的启动注入器 |
| `src/CgDesk/Native/ProxyBuilder` | 重新生成 `ddraw-proxy.dll` 的工具 |
| `src/CgDesk/Native/EnhanceInject` | 注入器源码 |
| `.tools/dgVoodoo2_87_5.zip` | dgVoodoo 2.87.5 原包 |
| `.tools/dgv` | 上面这个包的解压结果 |
| GitHub Release 附件 `CgDesk-<版本>-win-x64.zip` | 可直接运行的安装包 |

dgVoodoo 2 是 Dege 的第三方组件，只供画质增强使用。FSR 着色器来自 AMD GPUOpen，许可在头文件里。`bin/`、`obj/` 和 Visual Studio 的本地文件仍不入库。
