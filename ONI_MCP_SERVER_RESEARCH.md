# ONI MCP Server 调试接入记录

更新日期：2026-07-18

## 结论

- [F][HIGH] 两台测试机均已安装 `ONI MCP Server` `0.2.1`，本地 `OniMcp.dll` SHA-256 都是 `c328a9dfebc431eef98561eca0d93947bb156b0ef243115091531837f592c260`。
- [F][HIGH] 调试阶段两端服务均绑定 `localhost:8788`，`AuthEnabled=false`；端口没有直接暴露到局域网或公网。原生验收阶段已禁用该 Mod，两端当前均无 `8788` 监听。
- [F][HIGH] 运行时完成了 MCP initialize、工具列表、资源列表和活动世界只读检查；两端分别返回 7 个工具、126 个资源。
- [I][HIGH] MCP 适合承担场景复现和观察，不承担 ONI Together 的同步正确性判定。最终证据仍是 generation、tick barrier、ACK、domain hash 与 packet revision。
- [I][HIGH] 调试时只允许宿主执行写操作，客户端只读。向两端分别执行同一操作会绕过“客户端请求 → 宿主权威修改 → 状态下发”的待验链路。
- [I][HIGH] 完成 MCP 辅助排错后，必须禁用 MCP，再跑一次原生双机 37,800 tick soak，排除额外 Harmony patch 和帧耗时的影响。
- [F][HIGH] MCP-enabled 双机 soak 已完成 21 段、37,800 tick；21 次 post-keyframe 五域 hash 全部一致，keyframe apply failure 与生命周期 membership 异常均为 0。
- [F][HIGH] MCP-disabled 原生双机复测也已完成 21 段、37,800 tick；21 次 post-keyframe 五域 hash 全部一致，最终 `postMismatchSeen=False`、`keyframeApplyFailureSeen=False`、`postKeyframeEqual=True`。

## 一手来源

- [F][HIGH] [官方源码与说明](https://github.com/LIghtJUNction/OniMods/tree/main/mods/OniMcp)将它定义为进程内本地 MCP 服务，默认端点为 `http://localhost:8788/mcp/`。
- [F][HIGH] [0.2.1 release](https://github.com/LIghtJUNction/OniMods/releases/tag/v0.2.1)仍标记为 pre-release。
- [F][HIGH] [Steam Workshop 项目](https://steamcommunity.com/sharedfiles/filedetails/?id=3731864673)是社区安装入口。
- [F][HIGH] 上游明确说明 1.0.0 之前工具名、参数和响应结构可能发生不兼容变化，客户端应固定版本并以运行时 manifest 为准。

## 当前安装与安全边界

| 项目 | 两端实测 |
|---|---|
| Mod metadata version | `0.2.1` |
| Mod static ID | `LIghtJUNction.OniMcp` |
| DLL SHA-256 | `c328a9dfebc431eef98561eca0d93947bb156b0ef243115091531837f592c260` |
| 调试时监听地址 | `localhost` |
| 调试时端口 | `8788` |
| 鉴权 | 关闭，仅限回环访问 |
| MCP protocol | `2025-11-25` |
| 工具 / 资源 | `7 / 126` |
| 当前运行状态 | 已禁用，无监听 |

- [F][HIGH] 本机配置文件位于 `~/Library/Application Support/unity.Klei.Oxygen Not Included/OniMcpConfig.json`。
- [F][HIGH] 本次没有把未鉴权端口绑定到 `0.0.0.0`，也没有把 token 写入仓库。
- [I][HIGH] 若以后需要跨主机直连，应先启用强随机 token，再限制到可信网络；当前调试继续使用两端各自的回环接口或 SSH tunnel。

## 实测能力

- [F][HIGH] `world_editor` 可读取虚拟文件化的殖民地、地图、复制人、建筑、管理、诊断和基础设施状态。
- [F][HIGH] `game_control` 可控制暂停、速度和状态管理。
- [F][HIGH] `navigation_control` 可控制视图、覆盖层和截图。
- [F][HIGH] `building_control` 与 `orders_control` 可执行建筑规划、挖掘、清扫、拖地和拆除等操作。
- [F][HIGH] `server_control` 可读取服务状态、日志和截图任务。
- [F][HIGH] 双端 `/active/index.md?format=json` 在 soak 启动前给出相同周期进度、暂停状态、活动世界与复制人数。

## 推荐调试闭环

1. [I][HIGH] 先暂停宿主，从两端读取相同的低成本状态摘要。
2. [I][HIGH] 只在宿主执行一个可复现操作，或从客户端经 ONI Together 正常 UI 发起请求。
3. [I][HIGH] 短暂运行固定 tick，再暂停。
4. [I][HIGH] MCP 读取两端可解释状态；ONI Together 同时采集 raw 与 post-keyframe 五域 hash。
5. [I][HIGH] 若出现分歧，以首个 generation/tick、packet revision、ACK 和 lifecycle journal 为因果锚点；MCP 快照只用于缩小场景。

```text
宿主 MCP 写操作 ──> 宿主权威世界 ──> ONI Together packet ──> 客户端
       │                    │                                 │
       └──────── MCP 读 ────┴──────── domain hash ───────────┘
```

## 已知限制与当前异常

- [F][HIGH] MCP server 的 initialize 响应报告 `0.2.0`，但已安装 metadata 是 `0.2.1`；调试记录以 DLL hash 和 metadata 双重固定，不依赖该响应字符串。
- [F][HIGH] `oni://colony/status` 在当前构建会请求未注册的 `colony_control`；活动世界只读检查改用 `world_editor` 或 `/active/index.md?format=json`。
- [F][HIGH] benchmark 工具清单检查会报告 `ToolInfos count changed unexpectedly: 11 vs 12`；运行时 `tools/list` 的 7 个聚合工具仍可用。
- [I][HIGH] MCP 的异步读取没有 ONI Together 的 generation、tick fence、ordered reliable ACK 或 revision 语义，因此不能证明两个世界因果一致。
- [I][MED] 额外 Mod 会改变 Harmony patch 集、帧耗时和启用 Mod fingerprint；MCP-enabled soak 只能作为调试回归，不是最终发布验收。

## 完成判定

- [F][HIGH] MCP-enabled 阶段已完成：两端版本与 DLL hash 相同、只读 smoke 通过、完整 21 段 / 37,800 tick soak 的 post-keyframe 五域比较全部一致且没有断线。
- [F][HIGH] Debug 模式在验收窗口抑制后台权威修复，因此 raw 分歧用于暴露 drift，不作为失败条件；每段 keyframe、ACK、延迟 membership 校验与 post-keyframe 五域一致才构成收敛证据。
- [F][HIGH] 原生阶段已完成：禁用 MCP 后重新握手、重新载入权威快照，并跑满相同 21 段 / 37,800 tick soak。
- [F][HIGH] 原生完成汇总记录 `rawMismatchSeen=True`、`firstRawMismatchSample=1`、`postMismatchSeen=False`、`keyframeApplyFailureSeen=False`、`postKeyframeEqual=True`。raw drift 经每段宿主 keyframe 与 ACK 收敛，没有 post-keyframe 分歧。
- [F][HIGH] 两端 Riptide 回环测试通过；相同 Release DLL 已在两端原生启动到主菜单，ONI Together 未记录 error/exception，`8788` 仍无监听。本轮双机同步验收已关闭。
