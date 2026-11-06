# 魔力窗口台

Windows 桌面程序。通过官方登录器进入游戏后，自动接入主客户端，其他窗口在右侧预览，并支持热键切换。

## 构建和运行

需要 .NET 10 SDK：

```powershell
dotnet build src/CgDesk/CgDesk.csproj -c Release
```

发布包自带 .NET 桌面运行时，解压后运行 CgDesk.exe。攻略功能使用 WebView2；缺少运行环境时由原有攻略安装流程提示处理。游戏账号密码始终在官方登录器中输入，窗口台不保存密码。

## 启动分辨率

在「设置 → 游戏启动 → 启动分辨率」选择档位，然后点「启动游戏」，仍通过官方登录器正常登录。

- 默认开启分辨率实验，选择当前屏幕能容纳的最高 4:3 档位，上限 1600×1200。旧版默认关闭的配置会迁移一次；已有有效手选档位会保留。选择「跟随游戏原生设置（关闭实验）」后会记住关闭选择。
- 列表按窗口台当前所在显示器的实际分辨率筛选，宽、高均不超过显示器；换屏、屏幕设置改变、展开列表和启动前都会重新校验。
- 仅提供 4:3：640×480、800×600、1024×768、1152×864、1280×960、1400×1050、1440×1080、1600×1200。最大原生绘制尺寸为 1600×1200；实际可选项还受显示器宽、高限制。1920×1080 屏幕最高提供 1440×1080。
- 1280×960 及以上档位启用带版本守卫的 64×64 地图缓存，修复只扩大裁剪造成的角落缺图。离线边界检查已通过，1600×1200 地图及 FSR 缩放已获用户本机实测确认。旧 1920×1080 的失败记录保留在 [1080p 验证记录](docs/reports/viewport-1080p-validation-2026-11-06.md)，不提供宽屏档位。
- 实验只支持已核对的 cg_se_3000.exe 版本。启用前明确提示：独立 DLL 和运行时调整仍可能被检测或被认定为外挂，不能保证账号安全。原 EXE 和资源文件不改写。
- 实验组件只管理新增的 ddraw.dll、cgdesk_viewport_sys.dll、cgdesk_viewport.ini，以及自己的安装记录。遇到其他同名组件或已变化的文件会停止，不覆盖或删除。
- 改变实验档位、停用实验或升级组件前需要退出游戏；相同组件和相同档位可以继续启动其他客户端。选择原生设置再启动，会撤下校验通过的自有组件。
- 游戏出现后，窗口台按实际客户区大小自动调整，并限制在当前显示器工作区。再次拖动或最大化窗口后，原生绘制尺寸保持启动档位，显示使用 FSR 等比缩放；上限不随窗口增大而突破。显示区域始终按 4:3 居中，余下留边，避免变形。放大使用 EASU+RCAS，缩小使用线性采样+RCAS。采集失败会恢复可操作的原生窗口。
- 分辨率扩大不保证服务端会下发更远的角色。本版没有修改服务器、网络协议或资源刷新半径。

0.1.13 默认启用分辨率实验，保留启动前的风险确认。已删除旧画质增强开关、锐化界面、增强注入器、无效资源半径改动和旧试验部署/进程探测工具。保留外部窗口采集和 FSR 缩放；缩小后为避免源窗口露出，会把采集源放到屏幕边缘外、保留 1 像素以持续绘制。此前 0.1.12 的本机游戏测试正常。

## 窗口操作

1. 首次选择官方登录器，后续可点「启动游戏」。客户端出现后自动接入，也可用「接入窗口」手动选择。
2. 点右侧小窗或用热键切换主客户端。右键可备注或移回桌面并停止自动接入。
3. 「迷你控制台」将游戏放回桌面；展开后重新接入。「实时预览」控制其他客户端的预览更新。
4. 关闭窗口台会把游戏放回桌面，不关闭游戏。可以在设置中改为关闭时收进托盘。

默认快捷键为 Ctrl+Alt+1 … 9 切换窗口，Ctrl+Alt+右方向键切换下一个，Ctrl+Alt+0 重新接入。设置中可编辑热键、主题和关闭行为。设置保存在用户 AppData 下的 CgDesk/config.json。

攻略是按需打开的独立窗口，支持搜索、缩放、置顶并保存位置。窗口台支持签名更新清单和手动下载校验；更新源配置、签名和发布流程见 [更新发布文档](docs/update-publishing.md) 与 [更新源设计](docs/update-source-design.md)。不会自动替换正在运行的程序或关闭游戏。

## 验证

```powershell
dotnet run --project tests/CgDesk.Regression/CgDesk.Regression.csproj -c Release
dotnet run --project tests/CgDesk.Regression/CgDesk.Regression.csproj -c Release -- --probe-resolution
dotnet run --project tests/CgDesk.Regression/CgDesk.Regression.csproj -c Release -- --probe-scaling
dotnet run --project tests/CgDesk.Regression/CgDesk.Regression.csproj -c Release -- --stress-layout --enforce-clipping
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-SourcePrivacy.ps1
```

测试使用独立客户端夹具，验证分辨率过滤、旧配置迁移、组件所有权、原文件保持、窗口自动适配、切换、缩略图裁剪、IME、光标恢复、攻略和更新流程。测试程序不会登录真实游戏。用户另通过官方登录器完成了本机 1600×1200 与 FSR 显示验收；这不能代替账号安全、长期运行或其他客户端版本的兼容保证。

FSR 采集测试需要正常 Windows 桌面；沙箱隔离桌面会使系统采集接口返回 E_INVALIDARG。

## 原生实验组件

源代码在 tools/CgDesk.ViewportExperimentNative，随程序发布的独立组件在 src/CgDesk/Native/Viewport。普通 .NET 构建使用已打包组件；修改 C++ 后需要已有 MSVC x86 工具链，执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/CgDesk.ViewportExperimentNative/Build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/CgDesk.ViewportExperimentNative/Test-Smoke.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/CgDesk.ViewportExperimentNative/Test-MapCache.ps1
```

构建脚本会校验本机分析证据并更新随包 DLL 和哈希清单，不安装到游戏目录。未持有本机分析证据时不要重新生成该版本绑定组件。历史实验报告保留在 docs/reports，不能作为本次启动器集成的实机验收。

发布包通过 [GitHub Releases](https://github.com/359134032/cg-desk-public/releases) 分发。本地 release、artifacts、bin、obj 不入库；第三方许可见 [许可说明](docs/third-party-notices.md) 和包内 licenses 目录。
