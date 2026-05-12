using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino;

namespace HanakoBridge
{
    public class HanakoBridgeComponent : GH_Component
    {
        private static HttpListener _listener;
        private static Thread _lisThread;
        private static bool _running;
        private static string _lastStatus;
        private static string _pendingCmd;
        private static ManualResetEvent _pendingWait;
        private static string _pendingResult;
        private static volatile bool _solving;
        private static DateTime _solveStart;
        private static volatile bool _asyncBuild;
        private static string _asyncResult;
        private static volatile bool _secondSolve;
        private static readonly object _cmdLock = new object();
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        public static Dictionary<string, string> ProxyDB;
        public static Dictionary<string, string> CompDB;

        private static GH_Document _ghDoc;

        public HanakoBridgeComponent() : base("Hanako", "Hanako", "Bridge", "Hanako", "Bridge") { }
        public override Guid ComponentGuid { get { return new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"); } }
        protected override Bitmap Icon { get { return null; } }
        public override GH_Exposure Exposure { get { return GH_Exposure.primary; } }
        protected override void RegisterInputParams(GH_InputParamManager pM) { }
        protected override void RegisterOutputParams(GH_OutputParamManager pM) { pM.AddTextParameter("S", "S", "", GH_ParamAccess.item); }

        protected override void SolveInstance(IGH_DataAccess DA) {
            _ghDoc = OnPingDocument();
            var cmd = Interlocked.Exchange(ref _pendingCmd, null);
            if (cmd != null) {
                _lastStatus = Exec(cmd);
                var w = Interlocked.Exchange(ref _pendingWait, null);
                if (w != null) { _pendingResult = _lastStatus; w.Set(); }
            }
            if (!_running) {
                _running = true;
                this.Message = "OK"; _lastStatus = "OK";
                _lisThread = new Thread(LisRun) { IsBackground = true };
                _lisThread.Start();
            }
            DA.SetData(0, _lastStatus);
        }

        string Exec(string body) {
            try {
                // 尝试 JSON 解析，不行就回退到 string.Contains
                try {
                    var def = _json.Deserialize<Dictionary<string, object>>(body);
                    if (def != null && def.ContainsKey("action")) {
                        string a = (def["action"] as string) ?? "";
                        switch (a) {
                            case "ping":             return "pong";
                            case "scan":             return DoScan();
                            case "inspect":          return DoInspect(body);
                            case "check":            return DoCheck();
                            case "builddb":          return BuildDB();
                            case "qdb":              return QueryDB(body);
                            case "build":            return DoBuild(body);
                            case "get_levels":       return DoGetLevels();
                            case "get_families":     return DoGetFamilies();
                            case "get_wall_types":   return DoGetWallTypes();
                            case "get_active_view":  return DoGetActiveView();
                            case "wires":             return DoCheckWires();
                            case "scene":             return DoScene();
                            case "clear":             return DoClear();
                            case "bake":             return DoBake();
                            case "wire":             return DoWire(body);
                            case "verify":             return DoVerify(body);
                            case "diag":              return DoDiag();
                            default:                 return "?";
                        }
                    }
                } catch { }

                // 回退：旧式 string.Contains 匹配
                if (body.Contains("\"ping\"")) return "pong";
                if (body.Contains("\"scan\"")) return DoScan();
                if (body.Contains("\"build\"")) return DoBuild(body);
                return "?";
            } catch (Exception ex) { return "ex:" + ex.Message; }
        }

        void LisRun() {
            try {
                int port = 0;
                for (int p = 14880; p < 15000; p++) {
                    try { var l = new HttpListener(); l.Prefixes.Add("http://localhost:" + p + "/"); l.Start(); _listener = l; port = p; break; } catch { }
                }
                if (port == 0) { _lastStatus = "NO PORT"; return; }
                _lastStatus = ":" + port;
                try { File.WriteAllText("D:/-A-hanako/gh-bridge/port.txt", port.ToString()); } catch { }
                while (true) {
                    var ctx = _listener.GetContext();
                    try { Reply(ctx); } catch { try { ctx.Response.OutputStream.Close(); } catch { } }
                }
            } catch { _lastStatus = "FAIL"; }
        }

        void Reply(HttpListenerContext ctx) {
            var req = ctx.Request; var resp = ctx.Response; string result;
            if (req.HttpMethod == "GET") { result = "{\"s\":\"" + _lastStatus + "\"}"; }
            else if (req.HttpMethod == "POST") {
                var buf = new byte[req.ContentLength64 > 0 ? (int)req.ContentLength64 : 4096];
                int read = req.InputStream.Read(buf, 0, buf.Length);
                string body = Encoding.UTF8.GetString(buf, 0, read);
                if (body.Contains("\"ping\"")) { result = "{\"ok\":true}"; }
                else {
                    // 所有指令统一走队列 + SolveInstance 路径
                    _pendingCmd = body;
                    var w = new ManualResetEvent(false);
                    _pendingWait = w; _pendingResult = null;
                    try {
                        Rhino.RhinoApp.MainApplicationWindow.Invoke((Action)(() => {
                            try {
                                ExpireSolution(false);
                                var cv = Grasshopper.Instances.ActiveCanvas;
                                if (cv != null) { var d = cv.Document; if (d != null) d.NewSolution(false); }
                            } catch { }
                        }));
                    } catch { }
                    if (w.WaitOne(15000)) result = _pendingResult ?? "nope";
                    else result = "queued";
                }
            } else result = "{\"ok\":false}";
            var rb = Encoding.UTF8.GetBytes(result);
            resp.ContentType = "application/json"; resp.ContentLength64 = rb.Length;
            resp.OutputStream.Write(rb, 0, rb.Length); resp.OutputStream.Close();
        }

        // ==== SCAN ====
        string DoScan() {
            if (ProxyDB == null) ProxyDB = new Dictionary<string, string>();
            ProxyDB.Clear();
            foreach (var p in Grasshopper.Instances.ComponentServer.ObjectProxies) {
                string g = p.Guid.ToString().Substring(0, 8);
                string n = "?", c = "?";
                try { n = p.Desc.ToString(); } catch { }
                try { c = p.GetType().GetProperty("Category").GetValue(p, null).ToString(); } catch { }
                ProxyDB[g] = n + "|" + c;
            }
            return "scan:" + ProxyDB.Count;
        }

        // ==== INSPECT ====
        string DoInspect(string body) {
            string guid = "";
            int idx = body.IndexOf("\"guid\"");
            if (idx >= 0) {
                int start = body.IndexOf('"', idx + 7) + 1; int end = body.IndexOf('"', start);
                if (end > start) guid = body.Substring(start, end - start);
            }
            if (guid.Length == 0) return "need guid";
            var obj = CI(guid); if (obj == null) return "not found";
            try {
                var p = obj.GetType().GetProperty("Params").GetValue(obj, null);
                dynamic dp = p;
                var inputs = new List<object>(); var outputs = new List<object>();
                foreach (dynamic inp in dp.Input) inputs.Add(new { n = inp.NickName ?? "", t = inp.TypeName ?? "" });
                foreach (dynamic outp in dp.Output) outputs.Add(new { n = outp.NickName ?? "", t = outp.TypeName ?? "" });
                return _json.Serialize(new { name = obj.GetType().Name, inputs, outputs });
            } catch { return "no params"; }
        }

        // ==== WIRES ====
        string DoCheckWires() {
            var doc = _ghDoc;
            if (doc == null) return "no doc";
            try {
                int totalComps = 0, totalIns = 0, totalOuts = 0, connectedIns = 0;
                var details = new List<object>();
                foreach (var obj in doc.Objects) {
                    totalComps++;
                    try {
                        var p = obj.GetType().GetProperty("Params").GetValue(obj, null);
                        if (p == null) continue;
                        dynamic dp = p;
                        var inputList = (IList)dp.Input;
                        var outputList = (IList)dp.Output;
                        int inCount = inputList?.Count ?? 0;
                        int outCount = outputList?.Count ?? 0;
                        totalIns += inCount;
                        totalOuts += outCount;
                        int inConnected = 0;
                        string debug = "";
                        for (int i = 0; i < inCount; i++) {
                            try {
                                var pIn = (IGH_Param)inputList[i];
                                if (pIn.SourceCount > 0) inConnected++;
                            } catch { }
                        }
                        string name = "?";
                        try { name = obj.GetType().Name; } catch { }
                        try { dynamic d = obj; name = d.NickName ?? name; } catch { }
                        if (inCount > 0 || outCount > 0 || name == "Hanako")
                            details.Add(new { name, inputs = inCount, connected = inConnected, outputs = outCount });
                    } catch { }
                }
                return _json.Serialize(new {
                    totalComponents = totalComps,
                    totalInputs = totalIns,
                    connectedInputs = connectedIns,
                    unconnectedInputs = totalIns - connectedIns,
                    totalOutputs = totalOuts,
                    details
                });
            } catch (Exception ex) { return "wires err:" + ex.Message; }
        }

        // ==== BUILD ====
        string DoBuild(string body) {
            var doc = _ghDoc;
            if (doc == null) return "no doc";
            try {
                var def = _json.Deserialize<Dictionary<string, object>>(body);
                if (!def.ContainsKey("components")) return "need components";
                var comps = (ArrayList)def["components"];
                var created = new Dictionary<string, object>();
                foreach (var c in comps) {
                    var cp = (Dictionary<string, object>)c;
                    string id = (string)cp["id"];
                    string guid = (string)cp["guid"];
                    var obj = CI(guid);
                    if (obj == null) return "missing:" + guid;
                    if (cp.ContainsKey("val")) { try { dynamic d = obj; d.Value = Convert.ToDouble(cp["val"]); } catch { } }
                    if (cp.ContainsKey("nick")) { try { dynamic d = obj; d.NickName = (string)cp["nick"]; } catch { } }
                    // 支持 Param_Interval 设 Domain 值
                    if (cp.ContainsKey("dval")) {
                        try {
                            var arr = (ArrayList)cp["dval"];
                            double d0 = Convert.ToDouble(arr[0]);
                            double d1 = Convert.ToDouble(arr[1]);
                            var itv = new Rhino.Geometry.Interval(d0, d1);
                            var ghInt = new Grasshopper.Kernel.Types.GH_Interval(itv);
                            dynamic d = obj;
                            d.PersistentData.Clear();
                            d.PersistentData.Append(ghInt);
                        } catch { }
                    }
                    created[id] = obj;
                    try { doc.AddObject((IGH_DocumentObject)obj, false); } catch { }
                }
                if (def.ContainsKey("wires")) {
                    var wires = (ArrayList)def["wires"];
                    foreach (var w in wires) {
                        var wl = (IList)w;
                        string fromId = (string)wl[0]; int fromOut = Convert.ToInt32(wl[1]);
                        string toId = (string)wl[2]; int toIn = Convert.ToInt32(wl[3]);
                        if (!created.ContainsKey(fromId) || !created.ContainsKey(toId)) continue;
                        try {
                            var fromObj = created[fromId]; var toObj = created[toId];
                            // 获取源参数：如果 fromObj 自身就是 IGH_Param（如 Number Slider），直接用它
                            // 否则从 Params.Output 里取
                            IGH_Param srcParam;
                            if (fromObj is IGH_Param) {
                                srcParam = (IGH_Param)fromObj;
                            } else {
                                var fromP = fromObj.GetType().GetProperty("Params").GetValue(fromObj, null);
                                dynamic fromPd = fromP;
                                srcParam = (IGH_Param)((IList)fromPd.Output)[fromOut];
                            }
                            // 目标参数
                            var toP = toObj.GetType().GetProperty("Params").GetValue(toObj, null);
                            dynamic toPd2 = toP;
                            ((IGH_Param)((IList)toPd2.Input)[toIn]).AddSource(srcParam);
                        } catch { }
                    }
                }
                if (def.ContainsKey("positions")) {
                    var posDict = (Dictionary<string, object>)def["positions"];
                    foreach (var kv in posDict) {
                        if (!created.ContainsKey(kv.Key)) continue;
                        var pos = (IList)kv.Value;
                        try { dynamic d = created[kv.Key]; d.Attributes.Pivot = new PointF(Convert.ToSingle(pos[0]), Convert.ToSingle(pos[1])); } catch { }
                    }
                }
                // 记录创建的组件 GUID，方便后续 wire 指令引用
                var idMap = new List<object>();
                foreach (var kv in created) {
                    try {
                        var obj = (IGH_DocumentObject)kv.Value;
                        idMap.Add(new { id = kv.Key, guid = obj.InstanceGuid.ToString() });
                    } catch { }
                }
                // 调度下一轮求解（当前在求解中，不能直接 NewSolution）
                try { doc.ScheduleSolution(5, (d) => { try { d.NewSolution(false); } catch { } }); } catch { }
                return _json.Serialize(new { result = "built:" + created.Count + " comps", components = idMap });
            } catch (Exception ex) { return "build err:" + ex.Message; }
        }

        // ==== BUILD DB ====
        string BuildDB() {
            if (CompDB == null) CompDB = new Dictionary<string, string>();
            CompDB.Clear();
            foreach (var p in Grasshopper.Instances.ComponentServer.ObjectProxies) {
                string g = p.Guid.ToString().Substring(0, 8);
                string name = "?";
                string ins = "[]", outs = "[]";
                try {
                    var mi = p.GetType().GetMethod("CreateInstance", Type.EmptyTypes);
                    if (mi != null) {
                        var obj = mi.Invoke(p, null);
                        if (obj != null) {
                            name = obj.GetType().Name;
                            try {
                                var par = obj.GetType().GetProperty("Params").GetValue(obj, null);
                                if (par != null) {
                                    dynamic dp = par;
                                    var il = new List<object>(); var ol = new List<object>();
                                    foreach (dynamic inp in dp.Input) il.Add(new { n = inp.NickName ?? "", t = inp.TypeName ?? "" });
                                    foreach (dynamic outp in dp.Output) ol.Add(new { n = outp.NickName ?? "", t = outp.TypeName ?? "" });
                                    ins = _json.Serialize(il); outs = _json.Serialize(ol);
                                }
                            } catch { }
                        }
                    }
                } catch { }
                if (name == "?") try { name = p.Desc.ToString(); } catch { }
                CompDB[g] = name + "||" + ins + "||" + outs;
            }
            return "db:" + CompDB.Count;
        }

        // ==== QUERY DB ====
        string QueryDB(string body) {
            if (CompDB == null || CompDB.Count == 0) return "run builddb first";
            string key = "";
            int idx = body.IndexOf("\"k\"");
            if (idx >= 0) {
                int start = body.IndexOf('"', idx + 4) + 1;
                int end = body.IndexOf('"', start);
                if (end > start) key = body.Substring(start, end - start).ToLower();
            }
            var hits = new List<object>();
            foreach (var kv in CompDB) {
                if (kv.Value.ToLower().Contains(key)) {
                    string info = kv.Value; if (info.Length > 150) info = info.Substring(0, 150);
                    hits.Add(new { g = kv.Key, info });
                }
                if (hits.Count > 20) break;
            }
            return hits.Count + " hits: " + _json.Serialize(hits);
        }

        object CI(string prefix) {
            foreach (var p in Grasshopper.Instances.ComponentServer.ObjectProxies)
                if (p.Guid.ToString().StartsWith(prefix)) {
                    var mi = p.GetType().GetMethod("CreateInstance", Type.EmptyTypes);
                    if (mi != null) return mi.Invoke(p, null);
                }
            return null;
        }

        // ======================== Revit 查询 ========================

        object GetRevitDoc() {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "RhinoInside.Revit") {
                    var t = a.GetType("RhinoInside.Revit.Revit");
                    if (t != null) {
                        var dp = t.GetProperty("ActiveDBDocument", BindingFlags.Public | BindingFlags.Static);
                        if (dp != null) return dp.GetValue(null, null);
                    }
                }
            return null;
        }

        Assembly GetRevitAsm(object doc) { return doc?.GetType().Assembly; }

        string DoGetLevels() {
            var doc = GetRevitDoc(); if (doc == null) return JsonResult(false, "RIR 未加载或没有打开的 Revit 文档");
            try {
                var asm = GetRevitAsm(doc);
                var levelType = asm.GetType("Autodesk.Revit.DB.Level");
                var results = CollectElements(doc, asm, levelType);
                var levels = new List<object>();
                foreach (var el in results) {
                    string name = ""; double elevation = 0;
                    try { name = (string)el.GetType().GetProperty("Name").GetValue(el, null); } catch { }
                    try { elevation = (double)el.GetType().GetProperty("Elevation").GetValue(el, null); } catch { }
                    levels.Add(new { name = name ?? "?", elevation = Math.Round(elevation * 304.8, 1) });
                }
                return _json.Serialize(new { success = true, data = levels });
            } catch (Exception ex) { return JsonResult(false, "查询标高失败: " + ex.Message); }
        }

        string DoGetFamilies() {
            var doc = GetRevitDoc(); if (doc == null) return JsonResult(false, "RIR 未加载或没有打开的 Revit 文档");
            try {
                var asm = GetRevitAsm(doc);
                var bicType = asm.GetType("Autodesk.Revit.DB.BuiltInCategory");
                var familySymbolType = asm.GetType("Autodesk.Revit.DB.FamilySymbol");
                var cats = new Dictionary<string, object[]> {
                    ["columns"] = new[] { Enum.ToObject(bicType, -2000012) },
                    ["beams"]   = new[] { Enum.ToObject(bicType, -2000011) },
                    ["doors"]   = new[] { Enum.ToObject(bicType, -2000020) },
                    ["windows"] = new[] { Enum.ToObject(bicType, -2000030) },
                };
                var data = new Dictionary<string, object>();
                foreach (var kv in cats) {
                    var symbols = CollectElementsByCategory(doc, asm, familySymbolType, bicType, (int)kv.Value[0]);
                    var list = new List<object>();
                    foreach (var el in symbols) {
                        string fn = "", sn = "";
                        try { fn = (string)el.GetType().GetProperty("FamilyName")?.GetValue(el, null); } catch { }
                        try { sn = (string)el.GetType().GetProperty("Name")?.GetValue(el, null); } catch { }
                        list.Add(new { familyName = fn ?? "", name = sn ?? "" });
                    }
                    data[kv.Key] = list;
                }
                var wallTypeType = asm.GetType("Autodesk.Revit.DB.WallType");
                data["walls"] = CollectElements(doc, asm, wallTypeType).Cast<object>().Select(e => {
                    string n = ""; try { n = (string)e.GetType().GetProperty("Name")?.GetValue(e, null); } catch { }
                    return new { name = n ?? "" };
                }).ToList();
                var floorTypeType = asm.GetType("Autodesk.Revit.DB.FloorType");
                data["floors"] = CollectElements(doc, asm, floorTypeType).Cast<object>().Select(e => {
                    string n = ""; try { n = (string)e.GetType().GetProperty("Name")?.GetValue(e, null); } catch { }
                    return new { name = n ?? "" };
                }).ToList();
                return _json.Serialize(new { success = true, data });
            } catch (Exception ex) { return JsonResult(false, "查询族类型失败: " + ex.Message); }
        }

        string DoGetWallTypes() {
            var doc = GetRevitDoc(); if (doc == null) return JsonResult(false, "RIR 未加载或没有打开的 Revit 文档");
            try {
                var asm = GetRevitAsm(doc);
                var wallTypeType = asm.GetType("Autodesk.Revit.DB.WallType");
                var results = CollectElements(doc, asm, wallTypeType);
                var types = new List<object>();
                foreach (var wt in results) {
                    string name = "", kind = ""; double width = 0;
                    try { name = (string)wt.GetType().GetProperty("Name")?.GetValue(wt, null); } catch { }
                    try { kind = wt.GetType().GetProperty("Kind")?.GetValue(wt, null)?.ToString(); } catch { }
                    try { width = (double)wt.GetType().GetProperty("Width")?.GetValue(wt, null) * 304.8; } catch { }
                    types.Add(new { name = name ?? "?", kind = kind ?? "?", width = Math.Round(width, 1) });
                }
                return _json.Serialize(new { success = true, data = types });
            } catch (Exception ex) { return JsonResult(false, "查询墙类型失败: " + ex.Message); }
        }

        string DoGetActiveView() {
            try {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "RhinoInside.Revit") {
                        var revitType = a.GetType("RhinoInside.Revit.Revit");
                        var uiAppProp = revitType?.GetProperty("ActiveUIApplication", BindingFlags.Public | BindingFlags.Static);
                        if (uiAppProp != null) {
                            var uiApp = uiAppProp.GetValue(null, null);
                            if (uiApp != null) {
                                var uidoc = uiApp.GetType().GetProperty("ActiveUIDocument")?.GetValue(uiApp, null);
                                if (uidoc != null) {
                                    var view = uidoc.GetType().GetProperty("ActiveView")?.GetValue(uidoc, null);
                                    if (view != null) {
                                        string name = "", type = "", level = "";
                                        try { name = (string)view.GetType().GetProperty("Name")?.GetValue(view, null); } catch { }
                                        try { type = view.GetType().GetProperty("ViewType")?.GetValue(view, null)?.ToString(); } catch { }
                                        try { var gl = view.GetType().GetProperty("GenLevel")?.GetValue(view, null); if (gl != null) level = (string)gl.GetType().GetProperty("Name")?.GetValue(gl, null); } catch { }
                                        return _json.Serialize(new { success = true, data = new { name = name ?? "?", type = type ?? "?", level = level ?? "" } });
                                    }
                                }
                            }
                        }
                        break;
                    }
                return JsonResult(false, "无法获取激活视图");
            } catch (Exception ex) { return JsonResult(false, "查询视图失败: " + ex.Message); }
        }

        string DoCheck() {
            try {
                var doc = GetRevitDoc(); if (doc == null) return "rir not found";
                var asm = GetRevitAsm(doc);
                var fecType = asm.GetType("Autodesk.Revit.DB.FilteredElementCollector");
                if (fecType == null) return "revit active";
                var collector = Activator.CreateInstance(fecType, new object[] { doc });
                var countProp = fecType.GetMethod("GetElementCount");
                int count = countProp != null ? (int)countProp.Invoke(collector, null) : 0;
                int floorCount = 0;
                var floorClass = asm.GetType("Autodesk.Revit.DB.Floor");
                if (floorClass != null) {
                    var ecfType = asm.GetType("Autodesk.Revit.DB.ElementClassFilter");
                    var classFilter = Activator.CreateInstance(ecfType, new object[] { floorClass });
                    var fCollector = Activator.CreateInstance(fecType, new object[] { doc });
                    var wherePasses = fecType.GetMethod("WherePasses", new Type[] { asm.GetType("Autodesk.Revit.DB.ElementFilter") });
                    if (wherePasses != null) {
                        var filtered = wherePasses.Invoke(fCollector, new object[] { classFilter });
                        var getCount = fecType.GetMethod("GetElementCount");
                        if (getCount != null) floorCount = (int)getCount.Invoke(filtered, null);
                    }
                }
                return "elements:" + count + " floors:" + floorCount;
            } catch (Exception ex) { return "c:" + ex.Message; }
        }

        IList CollectElements(object doc, Assembly asm, Type targetType) {
            var fecType = asm.GetType("Autodesk.Revit.DB.FilteredElementCollector");
            var classFilterType = asm.GetType("Autodesk.Revit.DB.ElementClassFilter");
            var collector = Activator.CreateInstance(fecType, new object[] { doc });
            var classFilter = Activator.CreateInstance(classFilterType, new object[] { targetType });
            var wherePasses = fecType.GetMethod("WherePasses", new Type[] { asm.GetType("Autodesk.Revit.DB.ElementFilter") });
            return (IList)fecType.GetMethod("ToElements").Invoke(wherePasses.Invoke(collector, new object[] { classFilter }), null);
        }

        IList CollectElementsByCategory(object doc, Assembly asm, Type symbolType, Type bicType, int catVal) {
            var fecType = asm.GetType("Autodesk.Revit.DB.FilteredElementCollector");
            var collector = Activator.CreateInstance(fecType, new object[] { doc });
            fecType.GetMethod("OfClass", new Type[] { typeof(Type) })?.Invoke(collector, new object[] { symbolType });
            fecType.GetMethod("OfCategory", new Type[] { bicType })?.Invoke(collector, new object[] { Enum.ToObject(bicType, catVal) });
            return (IList)fecType.GetMethod("ToElements").Invoke(collector, null);
        }

        string JsonResult(bool success, string message) {
            return _json.Serialize(new { success, message });
        }

        // ==== CLEAR ====
        string DoClear() {
            var doc = _ghDoc;
            if (doc == null) return "no doc";
            try {
                var toRemove = new List<IGH_DocumentObject>();
                foreach (var obj in doc.Objects) {
                    // 保留 Hanako 桥本身
                    if (obj is HanakoBridgeComponent) continue;
                    toRemove.Add(obj);
                }
                int count = toRemove.Count;
                foreach (var obj in toRemove) {
                    try { doc.RemoveObject(obj, false); } catch { }
                }
                try { doc.NewSolution(false); } catch { }
                return "cleared:" + count + " objects";
            } catch (Exception ex) { return "clear err:" + ex.Message; }
        }

        // ==== BAKE ====
        string DoBake() {
            var doc = _ghDoc;
            if (doc == null) return "no doc";
            try {
                int baked = 0;
                var rDoc = Rhino.RhinoDoc.ActiveDoc;
                if (rDoc == null) return "no rhino doc";

                foreach (var obj in doc.Objects) {
                    if (obj is HanakoBridgeComponent) continue;
                    try {
                        var p = obj.GetType().GetProperty("Params").GetValue(obj, null);
                        if (p == null) continue;
                        dynamic dp = p;
                        var outs = (IList)dp.Output;
                        if (outs == null) continue;
                        for (int i = 0; i < outs.Count; i++) {
                            try {
                                // 直接用 IGH_Param.BakeGeometry()，GH 内置烘焙方法
                                var ghOut = (IGH_Param)outs[i];
                                // 尝试各种 BakeGeometry 签名
                                var bakeMi = ghOut.GetType().GetMethod("BakeGeometry",
                                    new Type[] { typeof(Rhino.RhinoDoc) });
                                if (bakeMi == null) {
                                    bakeMi = ghOut.GetType().GetMethod("BakeGeometry", Type.EmptyTypes);
                                }
                                if (bakeMi != null) {
                                    var pars = bakeMi.GetParameters();
                                    var args = pars.Length == 0
                                        ? new object[0]
                                        : new object[] { rDoc };
                                    var result = bakeMi.Invoke(ghOut, args);
                                    if (result is int count) baked += count;
                                    else if (result is Guid[] guids) baked += guids.Length;
                                    else if (result is bool && (bool)result) baked++;
                                } else {
                                    // 退回到遍历数据
                                    var data = ghOut.VolatileData;
                                    if (data == null || data.IsEmpty) continue;
                                    foreach (var item in data.AllData(true)) {
                                        try {
                                            var valProp = item.GetType().GetProperty("Value");
                                            if (valProp != null) {
                                                var geo = valProp.GetValue(item, null);
                                                if (geo != null) {
                                                    try { rDoc.Objects.Add((Rhino.Geometry.Mesh)geo); baked++; continue; } catch { }
                                                    try { rDoc.Objects.Add((Rhino.Geometry.Brep)geo); baked++; continue; } catch { }
                                                    try { rDoc.Objects.Add((Rhino.Geometry.Curve)geo); baked++; continue; } catch { }
                                                    try { rDoc.Objects.Add((Rhino.Geometry.Point)geo); baked++; continue; } catch { }
                                                    try { rDoc.Objects.Add((Rhino.Geometry.Surface)geo); baked++; continue; } catch { }
                                                }
                                            }
                                        } catch { }
                                    }
                                }
                            } catch { }
                        }
                    } catch { }
                }
                if (baked > 0) rDoc.Views.Redraw();
                return "baked:" + baked + " objects";
            } catch (Exception ex) { return "bake err:" + ex.Message; }
        }

        // ==== WIRE ====
        string DoWire(string body) {
            var doc = _ghDoc;
            if (doc == null) return "no doc";
            try {
                var def = _json.Deserialize<Dictionary<string, object>>(body);
                if (!def.ContainsKey("wires")) return "need wires";
                var wires = (ArrayList)def["wires"];
                int count = 0;
                foreach (var w in wires) {
                    var wl = (IList)w;
                    string fromGuid = (string)wl[0];
                    int fromOut = Convert.ToInt32(wl[1]);
                    string toGuid = (string)wl[2];
                    int toIn = Convert.ToInt32(wl[3]);
                    // 按 InstanceGuid 查找画布上的组件
                    IGH_DocumentObject fromObj = null, toObj = null;
                    foreach (var obj in doc.Objects) {
                        if (obj.InstanceGuid.ToString() == fromGuid) fromObj = obj;
                        if (obj.InstanceGuid.ToString() == toGuid) toObj = obj;
                    }
                    if (fromObj == null || toObj == null) continue;
                    try {
                        IGH_Param srcParam;
                        if (fromObj is IGH_Param) {
                            srcParam = (IGH_Param)fromObj;
                        } else {
                            var fromP = fromObj.GetType().GetProperty("Params").GetValue(fromObj, null);
                            dynamic fromPd = fromP;
                            srcParam = (IGH_Param)((IList)fromPd.Output)[fromOut];
                        }
                        var toP = toObj.GetType().GetProperty("Params").GetValue(toObj, null);
                        dynamic toPd2 = toP;
                        ((IGH_Param)((IList)toPd2.Input)[toIn]).AddSource(srcParam);
                        count++;
                    } catch { }
                }
                if (count > 0) {
                    try { doc.ScheduleSolution(5, (d) => { try { d.NewSolution(false); } catch { } }); } catch { }
                }
                return "wired:" + count;
            } catch (Exception ex) { return "wire err:" + ex.Message; }
        }

        // ==== VERIFY ====
        string DoVerify(string body) {
            try {
                var def = _json.Deserialize<Dictionary<string, object>>(body);
                var issues = new List<object>();
                var comps = def.ContainsKey("components") ? (ArrayList)def["components"] : new ArrayList();
                var wireList = def.ContainsKey("wires") ? (ArrayList)def["wires"] : new ArrayList();

                // 检查 builddb 是否已运行
                bool hasDB = CompDB != null && CompDB.Count > 0;

                // 验证每个组件是否存在
                var compMap = new Dictionary<string, string>(); // id -> name
                foreach (var c in comps) {
                    var cp = (Dictionary<string, object>)c;
                    string id = (string)cp["id"];
                    string guid = (string)cp["guid"];
                    compMap[id] = guid;

                    if (hasDB) {
                        // 在 CompDB 中查找 GUID
                        bool found = false;
                        foreach (var kv in CompDB) {
                            if (kv.Key == guid) {
                                found = true;
                                string name = kv.Value;
                                int pipeIdx = name.IndexOf("||");
                                if (pipeIdx > 0) name = name.Substring(0, pipeIdx);
                                issues.Add(new { level = "info", id, guid, name, msg = "组件可用" });
                                break;
                            }
                        }
                        if (!found) {
                            issues.Add(new { level = "error", id, guid, msg = "GUID 未在 CompDB 中找到，可能不存在或需要先 builddb" });
                        }
                    }
                }

                // 验证连线
                foreach (var w in wireList) {
                    var wl = (IList)w;
                    string fromId = (string)wl[0];
                    int fromOut = Convert.ToInt32(wl[1]);
                    string toId = (string)wl[2];
                    int toIn = Convert.ToInt32(wl[3]);

                    if (!compMap.ContainsKey(fromId)) {
                        issues.Add(new { level = "error", msg = "来源组件 " + fromId + " 未定义", from = fromId });
                    }
                    if (!compMap.ContainsKey(toId)) {
                        issues.Add(new { level = "error", msg = "目标组件 " + toId + " 未定义", to = toId });
                    }
                }

                // 检查未连线的输入
                if (hasDB) {
                    // 尝试实例化组件检查接口
                    foreach (var c in comps) {
                        var cp = (Dictionary<string, object>)c;
                        string id = (string)cp["id"];
                        string guid = (string)cp["guid"];
                        var obj = CI(guid);
                        if (obj == null) continue;
                        try {
                            var p = obj.GetType().GetProperty("Params").GetValue(obj, null);
                            if (p == null) continue;
                            dynamic dp = p;
                            var ins = (IList)dp.Input;
                            if (ins == null) continue;
                            for (int i = 0; i < ins.Count; i++) {
                                // 检查这个输入有没有被任何 wire 连到
                                bool hasWire = false;
                                foreach (var w in wireList) {
                                    var wl = (IList)w;
                                    if ((string)wl[2] == id && Convert.ToInt32(wl[3]) == i) {
                                        hasWire = true;
                                        break;
                                    }
                                }
                                if (!hasWire) {
                                    string inName = "?";
                                    try { inName = ((dynamic)ins[i]).NickName ?? "?"; } catch { }
                                    string inType = "?";
                                    try { inType = ((dynamic)ins[i]).TypeName ?? "?"; } catch { }
                                    issues.Add(new { level = "warn", id, input = i, name = inName, type = inType, msg = "输入未连线" });
                                }
                            }
                        } catch { }
                    }
                }

                // 统计
                int errs = issues.Count(i => {
                    try { return (string)i.GetType().GetProperty("level").GetValue(i, null) == "error"; } catch { return false; }
                });
                int warns = issues.Count(i => {
                    try { return (string)i.GetType().GetProperty("level").GetValue(i, null) == "warn"; } catch { return false; }
                });

                return _json.Serialize(new {
                    result = errs > 0 ? "FAIL" : "PASS",
                    errors = errs,
                    warnings = warns,
                    issues
                });
            } catch (Exception ex) { return "verify err:" + ex.Message; }
        }

        // ==== DIAG ====
        string DoDiag() {
            var doc = _ghDoc;
            if (doc == null) return _json.Serialize(new { error = "no doc" });
            try {
                var results = new List<object>();
                int errCount = 0, warnCount = 0;
                foreach (var obj in doc.Objects) {
                    if (obj is HanakoBridgeComponent) continue;
                    string name = "?";
                    try { dynamic d = obj; name = d.NickName ?? obj.GetType().Name; } catch { }
                    try { name = obj.GetType().Name; } catch { }
                    // 通过反射获取 RuntimeMessages
                    var msgsProp = obj.GetType().GetProperty("RuntimeMessages",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (msgsProp != null) {
                        var msgs = msgsProp.GetValue(obj, null) as IList;
                        if (msgs != null && msgs.Count > 0) {
                            var msgList = new List<object>();
                            foreach (var m in msgs) {
                                string level = "?";
                                string message = "?";
                                try {
                                    var lvProp = m.GetType().GetProperty("Level");
                                    if (lvProp != null) level = lvProp.GetValue(m, null).ToString();
                                } catch { }
                                try {
                                    var msgProp = m.GetType().GetProperty("Message");
                                    if (msgProp != null) message = (string)msgProp.GetValue(m, null) ?? "?";
                                } catch { }
                                if (level.Contains("Error")) errCount++;
                                if (level.Contains("Warning")) warnCount++;
                                msgList.Add(new { level, message });
                            }
                            results.Add(new { name, messages = msgList });
                        }
                    }
                    // 也检查运行时描述（有些组件在 OnRuntimeMessage 中输出）
                    try {
                        var descProp = obj.GetType().GetProperty("RuntimeDescription",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (descProp != null) {
                            var desc = descProp.GetValue(obj, null) as string;
                            if (!string.IsNullOrEmpty(desc)) {
                                results.Add(new { name, runtimeDesc = desc });
                            }
                        }
                    } catch { }
                }
                return _json.Serialize(new {
                    totalComponents = doc.Objects.Count,
                    errors = errCount,
                    warnings = warnCount,
                    details = results
                });
            } catch (Exception ex) { return _json.Serialize(new { error = ex.Message }); }
        }

        // ==== SCENE ====
        string DoScene() {
            try {
                // GH 画布
                int ghComps = 0, ghWires = 0;
                string ghInfo = "no doc";
                var doc = _ghDoc;
                if (doc != null) {
                    ghComps = 0; ghWires = 0;
                    foreach (var obj in doc.Objects) {
                        ghComps++;
                        try {
                            var p = obj.GetType().GetProperty("Params").GetValue(obj, null);
                            if (p == null) continue;
                            dynamic dp = p;
                            var ins = (IList)dp.Input;
                            if (ins != null) {
                                for (int i = 0; i < ins.Count; i++) {
                                    try { if (((IGH_Param)ins[i]).SourceCount > 0) ghWires++; } catch { }
                                }
                            }
                        } catch { }
                    }
                    ghInfo = ghComps + " comps, " + ghWires + " wires";
                }

                // Rhino 文档
                string rhInfo = "no doc";
                int rhObjects = 0;
                try {
                    var rDoc = Rhino.RhinoDoc.ActiveDoc;
                    if (rDoc != null) {
                        rhObjects = rDoc.Objects.Count;
                        rhInfo = rhObjects + " objects";
                    }
                } catch { rhInfo = "error"; }

                // Revit（通过 RIR）
                string rvtInfo = "no rir";
                try {
                    var rvtDoc = GetRevitDoc();
                    if (rvtDoc != null) {
                        var asm = GetRevitAsm(rvtDoc);
                        var fecType = asm.GetType("Autodesk.Revit.DB.FilteredElementCollector");
                        if (fecType != null) {
                            var collector = Activator.CreateInstance(fecType, new object[] { rvtDoc });
                            var countProp = fecType.GetMethod("GetElementCount");
                            int rvtCount = countProp != null ? (int)countProp.Invoke(collector, null) : 0;
                            rvtInfo = rvtCount + " elements";
                        }
                    }
                } catch { rvtInfo = "error"; }

                return _json.Serialize(new {
                    timestamp = DateTime.Now.ToString("HH:mm:ss"),
                    rhino = rhInfo,
                    grasshopper = ghInfo,
                    revit = rvtInfo
                });
            } catch (Exception ex) { return "scene err:" + ex.Message; }
        }

        string DoDescribe() {
            try {
                DoBake();
                var rDoc = Rhino.RhinoDoc.ActiveDoc;
                if (rDoc == null) return _json.Serialize(new { error = "no rhino doc" });
                var results = new List<object>();
                foreach (var obj in rDoc.Objects) {
                    var geo = obj.Geometry;
                    if (geo == null) continue;
                    var d = new Dictionary<string, object>();
                    d["type"] = geo.GetType().Name;
                    var bb = geo.GetBoundingBox(true);
                    if (bb.IsValid) {
                        double dx = bb.Max.X - bb.Min.X, dy = bb.Max.Y - bb.Min.Y, dz = bb.Max.Z - bb.Min.Z;
                        d["size"] = new double[] { Math.Round(dx,2), Math.Round(dy,2), Math.Round(dz,2) };
                        double maxD = Math.Max(dx, Math.Max(dy, dz));
                        double minD = Math.Min(dx, Math.Min(dy, dz));
                        if (maxD > minD * 5) d["shape"] = "细长/管状";
                        else if (maxD < minD * 1.5) d["shape"] = "块状/近立方体";
                        else d["shape"] = "扁平/片状";
                        if (maxD > 0.1) {
                            int mainAxis = dx >= dy && dx >= dz ? 0 : (dy >= dz ? 1 : 2);
                            double[] dims = {dx,dy,dz};
                            int samples = Math.Min(10, Math.Max(3, (int)(dims[mainAxis] / 0.5)));
                            var radii = new List<double>();
                            for (int s = 0; s <= samples; s++)
                                radii.Add(Math.Round(Math.Sqrt(dims[(mainAxis+1)%3] * dims[(mainAxis+2)%3] / Math.PI) / 2, 3));
                            if (radii.Count >= 3 && radii.Average() > 0.001) {
                                double rf = radii[0], rm = radii[radii.Count/2], rl = radii[radii.Count-1], avg = radii.Average();
                                if (Math.Abs(rf-rl) < avg*0.1) d["taper"] = "等径";
                                else if (rm > rf*1.2 && rm > rl*1.2) d["taper"] = "中间粗两端细";
                                else if (rf < rl) d["taper"] = "渐粗（单向）";
                                else d["taper"] = "渐细（单向）";
                                d["radius_range"] = new double[] { radii.Min(), radii.Max() };
                            }
                        }
                    }
                    if (geo is Rhino.Geometry.Brep) try { d["volume"] = Math.Round(((Rhino.Geometry.Brep)geo).GetVolume(),4); } catch {}
                    if (geo is Rhino.Geometry.Mesh) { try { d["vertices"] = ((Rhino.Geometry.Mesh)geo).Vertices.Count; } catch {} try { d["faces"] = ((Rhino.Geometry.Mesh)geo).Faces.Count; } catch {} }
                    if (geo is Rhino.Geometry.Curve) try { d["length"] = Math.Round(((Rhino.Geometry.Curve)geo).GetLength(),2); } catch {}
                    results.Add(d);
                }
                return _json.Serialize(new { total = results.Count, results });
            } catch (Exception ex) { return _json.Serialize(new { error = ex.Message }); }
        }

        string DoScreenshot() {
            try {
                var rDoc = Rhino.RhinoDoc.ActiveDoc;
                if (rDoc == null) return _json.Serialize(new { error = "no rhino doc" });
                var v = rDoc.Views.ActiveView;
                if (v == null) return _json.Serialize(new { error = "no view" });
                var bmp = v.CaptureToBitmap();
                if (bmp == null) return _json.Serialize(new { error = "capture failed" });
                string p = "D:/agents/-A-hanako/screenshot.png";
                bmp.Save(p, System.Drawing.Imaging.ImageFormat.Png);
                return _json.Serialize(new { ok = true, path = p, width = bmp.Width, height = bmp.Height });
            } catch (Exception ex) { return _json.Serialize(new { error = ex.Message }); }
        }
    }
}
