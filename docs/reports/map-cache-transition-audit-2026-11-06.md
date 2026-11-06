# 0.1.13 切图卡住：64×64 缓存静态复核

本轮没有取得卡住时的游戏进程或线程栈，不能确认本次卡住的实际根因。只分析既有 hash-bound 捕获与当前 `MapCacheFix.cpp`，未运行游戏、未执行捕获指令、未写游戏目录、未改生产源码。源码 SHA-256：`c31947e085dc9283573d9372f0e3d481b075808b1e865a4d4eb6588a7af31623`。

## 可排除或降优先级的假设

- 正常地图切换不会只因 `boundsKnown` 没有主动清零而继承旧快照。唯一地图 setter 调用链为 `0x545560→0x49feb0` 设置新地图 ID/宽高，随后 `0x4a0420` 设置新坐标，之后必经 `0x49fe60→0x4a0540`。新 facade 因此重新记录新 legacy/expanded。原 reset `0x49d800` 则先将地图 ID/尺寸/坐标置零再计算 bounds，产生全零快照。
- UpdateBoundsHook 正常调用原函数一次，原来的四次保护坐标操作保持四次。LegacyScanHook 本身不调用保护坐标 getter；正常情况下调用原扫描一次。拒绝路径的操作次数会减少，而非增加。没有发现新增 getter 使保护整数计数/重置频率增多的证据。
- 原 `0x49ab80` 的三个调用点都使用 cdecl 整数参数和 EAX 返回值；原 bounds 的十个调用点均与无参数 void 契约一致。当前 facade 没有发现栈清理或参数宽度不匹配。
- Scan 拒绝返回 0 不会直接置位 `aa8810`：renderer `0x49be00` 的两个调用点返回 0 时反而清该等待标志；移动链 `0x49e7e0` 仅在返回非 0 时生成最多两个矩形并设置 `aa8810=1`。地图加载等待 `aa8834` 是另一条原协议路径，facade 未直接写它。
- 读图 `0x49f9b0` 参数取自动态矩形，三层目标已换 8192 字节；其逐行循环受矩形高度控制，正常 bounds 不超过64。Checksum 路径 `0x49dac0` 的三块栈数组仍按协议矩形和原5202 clear工作；map setter 和大缓存未把其参数换成 expanded bounds。Map-data 接收路径 `0x549e90→0x4a0620` 按原接收参数写文件并更新当前缓存，未改请求容量。
- 原总 draw 队列4096和两个地物队列各1024仍有拒绝/返回边界，保留上限没有发现可直接演变成新无限循环的路径。大地图/密集资源可能增加排队拒绝或素材加载负担，不能以离线回调测试排除真实渲染/磁盘/GPU阻塞，但也不能把“超过队列即卡死”当作已经证明的结论。
- 重查四缓存前8字节及one-past-end指令字面量未发现漏迁移的缓存迭代终点。`0xa55c4a` 是原第一层数组之后的独立状态字节，多处场景/UI代码访问它；新数组独立分配，既没有重定位该字节，也没有扩大原地址处的memset，因此没有发现适配覆盖这个flag。

## 当前缓存模块存在的条件性生命周期缺陷

这些是可以独立复现的行为缺口，尚无卡住时的运行证据证明它们发生了。

1. **非 OwnerThread 的 bounds 调用被整个跳过。** `Owner()` 在调用原 `0x4a0540` 前拒绝，所以不会执行原四次读取或更新六个字段。若实际切图在另一线程调用这条链，将保留旧 bounds。最优先验证 `ThreadOrReentryRejected` 是否从0增长，同时对照 `OwnerThread`、游戏窗口线程、调用线程。这是当前模块内最直接可观测的异常契约分支；静态正常帧链仍指向同一主线程，不能仅靠假设认定实际发生。
2. **没有 map ID/generation 绑定。** 模块只比较六个几何字段，未记录 `aa8790/aa8794`。若地图 ID 改变却没有走正常 bounds/cache reload，几何相同的旧 scratch 会被当作当前地图接受。正常已知 setter 链会刷新，因此这个反例需要额外的实际路径/时序证据。
3. **finally 无条件恢复进入扫描时的六字段。** 如果原扫描运行期间有未观测的并发地图变更，它会把新地图写入的 bounds 覆盖回旧地图 expanded。原扫描及正常 helper 没有找到 UI消息泵或游戏 callback，因此同步重入入口未获证明；动态保护模块或其他线程的间接访问仍未由 `.text` 捕获穷尽。

上述三项不应通过“忽略线程检查”或“继续返回1”来盲修；后者反而可能设置等待并发送错误矩形。先取得能区别这些分支的状态或线程栈，再选择按地图世代绑定/取消临时全局交换等方案。

## 新增离线验证

独立文件：`MapCacheLifecycleTests.cpp` 和 `Test-MapCacheLifecycle.ps1`。没有修改已有生产文件或前一份测试。以 x86 `/W4 /WX /O2` 编译当前源码，SEC_IMAGE 只读映射同一捕获；所有执行函数都是测试自身的回调。

结果：34项检查、0失败。五种正常跨地图尺寸/坐标变化及全零 reset 都保持当前地图 bounds、原扫描返回和读取次数。另明确观测了上述三种负例：changed-map-ID stale-scratch、wrong-thread stale-bounds、scan期间换地图被 finally 覆盖。这些负例的预期就是暴露缺少保护，所以0失败不代表相应缺陷不存在。

输出记录：`artifacts/viewport-research/map-cache-lifecycle-tests.txt`。新静态证据导出：`map-cache-lifecycle-evidence.json`，不能据 synthetic host 结果宣布真实切图已经修好。

## 下次复现的最小判别信息

- 区分“游戏窗口也停止重绘/无响应”与“只有 cg_desk 采集画面停住”。窗口台采集/GPU链卡住不等于游戏地图线程卡住。
- 若进程还在，读取缓存诊断（尤其 ThreadOrReentryRejected/LastError/BoundsCalls/LegacyScanCalls）和当前地图 ID/宽高、六个 bounds、`aa8810/aa8834`。优先取得游戏窗口线程的阻塞栈及CPU状态；没有这些证据，不给某个假设贴已确认根因标签。
- 用同一路径比较关闭64缓存的小档和大档，并独立比较 FSR 开/关。它能隔离地图缓存组与窗口采集组，避免同时改多个模块后丢失复现条件。

## 核心证据

- 正常切图 setter：`ev_0a91a752137679bf012f85c56f60db273c69b49fd36ff42cd7e745c9afe06dfe`；地图字段 setter：`ev_4212f90adc3d97a5965c4c140e8a41973fa5e04fbb7338c7d531a71b891e3a77`；唯一调用者：`ev_c2894639f3e8f5527a1ef7b61f130c6aa43f2abe7fe6411f3f81f3f0313e49f7`。
- setter 后刷新：`ev_cdc3dd9dadec672d1554bb8699d7ee355a018b98045dff22e45b5393df267e77`；reset：`ev_a36b694fa40d3c604c52cb2cd154ffe81bf155d9f6418778ea1e4abf9501afad`。
- 原 renderer/scanner 返回：`ev_704b34bc91b0b936d7a90815f5b49700d5632acc11d01396428e0592635eca3e`、`ev_b972fadb0ae98fee1c94f9485f5e5ae524f3bf73c62903dfbc8e48f89e4f885e`。
- map-data 接收/写入：`ev_a637e1b16ca7bf4a629486405d818e55a7fc44c1d31f00125892631d6a5c45b9`、`ev_ea8141b1e431038afbe5d1f1a5dd7b7fe78c6428a12690c13e420b8e998b2659`。
- 原 draw 上限：`ev_976ab7e86b97fdc10657d8320899eb2d905b5f4520f0604ee1926e5b0af1d5c8`；两类地物上限：`ev_e319a85fa574d63a5444bf60b3c33cfcd441242aa0e6441cd1820bfe54477446`。
