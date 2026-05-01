"""
生成 Grasshopper .ghx 参数化楼板定义
"""

import os
import uuid

# GH 组件的 GUID
SLIDER_GUID     = "8a0a4d70-8e77-4bba-bdff-07fdaf60240e"
PYTHON_GUID     = "c5a2c2d3-5e4f-4a6b-8c7d-9e0f1a2b3c4d"
PANEL_GUID      = "b1ad7c06-0ab1-4857-a2bb-9a759c47a76b"
POINT_GUID      = "67e54eb5-2c7a-4487-9043-39689b7d3f5b"
RECTANGLE_GUID  = "309a0f6e-7e2b-4c5d-9a1b-9c7d8e9f0a1b"
BREP_GUID       = "a2c4e6f8-9b0d-4e1f-8a3b-6c7d8e9f0a1b"

# 实际可用的 GH 组件 GUID（来自 Grasshopper）
# Number Slider
SLIDER_CLS = "Grasshopper.Kernel.Special.GH_NumberSlider"
# Panel  
PANEL_CLS = "Grasshopper.Kernel.Special.GH_Panel"
# Construct Point
POINT_CLS = "Grasshopper.Kernel.Special.GH_ConstructPoint"
# Rectangle 2pt
RECT_CLS = "Grasshopper.Kernel.Special.GH_ConstructRectangle"
# GhPython  
PYTHON_CLS = "Grasshopper.Kernel.Special.GH_PythonScript"

def make_ghx(params):
    """
    生成 .ghx 文件内容
    params: {length, width, thickness}
    """
    L = float(params.get('length', 3000))
    W = float(params.get('width', 4000))
    T = float(params.get('thickness', 100))
    
    doc_id = str(uuid.uuid4()).upper()
    
    # 组件位置（像素坐标）
    y0 = 100
    x_base = 100
    x_gap = 200
    
    components_xml = ""
    
    # === Number Slider: 长度 ===
    components_xml += f'''
    <object description="Sl:3000" name="长度" type="{SLIDER_CLS}" guid="{{{SLIDER_GUID}}}">
      <chunks>
        <chunk name="Definition">
          <items>
            <item type="int" name="Type">5</item>
            <item type="double" name="SlidingAmount">0.5</item>
            <item type="double" name="Minimum">{L-2000}</item>
            <item type="double" name="Maximum">{L+2000}</item>
            <item type="double" name="Value">{L}</item>
            <item type="int" name="DecimalPlaces">0</item>
            <item type="string" name="Name">长度</item>
            <item type="string" name="Expression"></item>
            <item type="bool" name="IsExpression">false</item>
          </items>
        </chunk>
        <chunk name="Layout">
          <items>
            <item type="point" name="Pivot">{{{x_base},{y0},0.0}}</item>
          </items>
        </chunk>
      </chunks>
    </object>'''
    
    # === Number Slider: 宽度 ===
    components_xml += f'''
    <object description="Sl:2000" name="宽度" type="{SLIDER_CLS}" guid="{{{{{str(uuid.uuid4()).upper()}}}}}">
      <chunks>
        <chunk name="Definition">
          <items>
            <item type="int" name="Type">5</item>
            <item type="double" name="Minimum">{W-2000}</item>
            <item type="double" name="Maximum">{W+2000}</item>
            <item type="double" name="Value">{W}</item>
            <item type="int" name="DecimalPlaces">0</item>
            <item type="string" name="Name">宽度</item>
          </items>
        </chunk>
        <chunk name="Layout">
          <items>
            <item type="point" name="Pivot">{{{x_base},{y0+100},0.0}}</item>
          </items>
        </chunk>
      </chunks>
    </object>'''
    
    # === Number Slider: 厚度 ===
    components_xml += f'''
    <object description="Sl:50" name="厚度" type="{SLIDER_CLS}" guid="{{{{{str(uuid.uuid4()).upper()}}}}}">
      <chunks>
        <chunk name="Definition">
          <items>
            <item type="int" name="Type">5</item>
            <item type="double" name="Minimum">20</item>
            <item type="double" name="Maximum">500</item>
            <item type="double" name="Value">{T}</item>
            <item type="int" name="DecimalPlaces">0</item>
            <item type="string" name="Name">厚度</item>
          </items>
        </chunk>
        <chunk name="Layout">
          <items>
            <item type="point" name="Pivot">{{{x_base},{y0+200},0.0}}</item>
          </items>
        </chunk>
      </chunks>
    </object>'''
    
    # === 构建 .ghx 完整 XML ===
    ghx = f'''<?xml version="1.0" encoding="utf-8"?>
<Archive name=":hQAAAEdpem1vAHJpZ2h0AEJvdHRvbQ==">
  <chunk name="Definition">
    <items>
      <item type="int" name="ghenv.ComponentVersion">2</item>
      <item type="guid" name="ghenv.DocumentGuid">{{{doc_id}}}</item>
      <item type="string" name="ghenv.Description">Hanako 参数化楼板</item>
    </items>
    <chunk name="Document">
      <items>
        <item type="int" name="ghenv.SolutionMode">0</item>
        <item type="bool" name="ghenv.EnableSolver">true</item>
        <item type="int" name="ghenv.ObjectListVersion">1</item>
      </items>
      <chunk name="ObjectList">
        <items>
          <item type="int" name="ObjectCount">3</item>
        </items>
        {components_xml}
      </chunk>
    </chunk>
  </chunk>
</Archive>'''
    
    return ghx


def generate(params, output_path):
    """生成 .ghx 文件"""
    ghx_content = make_ghx(params)
    with open(output_path, 'w', encoding='utf-8') as f:
        f.write(ghx_content)
    print(f"已生成: {output_path}")
    print(f"大小: {len(ghx_content)} 字节")
    return output_path


if __name__ == '__main__':
    # 测试
    generate(
        {'length': 3000, 'width': 4000, 'thickness': 100},
        'D:/-A-hanako/gh-bridge/hanako_floor.ghx'
    )
