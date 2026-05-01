"""
Hanako GhPython 常驻监听桥 v1.0
首次运行自动开启 HTTP 服务（9877 端口），等待远程指令。
"""

import clr

# .NET 基础库
clr.AddReference('System')
clr.AddReference('System.Net')

from System.Threading import Thread, ThreadStart, ManualResetEvent
from System.Net import HttpListener
from System.Text import Encoding
from System.IO import StreamReader

import json

# 全局状态
_listener_host = None
_listener_thread = None
_shutdown_event = None
_bridge_initialized = False


# ============================================================
# HTTP 监听服务
# ============================================================

def start_http_listener(port=9877):
    global _listener_host, _shutdown_event, _listener_thread
    if _listener_host is not None:
        return True

    _shutdown_event = ManualResetEvent(False)

    def listener_loop():
        global _listener_host
        try:
            _listener_host = HttpListener()
            _listener_host.Prefixes.Add('http://localhost:{0}/'.format(port))
            _listener_host.Start()
            print('[Hanako Bridge] OK: http://localhost:{0}/'.format(port))

            while not _shutdown_event.WaitOne(100, False):
                try:
                    context = _listener_host.GetContext()
                    handle_request(context)
                except:
                    if _shutdown_event.WaitOne(0):
                        break
        except Exception as ex:
            print('[Hanako Bridge] 启动失败: {0}'.format(ex))
            _listener_host = None

    _listener_thread = Thread(ThreadStart(listener_loop))
    _listener_thread.IsBackground = True
    _listener_thread.Start()
    return True


def handle_request(context):
    req = context.Request
    resp = context.Response
    body = '{}'
    try:
        if req.HttpMethod == 'GET':
            result = {'status': 'ok'}
        elif req.HttpMethod == 'POST':
            reader = StreamReader(req.InputStream, Encoding.UTF8)
            body = reader.ReadToEnd()
            cmd = json.loads(body)

            if 'action' in cmd:
                clr.AddReference('RevitAPI')
                clr.AddReference('RevitAPIUI')
                clr.AddReference('RevitServices')
                from Autodesk.Revit.DB import *
                from RevitServices.Persistence import DocumentManager
                doc = DocumentManager.Instance.CurrentDBDocument

                action = cmd['action']
                params = cmd.get('params', {})

                if action == 'ping':
                    result = {'success': True, 'message': 'pong',
                              'version': doc.Application.VersionNumber,
                              'document': doc.Title}
                elif action == 'get_levels':
                    levels = FilteredElementCollector(doc).OfClass(Level).ToElements()
                    result = {'success': True, 'data': [{'name': l.Name} for l in levels]}
                elif action == 'create_wall':
                    sx = float(params.get('start_x', 0)) / 304.8
                    sy = float(params.get('start_y', 0)) / 304.8
                    ex = float(params.get('end_x', 10000)) / 304.8
                    ey = float(params.get('end_y', 0)) / 304.8
                    h = float(params.get('height', 4000)) / 304.8
                    levels = FilteredElementCollector(doc).OfClass(Level).ToElements()
                    level = levels[0]
                    line = Line.CreateBound(XYZ(sx, sy, 0), XYZ(ex, ey, 0))
                    t = Transaction(doc, 'Hanako: wall')
                    t.Start()
                    wall = Wall.Create(doc, line, level.Id, False)
                    wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).Set(h)
                    t.Commit()
                    result = {'success': True, 'element_id': wall.Id.IntegerValue}
                elif action == 'create_floor':
                    pts = params.get('points', [
                        {'x': 0, 'y': 0},
                        {'x': 2000, 'y': 0},
                        {'x': 2000, 'y': 2000},
                        {'x': 0, 'y': 2000}
                    ])
                    thick = float(params.get('thickness', 50)) / 304.8
                    levels = FilteredElementCollector(doc).OfClass(Level).ToElements()
                    level = levels[0] if levels else None
                    pts_xyz = [XYZ(float(p['x']) / 304.8, float(p['y']) / 304.8, 0) for p in pts]
                    curves = CurveArray()
                    for i in range(len(pts_xyz)):
                        curves.Append(Line.CreateBound(pts_xyz[i], pts_xyz[(i + 1) % len(pts_xyz)]))
                    t = Transaction(doc, 'Hanako: floor')
                    t.Start()
                    floor = doc.Create.NewFloor(curves, False)
                    type_param = floor.get_Parameter(BuiltInParameter.FLOOR_ATTR_DEFAULT_THICKNESS)
                    if type_param and not type_param.IsReadOnly:
                        type_param.Set(thick)
                    t.Commit()
                    result = {'success': True, 'element_id': floor.Id.IntegerValue}
                else:
                    result = {'success': False, 'message': '未知命令: {0}'.format(action)}
            else:
                result = {'success': False, 'message': '缺少 action'}
        else:
            result = {'error': '仅支持 GET/POST'}

        body = json.dumps(result, ensure_ascii=False)
        buf = Encoding.UTF8.GetBytes(body)
        resp.ContentType = 'application/json; charset=utf-8'
        resp.ContentLength64 = buf.Length
        resp.OutputStream.Write(buf, 0, buf.Length)
    except Exception as ex:
        buf = Encoding.UTF8.GetBytes(json.dumps({'error': str(ex)}))
        resp.ContentType = 'application/json; charset=utf-8'
        resp.ContentLength64 = buf.Length
        resp.OutputStream.Write(buf, 0, buf.Length)
    finally:
        resp.OutputStream.Close()


# ============================================================
# Grasshopper 入口
# ============================================================

def RunScript():
    global _bridge_initialized
    if not _bridge_initialized:
        ok = start_http_listener(9877)
        _bridge_initialized = True
        if ok:
            return '就绪: 9877'
        else:
            return '启动失败'
    return '运行中: 9877'
