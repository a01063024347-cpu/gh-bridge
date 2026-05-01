# Rhino.Inside.Revit 电池组规则

## Add Floor 流程
```
Curve (Rectangle) → Add Floor.Curve
Query Levels → Add Floor.Level (可选)
Floor Type → Add Floor.Type (可选)
```

## Add Wall 流程  
```
Curve → Add Wall.Curve
Level → Add Wall.Level  
Height (Number) → Add Wall.Height
Wall Type → Add Wall.Type (可选)
```

## Add Column 流程
```
Point → Add Column.Location
Level → Add Column.Level
Column Type → Add Column.Type
```

## 通用规则
1. Curve 必须是闭合平面曲线
2. Level 用 Query Levels 获取后取其中一个
3. Type 用 Query Types 获取
4. 所有 Number 参数可直接作为 IGH_Param 源
5. 组件通过 GetProperty("Params") 访问 Input/Output
6. Input/Output 通过 (IList)(dynamic)xxx.Params.Input/Output 获取
7. 连线用 targetInput.AddSource(sourceParam)

## 排查步骤
1. build → check 看组件报错
2. Revit 切换到 3D 视图
3. Revit Rhinoceros 标签 → Show Preview
4. 调滑块参数触发重新求解
