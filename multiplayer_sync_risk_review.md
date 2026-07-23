# ONI Together 联机同步风险审查

## 审查结论

- [F][HIGH] 本轮新增两个 P0 风险簇：网格状态缺少可收敛基线，以及实体身份与生命周期缺少单一权威来源。
- [F][HIGH] P1 确认风险包括无 revision 状态回写、storage transfer 旧意图复活、Mod API 权限标记丢失、启用模组指纹只覆盖元数据，以及多客户端 relay 缺少权威回显。
- [I][MED] transport/bulk 发送失败后清批列为 P1 待 probe；Soak hash 覆盖盲区列为 P2。
- [F][HIGH] 已知的 storage rebuild NRE、sample0/sample22 grid 与 registry hash 不一致仅作为因果锚点，不计入上述新增风险数量。
- [F][HIGH] 本轮只读审查源码、测试、双机 soak 日志和上游固定提交；未修改源码、配置或既有文档。

严重度口径：

- [COMMON][HIGH] P0 表示正常联机路径可形成永久分叉，且当前协议没有自动收敛闭环。
- [COMMON][HIGH] P1 表示特定网络、模组或多客户端条件下可破坏一致性或会话正确性。
- [COMMON][HIGH] P2 表示影响边界较窄，或还需要运行时 probe 才能确认最终后果。

证据状态口径：

- [COMMON][HIGH] observed 表示当前双机日志或采样直接出现。
- [COMMON][HIGH] code-proven 表示给定输入序列可沿现有分支静态推出结果。
- [COMMON][HIGH] trigger-dependent 表示危险分支存在，真实运行是否进入仍需定向 probe。

## 已知证据锚点

- [F][HIGH] 既有 630 sim sec 双机 soak 共记录 37,800 tick、23 个采样点；sample0 与 sample22 的 grid hash、registry hash 均不一致，客户端未断线或崩溃。
- [F][HIGH] 既有日志中有 7 个客户端 storage state packet 在 `BuildingUtils.ClearStorage:126 -> RebuildFromBlob:86 -> RebuildStorageFromData:72` 触发 NRE。
- [I][HIGH] 这些证据证明当前会话可以持续运行却不收敛；它们不能单独定位下列风险簇，本报告以源码路径补足因果链。

## P0-1：网格复制触发条件弱于验收 hash，静态差异无法自愈

### 结论

- [F][HIGH] `WorldStateSyncer` 只在 host 运行；初始化时它把 host 当前 element/mass 直接记为 shadow，且不发送全图基线。载入后已经存在的 client 静态差异因此不会产生增量包。证据：`ONI_Together/Networking/Components/WorldStateSyncer.cs:174-179`、`ONI_Together/Networking/Components/WorldStateSyncer.cs:676-688`。
- [F][HIGH] 仓库内 `WorldDataRequestPacket` 只有定义与测试引用，没有 production 发送调用；其完整 world data 路径不会补上该基线。证据：`ONI_Together/Networking/Packets/World/WorldDataRequestPacket.cs:10-66`。
- [F][HIGH] 后台扫描只以 element 变化或 mass 差值大于 `0.01f` 作为发送条件，temperature-only、disease-only 和不超过 10g 的 mass 差异不会触发复制。证据：`ONI_Together/Networking/Components/WorldStateSyncer.cs:793-818`。
- [F][HIGH] `WorldUpdatePacket` 实际携带并应用 element、temperature、mass、disease index、disease count。证据：`ONI_Together/Networking/Packets/World/WorldUpdatePacket.cs:17-24`、`ONI_Together/Networking/Packets/World/WorldUpdatePacket.cs:101-137`。
- [F][HIGH] `SimMessages.ModifyCell` 的 host Prefix 接收 `replace_type`，入队时没有保存；receiver 对所有更新固定使用 `Replace`。本机 `Assembly-CSharp.dll` IL 显示 `AddRemoveSubstance` 与 `ModifyMass` 传 `None`，`ReplaceElement` 传 `Replace`，`ReplaceAndDisplaceElement` 传 `ReplaceAndDisplace`；前后两类会在 client 被改成普通 `Replace`。证据：`ONI_Together/Patches/World/SimMessagesPatch.cs:13-38`、`ONI_Together/Networking/Packets/World/WorldUpdatePacket.cs:132-137`。
- [F][HIGH] soak hash 对 mass 的精确 bit、非零质量格的 temperature bit、disease index/count 都敏感。复制触发域小于验收域。证据：`ONI_Together/DebugTools/SoakStateHash.cs:80-96`。
- [F][HIGH] 证据状态：sample0/sample22 分叉为 observed；shadow、扫描字段和 replace type 折叠为 code-proven；当前 soak 中各 `replace_type` 的真实分布仍需 probe。

### 触发、后果与测试缺口

- [I][HIGH] 触发条件包括快照载入误差、只改变温度或病菌的模拟步骤，以及小于等于 10g 的质量变化。
- [I][HIGH] 后果是双方可能长期保留不同温度、病菌或微量质量，进而在相变、病菌反应和资源生成处继续放大分叉。
- [F][HIGH] 当前 `WorldUpdatePacketTests` 只覆盖超大 payload 与权限，`SoakStateHashTests` 只证明 hash 对字段敏感；没有“载入后全字段基线收敛”、temperature-only/disease-only 增量或 replace type 保真测试。证据：`ONI_Together/DebugTools/UnitTests/WorldUpdatePacketTests.cs:8-39`、`ONI_Together/DebugTools/UnitTests/SoakStateHashTests.cs:1-92`。

### 成熟项目机制

- [F][HIGH] OpenTTD 在固定提交 `eb62295` 的加入流程中，把存档生成前已接收但尚未执行的本地命令队列同步给新客户端，并明确说明漏掉这些命令会导致新客户端 desync。[上游源码](https://github.com/OpenTTD/OpenTTD/blob/eb62295cc08fe791b6de0061467a4c827da5fa05/src/network/network_command.cpp#L237-L251)
- [F][HIGH] RimWorld Multiplayer 的加入快照显式包含 scheduled commands，服务端在 join point 暂停并创建快照。[快照结构](https://github.com/rwmt/Multiplayer/blob/4a3be276bbf90cc597abfa5b299935ca8eeeb285/Source/Client/Session/GameDataSnapshot.cs#L6-L12) [加入状态](https://github.com/rwmt/Multiplayer/blob/4a3be276bbf90cc597abfa5b299935ca8eeeb285/Source/Common/Networking/State/ServerJoiningState.cs#L14-L40)
- [I][HIGH] 可迁移机制是“快照 + 明确 join boundary + 边界后的权威日志”，避免客户端从未确认的本地状态建立复制 shadow。

### 最小验证与根因修复方向

- [I][HIGH] 最小 probe：暂停在 Ready 边界，逐字段定位首个 grid 差异；分别注入 temperature-only、disease-only、5g mass 变化，确认当前扫描不发包；统计真实 `replace_type` 分布并逐枚举回放；随后显式发送一次完整基线，观察 grid hash 是否收敛。
- [I][HIGH] 根因修复方向：快照载入后按 generation/tick 发送权威全字段基线；shadow 覆盖全部正确性字段；客户端回报同一 domain hash 后才完成 Ready 验收。

## P0-2：实体 NetId 与生命周期由本地模拟和启发式匹配共同决定

### 结论

- [F][HIGH] 建筑和 Workable 优先使用基于 cell、prefab、layer/type/component index 的确定性 NetId；碰撞后 host 改用随机 `Guid.GetHashCode()`。证据：`ONI_Together/Networking/Components/NetworkIdentity.cs:49-95`、`ONI_Together/Networking/NetworkIdentityRegistry.cs:23-35`、`ONI_Together/Networking/NetIdHelper.cs:13-45`。
- [F][HIGH] client 对未分配对象按 prefab hash 与两格以内最近位置认领 host NetId；候选来自字典枚举，同距离没有稳定 tie-break。证据：`ONI_Together/Networking/NetworkIdentityRegistry.cs:105-137`。
- [F][HIGH] client 等待 30 秒仍未确认的本地 spawn 会被销毁。证据：`ONI_Together/Networking/NetworkIdentityRegistry.cs:139-159`。
- [F][HIGH] host 的权威 spawn 广播只覆盖 loose item 或 creature；发生碰撞后被随机重分配的建筑/Workable 没有同等身份纠正路径。证据：`ONI_Together/Networking/Components/NetworkIdentity.cs:134-152`。
- [F][HIGH] 通用 `DespawnEntityPacket` 在目标尚未注册时直接返回，且不记录 tombstone；后到的 spawn packet 仍可创建同一 NetId。证据：`ONI_Together/Networking/Packets/World/DespawnEntityPacket.cs:27-35`、`ONI_Together/Networking/Packets/World/SpawnPrefabPacket.cs:103-140`。
- [F][HIGH] 资源、Pickupable、建筑和所有 Workable 都会进入该身份路径；客户端实例化前缀仍返回 `true`，文件中的批量纠正后缀已被注释。证据：`ONI_Together/Patches/World/SubstancePatch.cs:18-31`、`ONI_Together/Patches/World/PickupablePatches.cs:12-29`、`ONI_Together/Patches/World/Buildings/BuildingComplete_Patches.cs:16-27`、`ONI_Together/Patches/World/WorkablePatch.cs:31-39`、`ONI_Together/Patches/KleiPatches/KInstantiatePatch.cs:18-54`。

### 运行证据、后果与测试缺口

- [F][HIGH] 当前 630 sim sec 客户端窗口内出现 3,790 次等待 host-assigned NetId、32 次销毁未确认 spawn、22 次 NetId lookup failure；host 同期至少出现一次确定性 NetId 碰撞并随机重分配。
- [F][HIGH] 证据状态：等待、销毁、lookup failure 与碰撞为 observed；启发式认领、30 秒删除和无 tombstone 为 code-proven；迟到 spawn 是否在当前 transport 顺序下复活为 trigger-dependent。
- [I][HIGH] 触发条件包括双方资源生成顺序或位置不同、同 prefab 多候选、位置偏差超过两格，以及确定性 key 碰撞。
- [I][HIGH] 后果包括客户端删除 host 真实对象、重复保留对象，随后 storage、chore、animation 或 structure 包继续命中缺失 NetId。registry 数量从 sample0 相差 89 到末次仍相差 83，与该风险簇一致，但现有证据不足以把全部差额归因于同一条路径。
- [F][HIGH] `LifecyclePacketTests` 覆盖注册、序列化、权限、幂等和 element 边界，没有覆盖碰撞、双模拟 spawn 配对或 30 秒过期删除。证据：`ONI_Together/DebugTools/UnitTests/LifecyclePacketTests.cs:10-74`。

### 成熟项目机制

- [F][HIGH] Mindustry 客户端从服务端快照读取 entity type 与精确 id，创建映射类型、赋相同 id，再 snap/add；后续快照仍按该 id 更新。[上游源码](https://github.com/Anuken/Mindustry/blob/a39660ec88961476c110e9e6073ef6b041beea9b/core/src/mindustry/core/NetClient.java#L504-L554)
- [COMMON][HIGH] 服务器权威实体复制的稳定边界是“host 分配 identity，client 消费 identity 与 lifecycle journal”；空间接近度适合显示层预测，不适合作为权威身份判定。

### 最小验证与根因修复方向

- [I][HIGH] 最小 probe：构造两个同 prefab、同距离候选和一次确定性 key 碰撞，分别反转客户端插入顺序；记录最终 NetId、30 秒销毁对象及 registry hash。
- [I][HIGH] 根因修复方向：使用单一 host allocator 和显式 create/destroy journal；快照携带完整 NetId、type、world、position 绑定；客户端不得把本地 spawn 的启发式匹配结果注册为权威身份。

## P1-1：绝对状态包没有 revision，旧包可以覆盖或复活新状态

### 结论

- [F][HIGH] `WorldCyclePacket` 没有 sequence/tick 字段，host 每 1 sim sec 以 Unreliable 发送，client 收到任何合法来源包都会调用 `SetTime`。证据：`ONI_Together/Networking/Packets/World/WorldCyclePacket.cs:22-43`、`ONI_Together/Patches/GamePatches/GameClockPatch.cs:76-85`。
- [F][HIGH] `StructureStatePacket` 同样没有 revision；周期状态以 Unreliable 发送，按需响应以 ReliableImmediate 发送，两条路径最终都无条件应用。证据：`ONI_Together/Networking/Packets/World/StructureStatePacket.cs:22-28`、`ONI_Together/Networking/Packets/World/StructureStatePacket.cs:112-125`、`ONI_Together/Networking/Components/StructureStateSyncers/StructureSyncerBase.cs:93-113`、`ONI_Together/Networking/Components/StructureStateSyncers/StructureSyncerBase.cs:144-163`。
- [F][HIGH] `WorkableProgressPacket` 没有 revision；work tick 的 `show=true` 以 Unreliable 发送，StopWork/Cancel 的 `show=false` 以 ReliableImmediate 发送，receiver 无条件设置进度条状态。旧 `show=true` 可在新 `show=false` 后到达并复活 UI/remote progress。证据：`ONI_Together/Networking/Packets/World/WorkableProgressPacket.cs:13-19`、`ONI_Together/Networking/Packets/World/WorkableProgressPacket.cs:142-200`、`ONI_Together/Patches/WorkProgressPatch.cs:44-52`、`ONI_Together/Patches/DuplicantActions/StandardWorker_Patches.cs:61-83`。
- [F][HIGH] storage 的 full snapshot 只编码 prefab hash、mass、temperature、disease，不携带 item NetId；应用时先删除现有对象，再重新生成并存入。证据：`ONI_Together/Misc/BuildingUtils.cs:33-64`、`ONI_Together/Misc/BuildingUtils.cs:79-128`。
- [F][HIGH] storage snapshot 复用无 revision 的 `StructureStatePacket`，周期发送为 Unreliable，按需回复为 ReliableImmediate。证据：`ONI_Together/Networking/Components/StructureStateSyncers/StorageStateSyncer.cs:33-45`、`ONI_Together/Networking/Components/StructureStateSyncers/StructureSyncerBase.cs:93-113`、`ONI_Together/Networking/Components/StructureStateSyncers/StructureSyncerBase.cs:144-163`。
- [F][HIGH] GameNetworkingSockets 只保证 reliable message 在同 lane 有序；unreliable message 可能丢失，也可能相对其他 unreliable 或 reliable message 乱序。[官方接口说明](https://github.com/ValveSoftware/GameNetworkingSockets/blob/f4525e39ee10f6b45181fd92d01fee75f7d71756/include/steam/isteamnetworkingsockets.h#L344-L349)
- [F][HIGH] 证据状态：字段、send mode 与无条件 apply 为 code-proven；真实 Steam 乱序触发为 trigger-dependent；当前日志中的 storage NRE 为 observed，但不等价于已观察到 stale replay。

### 触发、后果与测试缺口

- [I][HIGH] 触发条件是网络 jitter/reordering，或较新的按需 reliable response 与更旧的周期 unreliable packet 竞速。
- [I][HIGH] 后果是 GameClock 回拨、已隐藏进度条复活，或 structure/storage 旧快照覆盖新快照；storage 路径还会删除带现有 NetId 的对象并重建新对象，放大 registry 与 pending lifecycle 分叉。
- [F][HIGH] `SyncBarrierTests` 验证 reliable backlog 的有序 flush，并明确让 unreliable 不进入 backlog；没有 N+1 后到 N 的 stale rejection 测试。证据：`ONI_Together/DebugTools/UnitTests/SyncBarrierTests.cs:256-289`。

### 成熟项目机制

- [F][HIGH] OpenTTD 的命令按 frame 排队；若收到需要在过去执行的命令，会直接认定已 desync。[上游源码](https://github.com/OpenTTD/OpenTTD/blob/eb62295cc08fe791b6de0061467a4c827da5fa05/src/network/network_command.cpp#L253-L275)
- [F][HIGH] RimWorld Multiplayer 在指定 tick 执行 scheduled commands，并在实现中注明该队列用于避免乱序执行。[上游源码](https://github.com/rwmt/Multiplayer/blob/4a3be276bbf90cc597abfa5b299935ca8eeeb285/Source/Client/Patches/TickPatch.cs#L168-L203)

### 最小验证与根因修复方向

- [I][HIGH] 最小 probe：对 WorldCycle、WorkableProgress 和 Structure/storage 分别注入 N+1 后再注入 N，验证当前实现回写旧值；对 storage 同时比较应用前后 item NetId；再用 Steam jitter 条件复现。
- [I][HIGH] 根因修复方向：为每个权威 domain 使用单调 host revision/sim tick；receiver 丢弃 `revision <= lastApplied`；周期发送与按需响应共享同一 revision 边界。

## P1-2：storage 的 pending Delivered 不会被后到 PickedUp 取消

### 结论

- [F][HIGH] `StorageItemPacket` 在 item 或 storage 未注册时只缓存 `Delivered`；后到的 `PickedUp` 在同样未注册条件下不会删除该 pending entry。证据：`ONI_Together/Networking/Packets/World/StorageItemPacket.cs:18-42`、`ONI_Together/Networking/Packets/World/StorageItemPacket.cs:98-114`。
- [F][HIGH] 随后 spawn 成功会调用 `StorageItemPacket.TryApplyPending`，把旧 `Delivered` 应用于新对象。证据：`ONI_Together/Networking/Packets/World/SpawnPrefabPacket.cs:140-152`、`ONI_Together/Networking/Packets/World/StorageItemPacket.cs:32-42`。
- [F][HIGH] 给定顺序 `Delivered -> PickedUp -> Spawn` 时，最终 storage 状态违反最新 PickedUp 意图；该链路为 code-proven。真实 soak 是否命中过该顺序为 trigger-dependent，当前没有对应 observed 事件标记。

### 测试缺口、最小验证与根因修复方向

- [F][HIGH] 现有 pending pickup 使用 tombstone-like set 并能在 spawn 时消费；storage pending 使用单条 Delivered 字典，没有按 NetId 的有序 transfer state。证据：`ONI_Together/Networking/Packets/World/GroundItemPickedUpPacket.cs:19-34`、`ONI_Together/Networking/Packets/World/StorageItemPacket.cs:18-42`。
- [I][HIGH] 最小 probe：在目标未注册时依次 dispatch Delivered、PickedUp、Spawn，断言 spawn 后 item 不在 storage；再反转前两包验证 latest-wins。
- [I][HIGH] 根因修复方向：为 item lifecycle/transfer 使用同一 host revision journal；pending 保存每个 NetId 的最新权威意图，任何更高 revision 的 PickedUp/Despawn 都覆盖旧 Delivered。

## P1-3：启用模组指纹只覆盖元数据，运行代码差异仍可通过握手

### 结论

- [F][HIGH] 启用模组 fingerprint 只拼接 static ID、distribution platform、label ID、label version，再比较集合。证据：`ONI_Together/Misc/World/SaveHelper.cs:265-313`。
- [F][HIGH] ONI Together 自身 assembly 另有精确 SHA-256 protocol hash；该覆盖没有扩展到其他启用模组的 DLL、资源、配置或 Harmony patch set。证据：`ONI_Together/Networking/ProtocolCompatibility.cs:106-119`、`ONI_Together/Networking/GameClient.cs:268-285`。
- [I][HIGH] 两端模组标签与版本相同、实际执行 DLL 不同时，当前握手仍会接受会话；本地模拟随后可走不同 patch 路径。
- [F][HIGH] `ProtocolSafetyTests` 只验证元数据字段拼接无歧义，没有“同元数据、不同文件内容必须拒绝”的测试。证据：`ONI_Together/DebugTools/UnitTests/ProtocolSafetyTests.cs:399-406`。

### 成熟项目机制

- [F][HIGH] Factorio 官方文档说明多人加入时会计算并比较 mod checksum，以确认双方使用完全相同的 mod 与文件。[官方文档](https://wiki.factorio.com/Multiplayer)

### 最小验证与根因修复方向

- [I][HIGH] 最小 probe：保持某启用模组的 ID 与 version 不变，只改 DLL 一个 byte；记录当前 handshake 是否继续进入快照载入。
- [I][HIGH] 根因修复方向：生成每个启用模组实际执行 assembly、确定性资源与配置/patch-set 的 canonical SHA-256 manifest，并在传输快照前精确比较。

## P1-4：Mod API wrapper 丢失 authority marker，Ready client 可触发 host handler

### 结论

- [F][HIGH] 外部 Mod API packet 会被动态包装为 `ModApiPacket<T>`；wrapper 只实现 `IPacket`、`IModApiPacket`、`IPacketSkipsRegistration`，没有 `IHostOnlyPacket` 或 `IClientRelayable`。证据：`ONI_Together/Networking/Packets/Architecture/PacketRegistry.cs:64-77`、`ONI_Together/Networking/Packets/Core/ModApiPacket.cs:14-20`。
- [F][HIGH] host 的统一入口对 Ready client 先做会话校验；若 packet 没有 `IClientRelayable`，随后直接返回允许。wrapper 的 `OnDispatched` 会反射调用外部 packet handler。证据：`ONI_Together/Networking/Packets/Architecture/PacketHandler.cs:197-225`、`ONI_Together/Networking/Packets/Core/ModApiPacket.cs:39-57`。
- [F][HIGH] 因此通过 Ready 校验的 client 可直接发送已注册 Mod API wrapper 并在 host 执行 handler；该 authority bypass 为 code-proven。具体第三方 handler 的副作用取决于已安装模组，属于 trigger-dependent；当前 soak 没有 observed 恶意包。

### 成熟项目机制、最小验证与根因修复方向

- [F][HIGH] OpenTTD 在接收 client command 时先验证 command enum 与 server-only/offline-only 规则，再进入执行队列。[上游源码](https://github.com/OpenTTD/OpenTTD/blob/eb62295cc08fe791b6de0061467a4c827da5fa05/src/network/network_command.cpp#L374-L379)
- [I][HIGH] 最小 probe：注册一个带 host-side counter 的 Mod API packet，由 Ready client 直接发送 wrapper；断言当前 counter 会增加，并确认普通 `IHostOnlyPacket` 对照组被拒绝。
- [I][HIGH] 根因修复方向：注册时把显式 authority policy 固化到 wrapper type/registry metadata；统一入口依据 registry policy 判定来源，禁止从缺少 marker 推导“允许 client”。

## P1 待 probe：Steam 背压时，权威增量可能被静默丢弃

### 结论

- [F][HIGH] Steam sender 仅在 API 返回 `OK` 时报告成功。证据：`ONI_Together/Networking/Transport/Steamworks/SteamworksPacketSender.cs:29-46`。
- [F][HIGH] 广播 helper 调用 `SendToConnection` 后忽略布尔返回值；`WorldUpdateBatcher` 发送 reliable world update 后无条件清空 pending updates。证据：`ONI_Together/Networking/Packets/Architecture/PacketSender.cs:323-332`、`ONI_Together/Misc/World/WorldUpdateBatcher.cs:90-120`。
- [F][HIGH] 通用 reliable bulk flush 同样忽略 `SendToConnection` 返回值，随后清空 pending list、byte total 和 packet type entry；其中可包含 storage transfer 等权威事件。证据：`ONI_Together/Networking/Packets/Architecture/PacketSender.cs:111-143`、`ONI_Together/Networking/Packets/World/StorageItemPacket.cs:16-30`。
- [F][HIGH] GameNetworkingSockets 官方接口允许 NoDelay 消息返回 `Ignored`，也允许因排队数据过多返回 `LimitExceeded`。[官方接口说明](https://github.com/ValveSoftware/GameNetworkingSockets/blob/f4525e39ee10f6b45181fd92d01fee75f7d71756/include/steam/isteamnetworkingsockets.h#L247-L268)
- [I][MED] 因此发送失败时，当前 world batcher 与 reliable bulk queue 都可能把尚未被 transport 接受的权威 delta/event 当作已完成并清除。清批分支为 code-proven；真实 Steam queue saturation 为 trigger-dependent；现有局域网双机 soak 没有 observed saturation。

### 测试缺口、外部机制与最小验证

- [F][HIGH] OpenTTD 为每个 client 维护 outgoing command queue，并按 client/tick 施加命令速率限制。[队列](https://github.com/OpenTTD/OpenTTD/blob/eb62295cc08fe791b6de0061467a4c827da5fa05/src/network/network_command.cpp#L301-L318) [限流](https://github.com/OpenTTD/OpenTTD/blob/eb62295cc08fe791b6de0061467a4c827da5fa05/src/network/network_command.cpp#L326-L354)
- [I][MED] 最小 probe：让 fake transport 对 WorldUpdate 与 `BulkSenderPacket` 返回 `false`，分别断言 pending delta 和 inner events 仍保留；再用三客户端制造 Steam reliable queue 压力，同时记录 pending bytes、queue time、grid 与 registry hash。
- [I][MED] 根因修复方向：把 transport acceptance/ack 纳入每客户端复制状态；按 revision 保留或合并权威 delta，直到接受/确认；超过明确上限时终止会话并要求新快照，禁止静默清除。

## P1-5：多客户端 relay 缺少 authoritative echo，可形成不同最终顺序

### 结论

- [F][HIGH] 本地 `SetSpeed`/`TogglePause` 已经生效后，Postfix 才调用 `SendToAllOtherPeers` 发出 client-relayable `SpeedChangePacket`。证据：`ONI_Together/Patches/World/SpeedControlPatch.cs:15-29`、`ONI_Together/Patches/World/SpeedControlPatch.cs:37-54`、`ONI_Together/Networking/Packets/World/SpeedChangePacket.cs:11-30`。
- [F][HIGH] host 收到 wrapper 后先 dispatch inner packet，再 fan-out；fan-out 明确排除 host 与原 sender。原 sender 不会收到 host 排序后的 authoritative echo。证据：`ONI_Together/Networking/Packets/Core/HostBroadcastPacket.cs:114-141`。
- [F][HIGH] code-proven schedule：A 本地设 speed1，B 本地设 speed2；host 依次处理 A、B，最终 speed2；host 发给 B 的 A 包在 B 本地操作后到达，使 B 最终 speed1；A 收到 B 包后为 speed2。三端最终状态不同。
- [F][HIGH] `SpeedChangePacket` 没有 host revision 或周期权威快照；receiver 直接应用值。证据：`ONI_Together/Networking/Packets/World/SpeedChangePacket.cs:33-81`。
- [F][HIGH] 同类 RedAlert click 也在本地状态改变后发送 relay request。证据：`ONI_Together/Patches/World/MeterScreenPatches.cs:12-29`。该路径是否形成相同最终分叉仍属 trigger-dependent，本报告不把 Build 泛化进该结论。
- [F][HIGH] 现有真实 soak 只有一个远端 client，没有 observed 三端竞态；多客户端分叉链为 code-proven。

### 成熟项目机制与最小验证

- [F][HIGH] OpenTTD 把 client command 放进 server-ordered frame queue；RimWorld Multiplayer 按指定 tick 从统一队列执行命令，避免不同 peer 采用各自到达顺序。[OpenTTD](https://github.com/OpenTTD/OpenTTD/blob/eb62295cc08fe791b6de0061467a4c827da5fa05/src/network/network_command.cpp#L253-L275) [RimWorld Multiplayer](https://github.com/rwmt/Multiplayer/blob/4a3be276bbf90cc597abfa5b299935ca8eeeb285/Source/Client/Patches/TickPatch.cs#L168-L203)
- [I][HIGH] 最小 probe：host、A、B 三端依次执行上述 schedule，人工延迟 host→B 的 A 包；同时记录 host sequence 与三端最终 speed。
- [I][HIGH] 根因修复方向：client 只提交 request；host 分配单调 command revision/tick 后向所有 peer 回显，包括原 sender；各端只按 host 顺序应用。

## P2：当前 Soak hash 看不到 rocket lifecycle 与多世界归属

### 结论

- [F][HIGH] grid hash 只覆盖全局 cell 的 element、mass、temperature、disease；registry hash 只覆盖 NetId、prefab hash、active。证据：`ONI_Together/DebugTools/SoakStateHash.cs:11-34`、`ONI_Together/DebugTools/SoakStateHash.cs:80-115`、`ONI_Together/DebugTools/SoakStateHash.cs:123-153`。
- [F][HIGH] registry hash 没有 position、world id、storage membership、rocket/cluster location 或 lifecycle revision；相同 NetId/prefab/active 位于不同世界或轨道位置时仍可得到相同 registry hash。该覆盖盲区为 code-proven。
- [I][MED] 因此当前 23 点 soak 只能确认两个已覆盖 domain 仍分叉，不能把 rocket lifecycle、多世界归属或 storage membership 判为一致。现有双机 run 没有 observed DLC rocket 场景。

### 最小验证与根因修复方向

- [I][HIGH] 最小 probe：建立含两世界与一艘 rocket 的固定场景，在 client-only probe 中改变 rocket world/location、一个 entity world/position 和 storage membership；验证现有 hash 不变，再确认扩展 domain hash 能检出。
- [I][HIGH] 根因修复方向：按权威 domain 分开记录 grid、entity lifecycle、world membership、storage membership 与 cluster/rocket state hash；每个 hash 携带 generation/tick，禁止用单一 registry 三字段摘要替代 DLC 验收。

## 已覆盖项与本轮不改结论

- [F][HIGH] packet envelope 已覆盖 16 MiB 上限、未知类型、反序列化异常和 trailing bytes 拒绝。证据：`ONI_Together/Networking/Packets/Architecture/PacketHandler.cs:101-169`。
- [F][HIGH] host-only 来源、connection generation、session epoch 和 protocol-ready gate 已在统一入口校验。证据：`ONI_Together/Networking/Packets/Architecture/PacketHandler.cs:197-239`。
- [F][HIGH] chunk reassembly 已按 sender+generation 隔离，并覆盖尺寸/分块上限、重复完成序列、timeout 和内存预算。证据：`ONI_Together/Networking/Packets/Core/ChunkedPacket.cs:10-15`、`ONI_Together/Networking/Packets/Core/ChunkedPacket.cs:38-51`、`ONI_Together/Networking/Packets/Core/ChunkedPacket.cs:83-170`、`ONI_Together/Networking/Packets/Core/ChunkedPacket.cs:193-227`。
- [F][HIGH] Ready barrier 已要求 loading proof，先 flush reliable backlog 再 ACK；flush 失败会终止连接。证据：`ONI_Together/Networking/ReadyManager.cs:177-226`、`ONI_Together/Networking/ReadyManager.cs:234-281`。
- [F][HIGH] barrier 仅在当前 session 持有 pause 时解除暂停；reliable backlog 有 packet/byte cap，按入队顺序 flush，overflow 会断开连接。证据：`ONI_Together/Networking/ReadyManager.cs:413-428`、`ONI_Together/Networking/ReliableSyncBacklog.cs:17-18`、`ONI_Together/Networking/ReliableSyncBacklog.cs:34-97`、`ONI_Together/Networking/Packets/Architecture/PacketSender.cs:286-297`。
- [F][HIGH] reconnect 的 stale token 会切换 FreshSnapshot，存档载入会推进 generation。证据：`ONI_Together/Networking/Packets/Handshake/GameStateRequestPacket.cs:251-260`、`ONI_Together/Misc/World/SaveHelper.cs:45-86`。
- [F][HIGH] DLC 集合与 active mod 元数据已有加入校验；本轮没有在已审 packet path 中确认独立的“world id 丢失”同步缺陷，但当前 Soak hash 也无法验收该 domain。证据：`ONI_Together/Networking/GameClient.cs:238-285`、`ONI_Together/DebugTools/SoakStateHash.cs:21-26`。
- [I][MED] 多客户端 Ready token 的单元测试覆盖了连接隔离，但现有真实 soak 只有一个远端客户端；因此多客户端运行稳定性仍属未验证，不单列为代码缺陷。证据：`ONI_Together/DebugTools/UnitTests/SyncBarrierTests.cs:198-253`。

## 最短验证顺序

1. [I][HIGH] 先封闭 P0 网格边界：paused Ready 全字段基线、temperature/disease/5g/replace-type 注入，并以 generation/tick domain hash 验收。
2. [I][HIGH] 再封闭 P0 实体边界：双候选、NetId 碰撞、despawn-before-spawn、`Delivered -> PickedUp -> Spawn`、30 秒 expiry；以 host lifecycle/transfer journal 验收。
3. [I][HIGH] 最后统一做协议 fault-injection：N+1/N 乱序、transport/bulk `false`、Mod API client dispatch、同版本异 DLL、三端 relay 并发排序和 DLC domain hash；固化 revision、retention、authority、manifest 与覆盖回归测试。

## 一手来源固定点

- [F][HIGH] OpenTTD 源码固定在提交 `eb62295cc08fe791b6de0061467a4c827da5fa05`。
- [F][HIGH] RimWorld Multiplayer 源码固定在提交 `4a3be276bbf90cc597abfa5b299935ca8eeeb285`。
- [F][HIGH] Mindustry 源码固定在提交 `a39660ec88961476c110e9e6073ef6b041beea9b`。
- [F][HIGH] GameNetworkingSockets 接口定义固定在提交 `f4525e39ee10f6b45181fd92d01fee75f7d71756`。
- [F][HIGH] Factorio 使用官方 wiki 的固定历史版本 `oldid=217941`。

[RULES I BROKE]: none.
