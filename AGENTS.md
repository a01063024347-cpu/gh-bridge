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
| 43037154 | ConstrainedAreaRectangle | 替代已废弃的 Rectangle（0ca0a214） |
| 2b2a4145 | InterpCurve (IntCrv) | 需要 Point 列表输入，P=true 闭合 |
| d2cedf38 | MeshColour (MCol) | 网格上色，输入 M(Mesh)+C(Colour) |
| 46b5564d | Ellipse | 输出 Curve |
| 4beead95 | Brep Closest Point | P(Point)+B(Brep) → P,N,D |
| 6ec39468 | Amplitude | V(Vector)+A(Number) → V |
| 99bee19d | Tree Statistics | T(Data) → P(Path),L,C |
| d8332545 | Dispatch | L+P(Boolean) → A,B |
| 3a710c1e | Tree Branch | T+P(Path) → B |
| 41aa4112 | Flip Matrix | D → D |
| PipeSurface（`c277f778`） | Pipe | C(Curve)+R(Number) → B(Brep) **必须显式连半径，默认0→退化** |
| 0bfbda45 | AddDirectShapeGeometry | 推 Revit，输入 G(Geometry)+C(Category) |
| f80cfe18 | Flatten Tree | 压平数据树 → 1D列表 |
| e64c5fb1 | Series | S(Start)+N(Step)+C(Count) → 序列索引 |
| 59daf374 | List Item | L(列表)+i(索引) → 单项提取 |

## Branch/Dispatch 已知 GH API 限制

Branch.P 接受 Path 类型，Dispatch.A 输出 Generic Data。原版画布上手动画接 Dispatch.A → Branch.P 时 GH UI 自动做隐式类型转换，但 build/wire 指令碰不到这个转换通道。

**替代路由方案**：
1. `TStat.P → Branch.P`：Path→Path 纯类型匹配，但提取全部路径非奇偶分流子集
2. **索引法（推荐）**：`Flatten + Series + List Item` 完全替代 Branch/Dispatch/TStat/Flip 链：
   ```
   Divide.P → Flatten → List Item (i fetched by Series output)
   ```
   螺旋索引 `Series(S, 36, 25)` = 每步跨一层+偏一角度 = 编织螺旋

## 会导致求解卡死的组件（禁用）

- PipeSurfaceEx（888f9c3c）— 求解报错死锁
- Sweep1（bb6666e7）— 部分场景卡死
- Arc3Pt_OBSOLETE（32c57b97）— OBSOLETE 不稳定

## 指令集（24 个）

`ping` `scan` `builddb` `qdb` `inspect` `verify` `build` `wire` `bake` `clear`
`scene` `wires` `diag` `check` `get_levels` `get_families` `get_wall_types` `get_active_view`
`set` `gettype` `cancel` `diagnose` `geomcheck` `createpanel`

**`diag` 指令**（2026-05-03 新增）：检测画布组件的运行时错误。注意：如果求解卡死，diag 也读不到——需要重启 Revit。

**`set` 指令**（2026-05-09 新增）：设置组件内部参数值。支持 path/bool/int/number/text/domain/bool_list 类型。

**`gettype` 指令**（2026-05-09 新增）：从画布上组件实例 GUID 反向获取类型 GUID。用于查找 .gh 文件内嵌组件。

**`geomcheck` 指令**（2026-05-09 新增）：遍历所有几何组件输出，检测零长度曲线、无效 Brep、零体积管道等退化几何体。GH 认为"有效"但渲染显示 X 的几何体可通过此指令定位。

**`cancel` + `diagnose`**：先 cancel 触发再求解，用 diagnose 检查空输出；用 geomcheck 检查退化几何体。

## 关键工作流

```
scene → clear(如有残留) → builddb → build(分段10-15组件) → cancel(触发再求解) → diagnose(检查)
```

**two-solve 机制**：build 在 SolveInstance 中添加组件，但新组件不在本次求解周期中。每次 build 后必须用 cancel 触发第二次求解才能计算。

**大型电池组**：分段 build，每段 10-15 组件，每段建完 cancel 求解再建下一段。不要一次建 30+ 组件，会导致 GH 超时或崩溃。

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
