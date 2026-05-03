# gh-bridge

**AI Agent 驱动的 Grasshopper 桥接插件 — 让大语言模型直接在 GH 画布上生成、连线、求解电池组。**

gh-bridge 是一个 Grasshopper GHA 插件，在 GH 画布上嵌入一个 HTTP 服务端。AI Agent 通过 18 个 RESTful 指令动态创建组件、连线、布位、触发求解，实现从自然语言到参数化模型的全自动链路。

配合 Rhino.Inside.Revit（RIR），可将生成的几何直接推入 Revit 文档。

## 架构

```
┌─────────────┐     HTTP POST     ┌────────────────┤     GH API     ┌──────────────┐
│  AI Agent   │ ────────────────→ │  gh-bridge GHA ├──────────────→ │  Grasshopper │
│ (Hanako 等) │ ←───────────────  │ (C#, GHA 插件) │ ←────────────── │  画布 + RIR  │
└─────────────┘    JSON 响应       └────────────────┘                └──────┬───────┘
                                                                           │ RIR
                                                                     ┌─────▼──────┐
                                                                     │ Revit 文档  │
                                                                     │ (DirectShape)│
                                                                     └────────────┘
```

所有操作走 GH 电池组，没有直推 API。组件在画布上可视化呈现，不黑箱。

## 指令一览

| 指令 | 用途 |
|------|------|
| `ping` | 心跳检测 |
| `scan` | 扫描 GH 组件代理 |
| `builddb` | 建立完整组件数据库 |
| `qdb` | 按关键词搜索组件 |
| `inspect` | 查看组件的输入输出接口 |
| `verify` | 预检组件+连线计划（build 前必调） |
| `build` | 创建组件、连线、布位、求解 |
| `wire` | 分步连线（用完整 InstanceGuid） |
| `bake` | 烘焙 GH 输出到 Rhino 文档 |
| `clear` | 清空画布（保留桥自身） |
| `scene` | 三边状态总览（Rhino/GH/Revit） |
| `wires` | 检查画布连线状态 |
| `diag` | 诊断组件运行时错误 |
| `check` | 检测 RIR 连接 + Revit 元素数 |
| `get_levels` | 查询 Revit 标高 |
| `get_families` | 查询 Revit 族类型 |
| `get_wall_types` | 查询墙类型 |
| `get_active_view` | 查询当前激活视图 |

## 使用流程

### 标准流程

```
1. scene        查看画布状态
2. clear        如有残留组件
3. builddb      建立组件数据库（会话中一次）
4. qdb          确认所需组件存在
5. verify       预检组件+连线方案
6. build        创建电池组+连线+求解
7. scene        确认结果
```

### 分步构建（推荐大电池组）

```
1. build                   创建首批组件
2. wire (用 InstanceGuid)  加跨组件连线
3. build (standalone)      加新组件
4. wire                    连到已有组件
```

### 推入 Revit

在 build 中包含 `AddDirectShapeBrep`（GUID: `5ade9ae3`），将 Brep 输出连到其输入。

## build 指令格式

```json
{
  "action": "build",
  "components": [
    {"id": "s1", "guid": "57da07bd", "val": 5000, "nick": "Radius"},
    {"id": "quad", "guid": "361790d6"}
  ],
  "wires": [["s1", 0, "quad", 1]],
  "positions": {"s1": [0, 0], "quad": [250, -100]}
}
```

- `id`: 组件标识符，供 wires/positions 引用
- `guid`: GH 组件的 8 位 GUID 前缀
- `val`: Number Slider 数值（可选）
- `nick`: 昵称（可选）
- `wires`: `[来源id, 输出索引, 目标id, 输入索引]`
- `positions`: `{id: [x, y]}` 像素坐标

## 开发

### 环境要求

- Visual Studio 2022 或 `dotnet build`
- .NET Framework 4.8
- Rhino 6/7 + Grasshopper
- Rhino.Inside.Revit（用于 Revit 推送）

### 编译

```bash
cd gh-bridge
dotnet build -c Release
```

编译结果自动复制到 `deploy/` 目录。

### 部署

```bash
# 替换 GH 组件库中的 .gha
cp deploy/HanakoBridge.gha ~/AppData/Roaming/Grasshopper/Libraries/
# **替换前必须关闭 Rhino/Grasshopper**
```

### .gitignore

仓库排除：`bin/`、`obj/`、`backup*/`、`port.txt`、`.vs/`。

## 已知坑点

- **幽灵端口**：崩溃后 PID 4 会捕获端口，重启 Revit 释放
- **求解卡死**：某些组件（PipeSurface 等）求解报错会导致循环死锁，需重启 Revit
- **build 不清理旧组件**：每次 build 前确保画布干净
- **build 的 wires 与 wire 指令不通用**：前者用组件 id，后者用完整 InstanceGuid
- **RIR AddFloor 事务不可靠**：可能计算出数据但不提交
