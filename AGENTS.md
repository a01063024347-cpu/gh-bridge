# gh-bridge — Agent 工作指南

> 最后更新：2026-05-12

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

## 核心特性（2026-05-12）

| 特性 | 说明 |
|------|------|
| 名称→GUID | build 的 guid 字段支持组件名，桥端 CompDB 三级回退解析 |
| 端口名匹配 | wire 支持端口名（如 "R"），大小写不敏感+模糊匹配 |
| 自动布局 | build 不设 positions 时按连线拓扑深度自动排列 |
| 异步模式 | `"wait":false` 跳过解算等待，ping 轮询获取结果 |
| 双求解 | build 后自动触发第二轮求解保证数据过线 |
| canvas | 画布完整快照 |
| explain | 连线关系自然语言解释 |
| describe | Bake 后分析 Rhino 对象：尺寸/形状/渐变 |
| screenshot | 截取 Rhino 视图 PNG |

## 指令集（25+）

`ping` `cancel` `scene` `clear` `scan` `builddb` `qdb` `inspect` `verify` `wires` `canvas` `explain` `gettype`
`build` `wire` `set`
`diag` `diagnose` `geomcheck` `describe`
`bake` `screenshot` `query` `loadgh`
`check` `get_levels` `get_families` `get_wall_types` `get_active_view`

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
    ["r", "Number", "cir", "R"]
  ]
}
```

- `guid`: 支持 GUID 或组件名
- `wait`: false = 异步（推荐）
- `wires[1]` / `wires[3]`: 支持端口号或端口名
- `positions`: 可选，不设自动拓扑布局

## 工作流

```
canvas → build(wait:false) → ping(轮询拿GUID) → cancel → diag → scene
```

## 已知限制

- 清空含 RIR 组件的画布会触发 Revit 弹窗
- 4Point Surface 四点共面生成退化面
- wire 指令用 InstanceGuid（36字符），不是 build 返回的 id
- 求解卡死时唯一解法：重启 Revit

## 崩溃恢复

桥失联 → `taskkill /F /IM Revit.exe` → 重开 Revit → 读 port.txt。

## 路径

所有文件实际位于 `D:/agents/-A-hanako/gh-bridge/`。
