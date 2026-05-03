# gh-bridge — Agent 工作指南

## 项目位置

```
D:/agents/-A-hanako/gh-bridge/
├── HanakoBridgeComponent.cs   ← 主逻辑（~1200 行，单一文件）
├── HanakoBridge.csproj         ← 项目文件
├── deploy/                     ← 编译产物（.gha / .dll）
├── rir-rules.md                ← RIR 反射规则
└── README.md                   ← 外部文档
```

GitHub: `github.com/a01063024347-cpu/gh-bridge`

## 端口发现

每次会话：**先读 D:/agents/-A-hanako/gh-bridge/port.txt**（桥启动时写入实际端口）。
不要死磕 14880，端口是动态的（14880–14999）。

## 稳定组件白名单（实测不卡死）

| GUID | 组件 | 备注 |
|------|------|------|
| 57da07bd | Number Slider | 设值用 `d.Value = val` |
| 17b7152b | XY Plane | 默认原点 |
| 3581f42a | ConstructPoint | X/Y/Z 输入 |
| 361790d6 | QuadSphere | 输出 Brep |
| 290f418a | ScaleNU | 非均匀缩放 |
| 5ade9ae3 | AddDirectShapeBrep | 推入 Revit |
| 0373008a | Cylinder | 输出 Surface（非 Brep） |
| 2b2a4145 | InterpCurve | 需要 Point 列表输入 |
| 46b5564d | Ellipse | 输出 Curve |

## 会导致求解卡死的组件（禁用）

- PipeSurface（c277f778）— 求解报错死锁
- PipeSurfaceEx（888f9c3c）— 同上
- Sweep1（bb6666e7）— 部分场景卡死
- Arc3Pt_OBSOLETE（32c57b97）— OBSOLETE 不稳定

## 指令集（18 个）

`ping` `scan` `builddb` `qdb` `inspect` `verify` `build` `wire` `bake` `clear`
`scene` `wires` `diag` `check` `get_levels` `get_families` `get_wall_types` `get_active_view`

**`diag` 指令**（2026-05-03 新增）：检测画布组件的运行时错误。注意：如果求解卡死，diag 也读不到——需要重启 Revit。

## 关键工作流

```
scene → clear(如有残留) → builddb → verify → build → wire(分步) → scene(确认)
```

## 崩溃恢复

所有 POST 操作返回 `"queued"` 或超时 → 求解卡死。
**唯一解法**：`taskkill //f //im Revit.exe` → 用户重开 Revit → 等待 RIR 加载 → 读 port.txt 确认新端口。

## build 关键规则

- build 的 wires 用组件 id（`"s1"`）引用**当前 build 的组件**，不能引用之前 build 的
- 跨 step 连线必须用 `wire` 指令 + 完整 InstanceGuid
- build 不清理旧组件，每次前检查画布

## 路径说明

所有 gh-bridge 相关文件实际位于 `D:/agents/-A-hanako/gh-bridge/`。
`D:/-A-hanako/` 是系统绑定的工作目录但文件不在此处。
