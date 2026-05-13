# gh-bridge — Agent 工作指南

> 最后更新：2026-05-13

## 项目位置

```
D:/agents/-A-hanako/gh-bridge/
├── HanakoBridgeComponent.cs   ← 主逻辑（单文件，~1400 行）
├── HanakoBridge.csproj        ← 项目文件
├── deploy/                    ← 编译产物（.gha / .dll）
├── README.md                  ← 外部文档
└── AGENTS.md                  ← 本文档
```

GitHub: `github.com/a01063024347-cpu/gh-bridge`

## 端口发现

每次会话：**先读 D:/agents/-A-hanako/gh-bridge/port.txt**。端口范围 18080-18100，动态分配。

## 核心特性（2026-05-13）

| 特性 | 说明 |
|------|------|
| 名称→GUID | build 的 guid 字段支持组件名，桥端 CompDB 三级回退解析 |
| 端口名匹配 | wire 支持端口名（如 "R"），大小写不敏感+模糊匹配 |
| 自动布局 | build 不设 positions 时按连线拓扑深度自动排列 |
| 异步模式 | `"wait":false` 跳过解算等待，ping 轮询获取结果 |
| 箭头连线 | `"r.Number→cir.R"` 语法糖，替代数组格式 |
| InstanceGuid 连线 | wire/build 支持 36 字符完整 GUID 定位画布已有组件 |
| 分批建造 | 每批 5~6 组件+cycle，绕开 RIR 多级数据树求解瓶颈 |
| full cycle | `cycle full=true` 标记全文档 dirty 重算，穿透 20+ 级数据树 |
| 文件解析 | `readfile` 解析二进制 .gh 文件，提取组件+GUID+连线拓扑 |
| 组件搜索 | `proxies` 搜索 ComponentServer 全量代理 |
| 远端调参 | `setval` 远程修改 Number Slider 值 |
| 视口修复 | `redraw` 强制刷新 Rhino 视口 |
| 原生截图 | screenshot 改用 `-_ViewCaptureToFile` 避免 RIR 白屏 |
| canvas | 画布完整快照 |
| explain | 连线关系自然语言解释 |
| describe | Bake 后分析 Rhino 对象：尺寸/形状/渐变 |

## 指令集（30+）

`ping` `cancel` `scene` `clear` `scan` `builddb` `qdb` `inspect` `verify` `wires` `canvas` `explain` `gettype`
`build` `wire` `set` `setval` `cycle`
`diag` `diagnose` `geomcheck` `describe`
`bake` `screenshot` `query` `loadgh` `readfile`
`check` `get_levels` `get_families` `get_wall_types` `get_active_view`
`proxies` `redraw` `remove` `disconnect` `move` `tidy`

## build 指令

```json
{
  "action": "build",
  "wait": false,
  "components": [
    {"id": "r", "guid": "Number Slider", "val": 5, "name": "半径"},
    {"id": "cir", "guid": "Circle"}
  ],
  "wires": [
    "r.Number->cir.R"
  ]
}
```

- `guid`: 支持 GUID 或组件名
- `wait`: false = 异步（推荐）
- `wires`: 支持箭头语法 `"id.port->id.port"` 或传统数组 `["id",port,"id",port]`
- `positions`: 可选，不设自动拓扑布局

## 工作流

```
canvas → build(wait:false) → ping(轮询拿GUID) → cancel → diag → scene
```

## 已知限制

- 清空含 RIR 组件的画布会触发 Revit 弹窗
- PipeSurface 在 RIR 中不产 Brep（用 Extrude 或 MeshToPolysurface 替代）
- MeshPipe 无法程序化新建（需用户拖入或 .gh 文件获取）
- 跨 session 后 _idMap 失效，wire/build 跨批次必须用完整 InstanceGuid（36字符）
- 求解卡死时唯一解法：重启 Revit
- 分批建造：RIR 中超过 10 组件单批建会卡求解，必须分 5~6 组件一批

## 崩溃恢复

桥失联 → `taskkill /F /IM Revit.exe` → 重开 Revit → 读 port.txt。

## 路径

所有文件实际位于 `D:/agents/-A-hanako/gh-bridge/`。
