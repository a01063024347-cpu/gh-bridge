# gh-bridge

**AI Agent 驱动的 Grasshopper 桥接插件 — 让大语言模型直接在 GH 画布上生成、连线、求解电池组。**

gh-bridge 是一个 Grasshopper GHA 插件，在 GH 画布上嵌入一个 HTTP 服务端。AI Agent 通过 25+ 个 RESTful 指令动态创建组件、连线、布位、触发求解，实现从自然语言到参数化模型的全自动链路。

配合 Rhino.Inside.Revit（RIR），可将生成的几何直接推入 Revit 文档。

> **当前稳定版本**：基于 2026-05-09 备份恢复，所有功能已验证通过。

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

## 核心特性

| 特性 | 说明 | 加入版本 |
|------|------|---------|
| 名称→GUID 翻译 | build 的 guid 字段支持组件名（如 "Number Slider"），桥端自动解析 | 2026-05-12 |
| 端口名匹配 | wire 支持端口名（如 "R"）替代端口号，大小写不敏感+模糊匹配 | 2026-05-12 |
| 自动布局 | build 不设 positions 时按连线拓扑深度自动排列 | 2026-05-12 |
| 异步模式 | build/wire/set 加 `"wait":false` 跳过解算等待，秒返 GUID | 2026-05-12 |
| 双求解 | build 后自动触发第二轮求解保证数据过线 | 2026-05-12 |
| 画布快照 | canvas 指令返回完整组件状态 | 2026-05-12 |
| 逻辑解释 | explain 指令追踪连线关系返回自然语言描述 | 2026-05-12 |
| 几何描述 | describe 指令 bake 后分析 Rhino 对象：尺寸、形状、渐变方向 | 2026-05-12 |
| 视图截图 | screenshot 指令截取 Rhino 视图保存为 PNG | 2026-05-12 |

## 指令一览

### 基础操作
| 指令 | 用途 |
|------|------|
| `ping` | 心跳检测，异步模式下返回上次 build 结果 |
| `cancel` | 取消当前求解 + 触发新求解 |
| `scene` | 三边状态总览（Rhino/GH/Revit 元素数） |
| `clear` | 清空画布（保留桥自身） |

### 组件查询与检查
| 指令 | 用途 |
|------|------|
| `scan` | 扫描 GH 组件代理 |
| `builddb` | 建立完整组件数据库 |
| `qdb` | 按关键词搜索组件 |
| `inspect` | 查看组件输入输出接口（支持名称或 GUID） |
| `verify` | 预检组件+连线计划 |
| `wires` | 检查画布连线状态 |
| `canvas` | 画布完整快照：组件/端口/连线/持久数据 |
| `explain` | 传入 GUID 列表，返回自然语言逻辑链解释 |
| `gettype` | 从实例GUID反查类型GUID |

### 构建与连线
| 指令 | 用途 |
|------|------|
| `build` | 创建组件、连线（支持名称、端口名、自动布局、async） |
| `wire` | 分步连线（支持端口名、用完整 InstanceGuid） |
| `set` | 设置组件内部参数（Domain、Expression、Branch路径等） |

### 诊断与验证
| 指令 | 用途 |
|------|------|
| `diag` | 诊断组件运行时错误 |
| `diagnose` | 诊断退化几何体 |
| `geomcheck` | 遍历几何输出，检测零长度/无效Brep/零体积 |
| `describe` | Bake 到 Rhino，分析对象尺寸、形状、渐变方向 |

### 输出与可视化
| 指令 | 用途 |
|------|------|
| `bake` | 烘焙 GH 输出到 Rhino 文档 |
| `screenshot` | 截取 Rhino 视图存为 PNG |
| `query` | 查询 Rhino 对象列表 |
| `loadgh` | 分析 .gh 文件依赖 |

### Revit 集成
| 指令 | 用途 |
|------|------|
| `check` | 检测 RIR 连接 + Revit 元素数 |
| `get_levels` | 查询 Revit 标高 |
| `get_families` | 查询 Revit 族类型 |
| `get_wall_types` | 查询墙类型 |
| `get_active_view` | 查询当前激活视图 |

## build 指令格式

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

- `guid`: 支持 8 位 GUID 前缀或组件名（如 "Number Slider"、"Circle"）
- `wait`: `false` 跳过解算等待，秒返 GUID（推荐）
- `val`: Number Slider 数值（可选）
- `name`: 组件中文名称（可选）
- `wires`: `[来源id, 输出端口, 目标id, 输入端口]`，端口支持数字或名称
- `positions`: 可选，不设时自动按拓扑布局

## 开发

### 环境要求

- .NET Framework 4.8
- Rhino 7 + Grasshopper + Rhino.Inside.Revit
- Visual Studio 2022 或 `dotnet build`

### 编译与部署

```bash
cd gh-bridge
dotnet build
# 编译结果自动复制到 deploy/

# 部署（需关闭 Rhino/GH）
copy deploy\HanakoBridge.gha %APPDATA%\Grasshopper\Libraries\
copy deploy\HanakoBridge.dll %APPDATA%\Grasshopper\Libraries\
```

### 端口范围

18080-18100，动态分配。端口号写入 `port.txt`。

## 已知限制

- **清空含 RIR 组件的画布会触发 Revit 弹窗**：使用 `revit-dialog-closer.sh` 关闭
- **4Point Surface 四点共面时会生成退化面**：至少两个角的 Z 值设为非零
- **异步模式下 ping 轮询获取 build 结果**
- **wire 指令用 InstanceGuid（36字符），不是 build 返回的 id**

## 项目结构

```
gh-bridge/
├── HanakoBridgeComponent.cs   # 核心源码（单文件）
├── HanakoBridge.csproj        # 项目文件
├── README.md                  # 本文档
├── AGENTS.md                  # Agent 使用指南
├── deploy.bat                 # 部署脚本
├── revit-dialog-closer.sh     # Revit 弹窗关闭工具
└── port.txt                   # 运行时端口号
```
