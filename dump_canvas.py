"""GH Python Script: 导出画布上所有组件和连线为 JSON
粘贴到 Grasshopper Python 组件中运行，输出到 Panel 或文件
"""
import json

doc = ghenv.Component.OnPingDocument()
if doc is None:
    output = '{"error":"no doc"}'
else:
    comps = []
    wires = []
    guid_map = {}  # param instance guid -> (component name, component instance guid)

    for obj in doc.Objects:
        ig = str(obj.InstanceGuid)
        name = getattr(obj, 'Name', '') or obj.GetType().Name
        comp_guid = ''
        try: comp_guid = str(obj.ComponentGuid)[:8]
        except: pass

        ins = []
        outs = []
        try:
            p = obj.GetType().GetProperty('Params').GetValue(obj, None)
            for pi in p.Input:
                pig = str(pi.InstanceGuid)
                connected = pi.SourceCount > 0
                ins.append({'name': pi.Name, 'nick': pi.NickName, 'connected': connected, 'guid': pig})
                if connected:
                    for s in pi.Sources:
                        sid = str(s.InstanceGuid)
                        guid_map[sid] = (str(obj.InstanceGuid), name, pi.Name)
                        wires.append({'fromParam': sid, 'toComp': ig, 'toPort': pi.Name})
            for po in p.Output:
                pog = str(po.InstanceGuid)
                outs.append({'name': po.Name, 'nick': po.NickName, 'guid': pog})
                guid_map[pog] = (ig, name, po.Name)
        except: pass

        comps.append({
            'guid': ig[:8],
            'fullGuid': ig,
            'name': name,
            'compGuid': comp_guid,
            'inputs': ins,
            'outputs': outs
        })

    # 补全 wire 中的 source component
    for w in wires:
        sid = w['fromParam']
        if sid in guid_map:
            comp_ig, comp_name, port = guid_map[sid]
            w['fromComp'] = comp_ig[:8]
            w['fromName'] = comp_name
            w['fromPort'] = port

    output = json.dumps({'totalComponents': len(comps), 'totalWires': len(wires), 'components': comps, 'wires': wires}, indent=2, ensure_ascii=False)

# 输出到 Panel 或写文件
a = output
