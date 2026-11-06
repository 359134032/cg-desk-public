# 原窗口花屏：地图 stride 与 native sheet 生命周期复核

范围：仅使用既有 hash-bound runtime `.text` 和当前源码进行静态分析。未读取 PID39140、未执行游戏或捕获中的指令、未写游戏目录、未修改生产源码。以下区分已确认静态链与仍需实际 HRESULT 证明的故障条件。

## 结论

没有发现正常切图会把地图绘制恢复成固定51步长或旧数组地址的代码。正常切图不仅重算 bounds，还明确将已加载坐标置为−1，保证下一次 renderer 重新读层及生成碰撞层。当前更具体、与“声音操作正常但原窗口持续花屏”兼容的 native 反例，是 **sheet 像素上传 Lock 失败后，调用方仍把缓存 metadata 标为已更新，后续帧命中 metadata 而不再上传**。该反例由原捕获代码证明；尚未取得本次进程实际 Lock/HRESULT，不能认定它已经发生，也不能断言扩分辨率是该失败的原因。

## 地图加载与 stride 的实际契约

切图 `545560→49FEB0` 写入新地图 ID/尺寸，随后 `4A0420` 设置坐标，`49FE60→4A0420→4A0540` 再计算边界。`4A0420` 的汇编还明确包含：

- `4A0485` 选中保护整数 `CD39F0`，`4A04F1` 写入−1。
- `4A04F8` 选中 `CD3A34`，`4A04FD` 写入−1。
- 另两组已装载坐标在 `4A0509/4A0515` 同样写入−1。

`49BE00` 使用 `42C860` 比较当前保护坐标与这两组已加载坐标；它不是普通无参布尔开关，而是比较两个保护整数的“不相等”。因此新旧出生坐标相同时也不会绕过重装。

`49F9B0` 使用传入 `(x1−x0)` 作为三层文件读取目标步长；mode1 的 `49F8F0` 与 mode0 的 `49F500` 使用同一动态矩形生成碰撞层；`4A0620` 的接收更新以 `aa87bc` 定位当前 layer2；两套 draw 枚举同样使用 `aa87bc/aa87c0`。在这些已恢复路径里没有发现绘制用固定 `IMUL ...,0x33`。原扫描的固定51逻辑仍限于 legacy scratch，不作为新缓存的行步长。

正常 reset `49D800` 也不写被替换的代码操作数；全局数组原址没有被整段扩大清零。第一层原数组末尾 `A55C4A` 是独立画面缓存标志，新数组在独立内存，原 clear 长度5202原本就排除这个字节。

上述结论限于捕获到的直接代码路径，不能穷尽未捕获动态数据表或保护模块的运行时修补。

## 屏幕、surface、sheet 尺寸不能混为一组

| 字段/对象 | 契约与1600×1200时预期 |
|---|---|
| `5E0198/5E019C` | 全局屏幕尺寸1600/1200；捕获 `.text` 中仅启动函数499260含原800×600写入，没有切图直接重置 |
| `context+94/+98` | viewport尺寸；481100从上述全局初始化，back surface和D3D viewport均据此创建 |
| `11131CC/11131D0` | 逻辑资源sheet为64×48；这不是地图格子缓存51/64，也不是屏幕大小 |
| `context+B8/BA` (short) | 每个sheet加2像素边界，再按texture caps进行pow2/square取整，通常128×64，square-only时128² |
| `73ED58` | 55DAC0通过47A5B0按屏幕尺寸创建的工作surface，预期1600×1200 |
| `11131B8` | 同函数按屏幕尺寸创建的截图surface；普通路径1600×1200。若47A5B0失败而走47A3E0 texture fallback，可增加2像素并pow2至2048²等 |
| `context+14..+8C` | 复用的DDSURFACEDESC scratch；每次47A3E0创建小sheet都会改其中宽高，不能用它认定backbuffer变小 |

47A5B0的反编译有“Type propagation algorithm not settling”警告，表面上看像先写width再复制descriptor覆盖它。汇编 `47A619/47A61C` 证明 height/width 在复制之后重写，不能据错误反编译推导出64像素宽的屏幕surface。

CPU sheet 上传读取 Lock 返回的 `lPitch` 并按该pitch移动目标行，未发现把51或64格缓存尺寸当作像素行跨度。D3D UV 则除以context的sheet纹理尺寸；screen-sized截图复制使用另一路BltFast，不能把截图的1600尺寸套到小sheet UV除数。

## 已确认的像素上传失败缓存反例

具体链为：

`55BC30 draw queue → 55B8B0 sheet更新检查 → 47BA60 → 47BAD0/47CCD0 → IDirectDrawSurface7::Lock`。

1. `47BAD0` 汇编 `47BB30` 调用 vtable `+64h` (Lock)，`47BB33` 检查 HRESULT，`47BB35` 在非零时直接跳到退出 `47CC88`，未写像素。47CCD0同样只在Lock成功分支写像素。
2. `47BA60` 不向55B8B0返回一个受检查的上传成功标志。其调用后55B8B0没有 `TEST EAX`/条件跳转来阻止提交metadata。
3. `55BBAB` 调用上传，随后无条件到 `55BBC4`；`55BBC7/55BBCE/55BBD6/55BBDD` 写入当前palette、帧、色调等有效metadata，之后 `55BC22` 返回1。
4. 后续55B8B0入口 `55B8F0..55B918` 比较这些字段，匹配后直接返回1。因此不发生新帧/新色调等失效条件时，一次失败可能长期保留旧像素或未初始化像素。
5. 55BC30帧入口仅检查back surface `context+0C` 的 IsLost；单个sheet丢失而back仍正常时，渲染及游戏逻辑都可能继续。
6. `47A2C0` 有逐sheet的 IsLost 扫描，但已解析的唯一调用者是WndProc `498180` 的激活分支，并非每次地图切换都会执行。

此外47FED0的SetTexture返回值未由该函数分支检查，DrawPrimitive仍继续；如果SetTexture失败，可能继续使用上一张绑定的texture。它是另一个具体HRESULT判别点，仍需现场证据。

这两条失败链存在于原客户端捕获中，不是本组件新写出的逻辑。大视口可能增加资源压力，但“资源压力导致此次Lock失败”目前只是待验证前提，不作为结论。

## 最小后续判别

当前只读helper能核对全局/viewport/sheet字段，不能把复用的DDS descriptor当成真实surface GetSurfaceDesc结果。若需要新一版native诊断，应该在现有组件范围内分别记录源sheet的Lock HRESULT、SetTexture HRESULT、DrawPrimitive HRESULT，以及相关surface是否lost；必须在正常退出后更新组件，不能为取得结果绕过进程读取限制。

只要能确认一次“源sheet Lock失败 → 55B8B0metadata仍更新 → 后帧无上传却cache-hit”，上述静态反例就能与本次实际花屏相联系。反之如果upload/settexture/draw全成功且对应surface尺寸一致，应保留反例为原客户端健壮性问题，继续查其他native路径。

## 证据索引

完整新证据在 `artifacts/viewport-research/native-sheet-stride-evidence.json`，全部绑定 partial-image SHA256 `b95fdfaa61520908ed1a7b04fe75c74decb53a96860d17f25b8aef8a6a2b2636`。

- 4A0420已加载坐标失效：`ev_5cdf21953db83d821f031077d34e32302699a95521916eab5d2d5a8efcef8628`；保护整数比较：`ev_d1ab3d99d50a5f0364d8da69e9f2edc44c6f3ca4d7cbd9585abaca4c1253cbf0`。
- texture surface factory：`ev_6f6ebb6b3cfa1e0ec0524c9f9942d3c36b9d17f0595d80dbcd86ef77cb6c1f62`；screen surface factory及其汇编：`ev_9c7cc04b7682966b4a10b60f3820b2bad4ec398b490783ee9390016e017bbf13`、`ev_83c7dec6e7984cf70953a37b0f8f5c6831fb6b757403ea3fc6f4487799d91d23`；screen/sheet pool创建：`ev_18fa9afc3e7a08b607a10ba5dc421b4cde217f19204e13c868fdbc42e2e9f825`。
- draw queue：`ev_3dcdc143f646b3e1b95c85b89260df217a7518e41018838849dcca0392db50ae`；sheet有效metadata：`ev_22973084cb1ed2397de252d1a671c440ac054f44b2fad0fabc4717d340954dc8`、`ev_b256cdf9d53e9622daa4717d2f96c48beb524c9ec89de180d26fd22430631636`。
- Lock失败退出：`ev_5effa96963923a52dd4e9d64890e9da199763ccb32d15ecb4f69a52f72de12c0`、`ev_00539a889099b3acdc9ead5e11a4c53c5f6ddeb1df981ae1f7298dcbb6ea1f51`、`ev_f9e6a86daf91447f794ec5a12178691729cc126843f9d21d4c34e25e596f2a79`。
- sheet IsLost扫描及唯一调用者：`ev_987e131912eb012bdfd46db9bd2f16eaf4ab0f1bc60adf751874a9d7a4a10cfc`、`ev_bf4c0ae021cea930d1d6dbc1d8f233505126478e59cb103e820d9f4d54afb7eb`。
