# Agent MCP 联机集成测试（协议 v10）

单元测试只能检查代码路径，不能证明两个 ONI 进程中的对象生命周期正确。这里的集成测试通过主机 OniMcp 和受限 Debug command 驱动真实游戏，再从主机和客户端各自新增的 `Player.log` 内容判断结果。协议 v10 不兼容旧 wire format，旧 DLL 会收到明确的拒绝原因和 ACK 请求，主机在收到 ACK 或等待两秒后断开连接。

## 边界

- OniMcp 只连接主机的 loopback 地址。它负责只读地图检查、速度控制，以及 `remote-dig` 的主机世界操作。
- ONI Together Debug command file 负责启动联机、状态检查、checkpoint、测试和 soak。`build:<prefab>:<cell>:<material>` 是唯一的建筑写入入口，它必须在同步窗口开放时通过 `BuildAuthority` 创建非 instant queued building，并把权威状态发给客户端。
- 客户端不接收 MCP 写操作。它只运行联机 Mod，并由测试器读取日志。
- 客户端的复制人工作、移动和动画同步只能写 `KBatchedAnimController`、Facing、工具视觉、进度 UI 和渲染位置；不得调用 `StandardWorker.StartWork/StopWork`、`Navigator.ClientGoTo/AdvancePath/GoTo`，也不得提交建筑、库存、资源或实体生命周期。
- 主机继续提交 NetId、spawn/despawn、tombstone、建筑、库存、任务结果、科技、日程、火箭、baseline 和 reconnect。
- 测试器先记录两端日志的 byte offset，只检查操作之后的内容。旧日志不能让测试误报成功或失败。
- 两端 `ONI_Together.dll` 的 SHA-256 必须一致。preflight 还会向两端提交 `status` Debug command，核对协议 v10、主客角色、同一个 host ID、DLC、`InGame`/`Started` 状态、远端连接数和同步窗口。状态行同时暴露 repair cut、journal、resolved、deferred、observing、ACK target、ACK sent、native tx 计数和 Steam queue。测试器还会记录主机 `OniMcp.dll` 的 SHA-256；主机 MCP 不可用或任一游戏未运行时直接失败。客户端必须关闭 OniMcp。

测试分成两个独立层级。C# headless 审计层使用 Roslyn 按真实 Debug 编译条件解析生产源码，并用 C# 重放确定性生命周期模型，不启动 ONI，也不连接 Steam；真实双机层验证 Unity 对象生命周期、Harmony 触发和主客最终状态。审计器沿 `IPacket` 继承关系还原运行时注册面，并检查所有 packet 的接口成员、可实例化性和方向 marker；同时枚举所有 Harmony patch 类，要求每个 patch 都有可执行入口。人物生命周期模型检查 printing pod preview 的 Personality、`MinionIdentity.OnSpawn`、`CharacterContainer.SetController/SetMinion` 和 `Flatulence.Emit` 顺序，并对三类已知故障做反向注入。

当前 Debug 源码清单包含 234 个可注册 packet 和 478 个 Harmony patch 类。`PacketRegistrationHelper` 已排除 interface 与 abstract 类型，不再错误注册 abstract `DragToolPacket`。这两个数字是当前有限接口面的结构性穷举，不等于穷举 Unity 所有运行状态：`EventExtensions.Trigger` 的通用同步入口仍被 `return; // Disabled for now` 禁用，不能写成已覆盖事件；prefab、协程、状态机和跨帧副作用仍必须由真实双机层验证。

MCP 是测试控制面，ONI Together 是被测数据面；实体存在、资源归属、建筑完成等不可逆状态仍由主机提交。Steam 和 Riptide 使用各自的 native reliable。应用层只保留 world baseline、repair、keyframe 和 Ready replay 的 Unity apply proof，不再实现 ordered ACK、通用分页、重传或重排。

## 场景

真实双机业务矩阵包含 22 个场景，不再只验挖掘和建造：

| 业务域 | 场景 |
|---|---|
| 基础动作 | `remote-dig`、`building-lifecycle` |
| 控制 | `research`、`priority`、`schedule`、`building-config`、`door`、`uproot`、`toggle` |
| 物品与拆除 | `inventory`、`storage`、`pickup`、`deconstruct` |
| 表现与社交 | `effect`、`chat`、`cursor`、`animation`、`motion` |
| 生命周期与 DLC | `entity-lifecycle`、`dlc-runtime`、`rocket` |
| 重连 | `reconnect-world-state` |

每个场景都必须产生结构化 `[IntegrationEvidence]`，同时证明：主机提交、客户端应用、客户端原版路径被阻止、合法 revision 被应用、重复和乱序 revision 被拒绝，以及主客最终 canonical state/hash 一致。`reconnect-world-state` 还必须证明新 connection generation 和 snapshot generation 上的 post-reconnect state/hash 与重连前终态一致。工具调用成功、命令返回成功或日志里出现单个 apply 行都不构成场景通过。

`remote-dig` 要求主机注册 `DigPlacer`，发送带 revision 的 `DuplicantPresentationBatchPacket`，并看到客户端 `RemoteDuplicantPresenter` 应用同一个 target cell。客户端只显示动画、工具和进度，不启动原版工作状态机。以下任一记录都会判失败：

- `Diggable.GetConversationTopic`
- `StandardWorker.StartWork` 或 `Worker.StartWork(Diggable)`
- `originalDigElement`
- `StandardWorker_WorkingState_Packet`

`building-lifecycle` 先要求客户端在目标 cell 收到 `Queued` 状态并完成 `BuildingDef.TryPlace` 物化，再等待主机发出 `BuildCompletePacket` 和客户端完成最终建造。这样会同时经过 `TileUnderConstruction` 与完成建筑两段生命周期。`Constructable.OnSpawn`、`SelectedElementsTags`、未初始化的完成建筑组件或 NetId 冲突都会判失败。

`checkpoint` 直接调用生产环境的 `ProductionDesyncRecovery.TryBeginCycleProbe`。它不修改世界，只把等待一个自然周期的检查缩短为一次显式触发。客户端 repair ACK 跨过下一 Unity frame 后发送，因此游戏暂停时也能完成 apply barrier；hash fence 继续负责证明最终状态与主机一致。

`hard-sync` 检查存档传输、generation-bound world baseline、Ready 前可靠 backlog 和最终提交。每个 `DeferredReliableBatchPacket` 不超过 64 KiB，并携带 `SnapshotGeneration`、`ReplayId:ulong`、`BatchIndex` 和 `BatchCount`。客户端只有在同 generation、同 replay 的全部 batch 成功 dispatch 后才发送一个 `ReadyReplayAppliedPacket`；主机验证 sender、connection generation、reconnect token、snapshot generation、replay ID 和 batch count 后提交 Ready。`replay-load:<frames>:<payloadBytes>` 只能在主机记录 `Snapshot <generation> started` 后提交；在 baseline cut 之前会返回 `ready-replay-window-not-open`。测试器把 `steam-join:*`、`build:*`、`replay-load:*` 归一化到日志中的 command family，并给建造与 replay 命令 15 秒响应时间。

## 使用

### 不启动游戏的 headless 契约测试

以下命令不读取 `Player.log`，不连接 OniMcp，不要求 Steam 登录，也不会启动 ONI：

```bash
dotnet run --project ONI_Together.HeadlessTests/ONI_Together.HeadlessTests.csproj --no-restore
```

该命令运行纯 .NET 8/C# 审计器，不使用 Unity、Steam、NuGet 或 Python，也不会假装实例化真实 `GameObject`。当前应输出 `production packets=234 Harmony patches=478` 和 `12/12 passed`。门禁包括：运行时 packet 注册规则、接口成员、公开无参构造、方向 marker 冲突、Harmony patch 入口、abstract 类型排除，以及人物创建顺序。生命周期测试还会注入 preview 条件反转、跨帧设置 Minion、`SetMinion` 早于 controller 三种故障；任一故障未被识别都会失败。

无游戏层对当前已注册入口做结构性穷举，可以阻止新增 packet 或 Harmony patch 在未满足公共契约时进入代码库。它不执行 Unity 的 `GameObject`、prefab、协程和状态机实现，因此不能证明每个 `OnDispatched` 在所有世界状态下都没有空引用，也不能证明任意 spawn/despawn、建筑物化或重连后的真实帧行为。已建模的 printing pod 回归可在命令行直接拦截；22 个真实双机场景继续覆盖业务语义、Unity apply 和主客终态。

Python 只保留在真实双机层，负责 SSH、MCP、Debug command、日志 offset 和证据判定。这些是进程外编排，不用于模拟 C# 或 Unity 生命周期。

### 真实双机测试

默认主机是本机，客户端是 `ssh mm`，OniMcp 地址是 `http://127.0.0.1:8788/mcp/`。

```bash
conda run -n base python scripts/oni_integration.py preflight
```

先记录日志起点：

```bash
conda run -n base python scripts/oni_integration.py begin remote-dig --expect-cell 12345
```

然后让 Agent 调用主机 MCP，或者直接使用测试器的 MCP 客户端。先以运行时 `tools/list` 或 `oni://tools/manifest` 为准生成参数；`arguments` 必须是该工具的 JSON object，也可以写成 `@文件路径`。当前接口不接受 raw coordinates，挖掘应先取得只包含目标 cell 的 `areaId`。`--expect-cell` 必须填写该目标，避免同一存档中的其他挖掘或建造让测试误绿。

```bash
conda run -n base python scripts/oni_integration.py mcp-call orders_control '{"task":"联机挖掘同步测试","domain":"area","action":"dig","areaId":"a1","confirm":true}'
```

操作完成后检查两端新增日志：

```bash
conda run -n base python scripts/oni_integration.py verify --timeout 120
```

已经知道 MCP tool 和参数时，可以一次完成 preflight、操作和验证：

```bash
conda run -n base python scripts/oni_integration.py run remote-dig orders_control '{"task":"联机挖掘同步测试","domain":"area","action":"dig","areaId":"a1","confirm":true}' --expect-cell 12345 --timeout 120
```

Debug command 必须串行提交。已有命令或 `.processing` claim 未消费时，新命令会失败，不会覆盖旧命令。

```bash
conda run -n base python scripts/oni_integration.py debug-command host soak
conda run -n base python scripts/oni_integration.py debug-command host checkpoint
conda run -n base python scripts/oni_integration.py debug-command host hard-sync
conda run -n base python scripts/oni_integration.py debug-command host reconnect-evidence
conda run -n base python scripts/oni_integration.py debug-command host replay-load:512:4096
conda run -n base python scripts/oni_integration.py debug-command host build:Tile:94290:SandStone
conda run -n base python scripts/oni_integration.py debug-command client tests
```

建造场景要先执行 `begin building-lifecycle --expect-cell <cell>`，再提交同一 cell 的 `build:` 命令。不要使用 OniMcp 的直接建筑编辑接口代替它；该接口不会经过 ONI Together 的客户端请求与权威状态发布链路。

## 流量证据

`status` 的 native counters 按 motion、animation（含 duplicant presentation）和 cursor 分类。采样器每秒记录两端累计值、Steam queue time 和 unacked reliable bytes。改前、改后必须使用同一存档和同一 630 simulation seconds 场景。

```bash
conda run -n base python scripts/oni_integration.py traffic-sample --duration 630 --interval 1 --output .codex_tmp/traffic-before.json
conda run -n base python scripts/oni_integration.py traffic-sample --duration 630 --interval 1 --output .codex_tmp/traffic-after.json
conda run -n base python scripts/oni_integration.py traffic-compare .codex_tmp/traffic-before.json .codex_tmp/traffic-after.json --minimum-reduction 60
conda run -n base python scripts/oni_integration.py traffic-health .codex_tmp/traffic-after.json
```

Ready replay 单独记录命令窗口，避免 630 秒背景流量掩盖 native call 数：

```bash
conda run -n base python scripts/oni_integration.py replay-measure host --output .codex_tmp/replay-after.json
conda run -n base python scripts/oni_integration.py replay-compare .codex_tmp/replay-before.json .codex_tmp/replay-after.json --minimum-reduction 50
```

## 验收顺序

1. 两端安装同一份 Debug DLL，启用 ONI Together；只在主机启用 OniMcp。
2. 两端进入同一个测试存档并达到 `InGame`，再运行 preflight。
3. 触发一次 `checkpoint`，确认 host 记录 `Causal checkpoint matched`，客户端 fence 的 `resolved` 最终达到 cut，且 `observing=0`。
4. 逐项执行 22 个业务场景；每项都用独立日志 offset 运行 `begin`/`verify`，不得用上一场景的证据补齐当前场景。
5. 执行 `hard-sync`。等主机出现本次 generation 的 `Snapshot ... started` 后注入 `replay-load:512:4096`，必须看到客户端 `ReadyReplayLoad Applying/Applied`、主机 `SyncBacklog Replay applied` 和最终 Ready commit。
6. 用 AppleScript 正常退出客户端，再用 `open 'steam://run/457140'` 启动并执行 `steam-join:<lobby>`；preflight 必须看到新的 connection generation。
7. 采集 motion、animation、cursor 的 630 秒改后流量，并与同一存档的改前证据比较。六项 calls/bytes 都要下降至少 60%，`replay-load:512:4096` 的 native calls 要下降至少 50%。
8. 场景通过后关闭 OniMcp，重启主机并跑 21 段、37,800 ticks、630 simulation seconds 的发布 soak。完成后必须运行 `soak-verdict`；它要求恰好 21 次 `POST_KEYFRAME_COMPARE`、`COMPLETE` 标记存在、没有失败标记，且 time、grid、entity、world、storage、clusterRocket 全部一致，lifecycle missing、unexpected、tombstoned、unassigned 全部为零。
9. `txFailures` 必须为零；Steam queue 最大值低于 1 秒、P95 低于 500 ms；unacked reliable 最大值低于 64 KiB、P95 低于 16 KiB。任何 queue overflow、snapshot lease expiry、barrier timeout、apply failure 或断线都判失败。

## 实机记录

### 2026 年 7 月 20 日：改前稳定性基线

本机 Mac 主机和 `ssh mm` Mac mini 客户端使用相同 Debug DLL `7dae355a37ddc67d9bd18aa857831da1edb7f1f6fefb5d9077629866c56796a9` 完成测试。Cycle 119 的 1,040-part baseline 用时约 300.6 秒，随后进入 `InGame`。repair baseline 从满载 128-entry journal 排空后，soak 完成 21 段、37,800 simulation ticks、630 simulation seconds。

21 次 `POST_KEYFRAME_COMPARE` 的 time、grid、entity、world、storage、clusterRocket 全部一致，missing、unexpected、tombstoned 和 unassigned lifecycle counters 全部为零。终态为 `postMismatchSeen=False`、`keyframeApplyFailureSeen=False`、`postKeyframeEqual=True`。`BASELINE_READY` 后未出现 queue overflow、snapshot lease expiry、barrier timeout、soak failure、soak abort 或连接丢失。

这次记录没有 motion、animation、cursor 的 native counters，不能充当 v10 的 60% 流量门禁证据。

### 2026 年 7 月 21 日：协议 v10 早前状态

- 已部署并经 Mac mini 游戏内测试的 Debug DLL SHA-256：`c9af66a2fda9a22e62065fd413a80d5d3fcfa0860c89f8ad1479052447eba32a`；本机与 Mac mini 文件 hash 一致。
- 使用本机 ONI `Managed` 路径和 `DOTNET_ROLL_FORWARD=Major` 构建成功，4 warnings、0 errors。仓库原始 `dotnet build ONI_Together/ONI_Together.csproj -c Debug` 会命中 Windows 游戏路径和缺失 .NET 6 publicizer，不能作为 macOS 构建命令。
- 当前 ILRepack 输出不是字节级可复现构建：同一源码连续构建得到不同 SHA-256。重新构建后的 DLL 必须作为新工件重新部署并重跑游戏内验收，不能沿用 `c9af...` 的实机证据。
- Mac mini 重启后在真实 U59 游戏进程中运行更新 DLL：`total=556, passed=530, failed=0, notRun=26`。日志证据为 `.codex_tmp/evidence/20260721-v10-macmini-ingame-unit-tests.log`。
- Python runner：33 tests 全部通过；五个 Python 文件通过 `py_compile`。
- MacBook 本机 Steam client 无法连接 CM：`PingWebSocketCM ... timeout/neterror`、`ConnectFailed`、`Logged Off`，ONI 随后记录 `Can't init Steam distribution platform` 并退出。Mac mini 同时保持 `ConnectionCompleted ... WebSocket` 和 `Logged On`。本机普通 HTTPS 可用不能证明 Steam CM 正常。
- 因主机进程无法启动，本轮没有执行 v10 的 `remote-dig`、building、checkpoint、hard-sync、fresh-generation reconnect、630 秒 soak 和流量对比。当前 DLL 不能标记为发布验收通过。

### 2026 年 7 月 21 日：全量业务矩阵实现

- `SCENARIOS` 已扩为上述 22 项；Python verifier 对所有场景执行同一份六类证据契约，并对 reconnect 和 soak 增加严格终态门禁。
- 各业务域已在真实 host-submit/client-apply seam 记录结构化证据；revision 探针调用各域现有 guard，不用模拟日志替代生产路径。
- 首次真实 host/join 暴露 spawn 证据递归：`SpawnPrefabPacket.Serialize` 内捕获全局状态会重新分配 identity 并再次序列化 spawn 包。实现已改为只由 packet wire fields 生成 canonical state，避免任何世界读取或发送；对应游戏内测试已加入。
- 当前部署 Debug DLL SHA-256：`0de7fdbb6369ddc4b92bba3cd02fbf6907383ecb37cb0fe8467efbcb8dc3fad1`，本机和 Mac mini 文件一致。
- Mac mini U59 游戏内测试连续两次通过：`total=665, passed=641, failed=0, notRun=24`。Python runner 39 tests 全部通过；相关脚本通过 `py_compile`；Debug build 为 4 warnings、0 errors。
- 全量双机业务验收仍未完成。本机 Steam 重启后仍以 `steamid=0` 运行，ONI 记录 `Can't init Steam distribution platform` 后退出；Mac mini 已登录并可稳定运行。必须先人工恢复本机 Steam 登录，再重跑 22 场景、reconnect 和 630 秒 soak，不能把上述实现/单测结果写成联机验收通过。

脚本自身测试：

```bash
dotnet run --project ONI_Together.HeadlessTests/ONI_Together.HeadlessTests.csproj --no-restore
conda run -n base python scripts/test_oni_integration.py
conda run -n base python -m py_compile scripts/oni_integration.py scripts/oni_scenario_contracts.py scripts/oni_mcp_client.py scripts/oni_target.py scripts/oni_traffic_evidence.py scripts/test_oni_integration.py
```
