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
        private static volatile bool _asyncBuild;
        private static string _asyncResult;
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        public static Dictionary<string, string> ProxyDB;
        public static Dictionary<string, string> CompDB;
        private static Dictionary<string, Guid> _idMap = new Dictionary<string, Guid>();

        private static GH_Document _ghDoc;
        private static volatile bool _solving;
        private static DateTime _solveStart;
        private static readonly object _cmdLock = new object();

        public HanakoBridgeComponent() : base("Hanako", "Hanako", "Bridge", "Hanako", "Bridge") { }
        public override Guid ComponentGuid { get { return new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"); } }
        protected override Bitmap Icon { get { return null; } }
        public override GH_Exposure Exposure { get { return GH_Exposure.primary; } }
        protected override void RegisterInputParams(GH_InputParamManager pM) { }
        protected override void RegisterOutputParams(GH_OutputParamManager pM) { pM.AddTextParameter("S", "S", "", GH_ParamAccess.item); }

        public override void AppendAdditionalMenuItems(System.Windows.Forms.ToolStripDropDown menu) {
            base.AppendAdditionalMenuItems(menu);
            Menu_AppendItem(menu, "整理画布", (sender, e) => {
                _ghDoc = OnPingDocument();
                _lastStatus = DoTidy();
                try {
                    var cv = Grasshopper.Instances.ActiveCanvas;
                    if (cv != null) {
                        var d = cv.Document;
                        if (d != null) {
                            d.ExpireSolution();
                            d.ScheduleSolution(1);
                        }
                        cv.Refresh();
                    }
                } catch { }
                ExpireSolution(true);
            });
        }

        protected override void SolveInstance(IGH_DataAccess DA) {
            _ghDoc = OnPingDocument();
            var cmd = Interlocked.Exchange(ref _pendingCmd, null);
            if (cmd != null) {
                _solving = true;
                _solveStart = DateTime.Now;
                try {
                    _lastStatus = Exec(cmd);
                } catch (Exception ex) {
                    _lastStatus = "exec_err:" + ex.Message;
                } finally {
                    _solving = false;
                }
                var w = Interlocked.Exchange(ref _pendingWait, null);
                if (w != null) { _pendingResult = _lastStatus; w.Set(); }
            }
            if (!_running) {
                _running = true;
                this.Message = "OK"; _lastStatus = "OK";
                _lisThread = new Thread(LisRun) { IsBackground = true };
                _lisThread.Start();
            }
            // 看门狗：如果求解超过 60 秒，强制标记为完成
            if (_solving && (DateTime.Now - _solveStart).TotalSeconds > 60) {
                _solving = false;
                _lastStatus = "timeout: solve took too long";
                var w = Interlocked.Exchange(ref _pendingWait, null);
                if (w != null) { _pendingResult = _lastStatus; w.Set(); }
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
                            case "remove":           return DoRemove(body);
                            case "disconnect":       return DoDisconnect(body);
                            case "move":             return DoMove(body);
                            case "tidy":             return DoTidy();
                            case "bake":             return DoBake();
                            case "wire":             return DoWire(body);
                            case "verify":             return DoVerify(body);
                            case "diag":              return DoDiag();
                            case "diagnose":          return DoDiagnose();
                            case "cancel":           return "{\"ok\":true,\"action\":\"cancel\"}";
                            case "cycle":            return DoCycle(body);
                            case "describe":         return DoDescribe();
                            case "screenshot":       return DoScreenshot();
                            case "canvas":           return DoCanvas();
                            case "explain":          return DoExplain(body);
                            case "query":           return DoQuery(body);
                            case "loadgh":          return DoLoadGH(body);
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
                for (int p = 18080; p < 18100; p++) {
                    try { var l = new HttpListener(); l.Prefixes.Add("http://localhost:" + p + "/"); l.Start(); _listener = l; port = p; break; } catch { }
                }
                if (port == 0) { _lastStatus = "NO PORT"; return; }
                _lastStatus = ":" + port;
                try { File.WriteAllText("D:/agents/-A-hanako/gh-bridge/port.txt", port.ToString()); } catch { }
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
                if (body.Contains("\"ping\"")) { 
                    if (_asyncResult != null) { var ar = _asyncResult; _asyncResult = null; result = ar; }
                    else { result = "{\"ok\":true}"; }
                }
                else if (body.Contains("\"describe\"")) { result = DoDescribe(); }
                else if (body.Contains("\"screenshot\"")) { result = DoScreenshot(); }
                else if (body.Contains("\"canvas\"")) { result = DoCanvas(); }
                else if (body.Contains("\"explain\"")) { result = DoExplain(body); }
                else if (body.Contains("\"remove\"")) { result = DoRemove(body); }
                else if (body.Contains("\"disconnect\"")) { result = DoDisconnect(body); }
                else if (body.Contains("\"move\"")) { result = DoMove(body); }
                else if ((body.Contains("\"build\"") || body.Contains("\"wire\"") || body.Contains("\"set\"")) && (body.Contains("\"wait\":false") || body.Contains("\"wait\": false"))) {
                    lock (_cmdLock) {
                        if (_solving) { var oldW = _pendingWait; if (oldW != null) oldW.WaitOne(5000); }
                        _pendingCmd = body; _pendingWait = null; _pendingResult = null; _asyncBuild = true;
                    }
                    try { Rhino.RhinoApp.MainApplicationWindow.Invoke((Action)(() => { try { ExpireSolution(false); var cvx = Grasshopper.Instances.ActiveCanvas; if (cvx != null) { var dx = cvx.Document; if (dx != null) dx.NewSolution(false); } } catch { } })); } catch { }
                    result = "{\"async\":true}";
                }
                else if (body.Contains("\"cancel\"")) {
                    // 强制取消当前求解
                    lock (_cmdLock) {
                        _pendingCmd = null;
                        var w = Interlocked.Exchange(ref _pendingWait, null);
                        if (w != null) { _pendingResult = "cancelled"; w.Set(); }
                        _solving = false;
                    }
                    try {
                        var cv = Grasshopper.Instances.ActiveCanvas;
                        if (cv != null) { var d = cv.Document; if (d != null) { d.ScheduleSolution(1); } }
                    } catch { }
                    result = "{\"ok\":true,\"action\":\"cancel\"}";
                }
                else {
                    // 如果上一个指令还在求解中，等待它完成或超时
                    lock (_cmdLock) {
                        if (_solving) {
                            // 上一个指令还在求解，等 5 秒看能不能完成
                            var oldW = _pendingWait;
                            if (oldW != null) oldW.WaitOne(5000);
                            if (_solving) {
                                // 还在求解，强制取消
                                _pendingCmd = null;
                                var w = Interlocked.Exchange(ref _pendingWait, null);
                                if (w != null) { _pendingResult = "prev_timeout"; w.Set(); }
                                _solving = false;
                            }
                        }
                        _pendingCmd = body;
                        var w2 = new ManualResetEvent(false);
                        _pendingWait = w2; _pendingResult = null;
                        try {
                            Rhino.RhinoApp.MainApplicationWindow.Invoke((Action)(() => {
                                try {
                                    ExpireSolution(false);
                                    var cv = Grasshopper.Instances.ActiveCanvas;
                                    if (cv != null) { var d = cv.Document; if (d != null) d.NewSolution(false); }
                                } catch { }
                            }));
                        } catch { }
                        if (w2.WaitOne(60000)) result = _pendingResult ?? "nope";
                        else {
                            // 60 秒超时，强制取消
                            _pendingCmd = null;
                            Interlocked.Exchange(ref _pendingWait, null);
                            _solving = false;
                            result = "{\"error\":\"timeout: solve took >60s\",\"action\":\"timeout\"}";
                        }
                    }
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
            var obj = TryResolveComponent(guid); if (obj == null) return "not found";
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
                            details.Add(new { name, guid = obj.InstanceGuid.ToString(), inputs = inCount, connected = inConnected, outputs = outCount });
                    } catch { }
                }
                // Build guid->name map and collect wire connections
                var guidMap = new Dictionary<Guid, string>();
                var wireConnections = new List<object>();
                foreach (var obj in doc.Objects) {
                    string cname = "?";
                    try { cname = obj.NickName ?? obj.GetType().Name; } catch { cname = obj.GetType().Name; }
                    guidMap[obj.InstanceGuid] = cname;
                }
                foreach (var obj in doc.Objects) {
                    try {
                        var p = obj.GetType().GetProperty("Params").GetValue(obj, null);
                        if (p == null) continue;
                        dynamic dp = p;
                        var inputList = (IList)dp.Input;
                        if (inputList == null) continue;
                        string toName = "?";
                        try { toName = obj.NickName ?? obj.GetType().Name; } catch { toName = obj.GetType().Name; }
                        for (int i = 0; i < inputList.Count; i++) {
                            try {
                                var pIn = (IGH_Param)inputList[i];
                                if (pIn.Sources == null) continue;
                                foreach (var src in pIn.Sources) {
                                    try {
                                        string fromName = "?";
                                        if (guidMap.ContainsKey(src.InstanceGuid)) fromName = guidMap[src.InstanceGuid];
                                        wireConnections.Add(new { from = fromName, to = toName, fromPort = src.NickName ?? "?", toPort = pIn.NickName ?? "?" });
                                    } catch { }
                                }
                            } catch { }
                        }
                    } catch { }
                }

                return _json.Serialize(new {
                    totalComponents = totalComps,
                    totalInputs = totalIns,
                    connectedInputs = connectedIns,
                    unconnectedInputs = totalIns - connectedIns,
                    totalOutputs = totalOuts,
                    details,
                    wires = wireConnections
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
                    var obj = TryResolveComponent(guid);
                    if (obj == null) return "missing:" + guid;
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
                    // Slider 范围必须在 AddObject 之后设，之前 Slider 属性为 null
                    if (cp.ContainsKey("val")) {
                        try {
                            double v = Convert.ToDouble(cp["val"]);
                            var slider = obj as Grasshopper.Kernel.Special.GH_NumberSlider;
                            if (slider != null) {
                                double min = cp.ContainsKey("min") ? Convert.ToDouble(cp["min"]) : Math.Min(0, v);
                                double max = cp.ContainsKey("max") ? Convert.ToDouble(cp["max"]) : Math.Max(v > 1000 ? v : 1000, v * 2);
                                slider.Slider.Minimum = (decimal)min;
                                slider.Slider.Maximum = (decimal)max;
                                slider.Slider.DecimalPlaces = 1;
                                slider.SetSliderValue((decimal)v);
                            } else {
                                dynamic d = obj;
                                d.Value = v;
                            }
                        } catch { }
                    }
                }
                if (def.ContainsKey("wires")) {
                    var wires = (ArrayList)def["wires"];
                    for (int wi = 0; wi < wires.Count; wi++) {
                        var wRaw = wires[wi];
                        IList wl;
                        // 支持箭头语法: "r→cir.R" 或 "r.Number→cir.R"
                        if (wRaw is string || wRaw.GetType() == typeof(string)) {
                            var arrow = ParseArrowWire((string)wRaw);
                            if (arrow == null) continue;
                            wl = new ArrayList { arrow.Item1, arrow.Item2, arrow.Item3, arrow.Item4 };
                        } else {
                            wl = (IList)wRaw;
                        }
                        string fromId = (string)wl[0]; string toId = (string)wl[2];
                        int fromOut = ResolvePortIndex(wl[1], created, fromId, false);
                        int toIn = ResolvePortIndex(wl[3], created, toId, true);
                        if (fromOut < 0 || toIn < 0) continue;
                        // 先从本次 build 的 created 字典找，再从画布上已有的组件找
                        object fromObj = null, toObj = null;
                        if (created.ContainsKey(fromId)) fromObj = created[fromId];
                        else if (_idMap.ContainsKey(fromId)) { foreach (var obj in doc.Objects) { try { if (obj.InstanceGuid == _idMap[fromId]) { fromObj = obj; break; } } catch { } } }
                        if (created.ContainsKey(toId)) toObj = created[toId];
                        else if (_idMap.ContainsKey(toId)) { foreach (var obj in doc.Objects) { try { if (obj.InstanceGuid == _idMap[toId]) { toObj = obj; break; } } catch { } } }
                        if (fromObj == null || toObj == null) continue;
                        try {
                            var fromObj2 = fromObj; var toObj2 = toObj;
                            // 获取源参数：如果 fromObj2 自身就是 IGH_Param（如 Number Slider），直接用它
                            // 否则从 Params.Output 里取
                            IGH_Param srcParam;
                            if (fromObj2 is IGH_Param) {
                                srcParam = (IGH_Param)fromObj2;
                            } else {
                                var fromP = fromObj2.GetType().GetProperty("Params").GetValue(fromObj2, null);
                                dynamic fromPd = fromP;
                                srcParam = (IGH_Param)((IList)fromPd.Output)[fromOut];
                            }
                            // 目标参数
                            var toP = toObj2.GetType().GetProperty("Params").GetValue(toObj2, null);
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
                else {
                    var deps = new Dictionary<string, List<string>>();
                    foreach (var kv in created) deps[kv.Key] = new List<string>();
                    if (def.ContainsKey("wires")) { var wl4 = (ArrayList)def["wires"]; foreach (var w in wl4) { if (w is string) continue; var wl2 = (IList)w; string fid = (string)wl2[0]; string tid = (string)wl2[2]; if (deps.ContainsKey(tid)) deps[tid].Add(fid); } }
                    var depth = new Dictionary<string, int>(); int maxDepth = 0; bool changed = true;
                    while (changed) { changed = false; foreach (var id2 in created.Keys) { int maxDep = 0; bool ak = true; foreach (var dep in deps[id2]) { if (depth.ContainsKey(dep)) maxDep = Math.Max(maxDep, depth[dep] + 1); else if (created.ContainsKey(dep)) { ak = false; break; } } if (ak && (!depth.ContainsKey(id2) || depth[id2] != maxDep)) { depth[id2] = maxDep; if (maxDep > maxDepth) maxDepth = maxDep; changed = true; } } }
                    var cr = new Dictionary<int, int>(); var ir = new Dictionary<string, int>();
                    foreach (var id2 in created.Keys) { int d2 = depth.ContainsKey(id2) ? depth[id2] : maxDepth + 1; if (!cr.ContainsKey(d2)) cr[d2] = 0; ir[id2] = cr[d2]; cr[d2]++; }
                    foreach (var id2 in created.Keys) { int col = depth.ContainsKey(id2) ? depth[id2] : maxDepth + 1; int row = ir[id2]; float x2 = col * 250f; float y2 = row * 60f; try { dynamic d = created[id2]; d.Attributes.Pivot = new PointF(x2, y2); } catch { } }
                }
                // 记录创建的组件 GUID，方便后续 wire 指令引用
                var idMap = new List<object>();
                foreach (var kv in created) {
                    try {
                        var obj = (IGH_DocumentObject)kv.Value;
                        idMap.Add(new { id = kv.Key, guid = obj.InstanceGuid.ToString() });
                        _idMap[kv.Key] = obj.InstanceGuid;
                    } catch { }
                }
                // 标记全部为 dirty，配合 cycle 指令逐层传播
                try { doc.ExpireSolution(); } catch { }
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

        // ==== REMOVE ====
        string DoRemove(string body) {
            var doc = _ghDoc; if (doc == null) return "no doc";
            try {
                var def = _json.Deserialize<Dictionary<string, object>>(body);
                int count = 0;
                if (def != null && def.ContainsKey("guids")) {
                    var arr = (ArrayList)def["guids"];
                    var toRemove = new List<IGH_DocumentObject>();
                    foreach (string g in arr) {
                        foreach (var obj in doc.Objects) {
                            if (obj is HanakoBridgeComponent) continue;
                            if (obj.InstanceGuid.ToString().StartsWith(g))
                                toRemove.Add(obj);
                        }
                    }
                    foreach (var obj in toRemove) {
                        try { doc.RemoveObject(obj, false); count++; } catch { }
                    }
                }
                return "removed:" + count;
            } catch (Exception ex) { return "remove err:" + ex.Message; }
        }

        // ==== DISCONNECT ====
        string DoDisconnect(string body) {
            var doc = _ghDoc; if (doc == null) return "no doc";
            try {
                var def = _json.Deserialize<Dictionary<string, object>>(body);
                if (!def.ContainsKey("wires")) return "need wires";
                var wires = (ArrayList)def["wires"];
                int count = 0;
                foreach (var w in wires) {
                    var wl = (IList)w;
                    string fromGuid = (string)wl[0];
                    string toGuid = (string)wl[2];
                    int toPort = Convert.ToInt32(wl[3]);
                    IGH_DocumentObject toObj = null;
                    foreach (var obj in doc.Objects)
                        if (obj.InstanceGuid.ToString() == toGuid) { toObj = obj; break; }
                    if (toObj == null) continue;
                    try {
                        var toP = toObj.GetType().GetProperty("Params").GetValue(toObj, null);
                        dynamic toPd = toP;
                        var toParam = (IGH_Param)((IList)toPd.Input)[toPort];
                        // 找到匹配的来源并移除
                        for (int s = toParam.SourceCount - 1; s >= 0; s--) {
                            var src = toParam.Sources[s];
                            // 找 src 的父组件 GUID
                            foreach (var o2 in doc.Objects) {
                                try {
                                    var pp = o2.GetType().GetProperty("Params").GetValue(o2, null);
                                    if (pp != null) { dynamic dpp = pp; var ol = (IList)dpp.Output;
                                        if (ol != null) for (int oj = 0; oj < ol.Count; oj++)
                                            if (((IGH_Param)ol[oj]).InstanceGuid == src.InstanceGuid && o2.InstanceGuid.ToString() == fromGuid) {
                                                toParam.RemoveSource(src); count++; break;
                                            } } } catch { }
                            }
                        }
                    } catch { }
                }
                return "disconnected:" + count;
            } catch (Exception ex) { return "disconnect err:" + ex.Message; }
        }

        // ==== MOVE ====
        string DoMove(string body) {
            var doc = _ghDoc; if (doc == null) return "no doc";
            try {
                var def = _json.Deserialize<Dictionary<string, object>>(body);
                if (!def.ContainsKey("positions")) return "need positions";
                var posDict = (Dictionary<string, object>)def["positions"];
                int count = 0;
                foreach (var kv in posDict) {
                    string guid = kv.Key;
                    var pos = (IList)kv.Value;
                    float x = Convert.ToSingle(pos[0]);
                    float y = Convert.ToSingle(pos[1]);
                    foreach (var obj in doc.Objects) {
                        if (obj.InstanceGuid.ToString().StartsWith(guid)) {
                            try { dynamic d = obj; d.Attributes.Pivot = new PointF(x, y); count++; } catch { }
                        }
                    }
                }
                return "moved:" + count;
            } catch (Exception ex) { return "move err:" + ex.Message; }
        }

        // ==== TIDY ====
        // 重新按拓扑排序整理所有组件位置
        string DoTidy() {
            var doc = _ghDoc; if (doc == null) return "no doc";
            try {
                var comps = new List<IGH_DocumentObject>();
                foreach (var obj in doc.Objects)
                    if (!(obj is HanakoBridgeComponent)) comps.Add(obj);
                if (comps.Count == 0) return "tidy:0";
                int n = comps.Count;
                // guid → index, index → guid
                var idxMap = new Dictionary<Guid, int>();
                for (int i = 0; i < n; i++) idxMap[comps[i].InstanceGuid] = i;
                // dep[i] = 依赖的组件索引列表
                var dep = new List<int>[n];
                for (int i = 0; i < n; i++) dep[i] = new List<int>();
                for (int i = 0; i < n; i++) {
                    var obj = comps[i];
                    // 读取所有输入口的 Sources
                    var sources = new List<Guid>();
                    try {
                        if (obj is IGH_Param) {
                            var self = (IGH_Param)obj;
                            for (int s = 0; s < self.SourceCount; s++)
                                sources.Add(self.Sources[s].InstanceGuid);
                        }
                        var pp = obj.GetType().GetProperty("Params").GetValue(obj, null);
                        if (pp != null) {
                            dynamic dp = pp;
                            var il = (IList)dp.Input;
                            if (il != null)
                                for (int j = 0; j < il.Count; j++) {
                                    var pi = (IGH_Param)il[j];
                                    for (int s = 0; s < pi.SourceCount; s++)
                                        sources.Add(pi.Sources[s].InstanceGuid);
                                }
                        }
                    } catch { }
                    // 把 Source GUID 映射到组件索引
                    foreach (var srcGuid in sources) {
                        foreach (var o2 in comps) {
                            try {
                                if (o2.InstanceGuid == srcGuid) { dep[i].Add(idxMap[o2.InstanceGuid]); break; }
                                var pp2 = o2.GetType().GetProperty("Params").GetValue(o2, null);
                                if (pp2 != null) {
                                    dynamic dp2 = pp2; var ol = (IList)dp2.Output;
                                    if (ol != null)
                                        for (int oj = 0; oj < ol.Count; oj++)
                                            if (((IGH_Param)ol[oj]).InstanceGuid == srcGuid)
                                                { dep[i].Add(idxMap[o2.InstanceGuid]); break; }
                                }
                            } catch { }
                        }
                    }
                }
                // 拓扑排序：计算深度
                var depth = new int[n];
                var inDegree = new int[n];
                for (int i = 0; i < n; i++)
                    foreach (var d in dep[i]) inDegree[i]++;
                var queue = new Queue<int>();
                for (int i = 0; i < n; i++)
                    if (inDegree[i] == 0) queue.Enqueue(i);
                int maxDepth = 0;
                while (queue.Count > 0) {
                    int u = queue.Dequeue();
                    // 找所有依赖 u 的组件
                    for (int v = 0; v < n; v++) {
                        if (dep[v].Contains(u)) {
                            depth[v] = Math.Max(depth[v], depth[u] + 1);
                            maxDepth = Math.Max(maxDepth, depth[v]);
                            inDegree[v]--;
                            if (inDegree[v] == 0) queue.Enqueue(v);
                        }
                    }
                }
                // 按深度分列，列内按原始Y排序
                var cols = new List<IGH_DocumentObject>[maxDepth + 2];
                for (int d = 0; d <= maxDepth + 1; d++) cols[d] = new List<IGH_DocumentObject>();
                for (int i = 0; i < n; i++) {
                    int d = depth[i];
                    if (d > maxDepth) d = maxDepth + 1;
                    cols[d].Add(comps[i]);
                }
                int count = 0;
                float colX = 50f;
                for (int d = 0; d <= maxDepth + 1; d++) {
                    if (cols[d].Count == 0) continue;
                    // 列内按当前Y排序
                    cols[d].Sort((a, b) => {
                        float ay = 0, by = 0;
                        try { ay = a.Attributes.Pivot.Y; } catch { }
                        try { by = b.Attributes.Pivot.Y; } catch { }
                        return ay.CompareTo(by);
                    });
                    float curY = 50f;
                    foreach (var obj in cols[d]) {
                        try { dynamic dd = obj; dd.Attributes.Pivot = new PointF(colX, curY); count++; } catch { }
                        curY += 90f;
                    }
                    colX += 250f;
                }
                return "tidy:" + count;
            } catch (Exception ex) { return "tidy err:" + ex.Message; }
        }

        string DoClear() {
            var doc = _ghDoc;
            if (doc == null) return "no doc";
            try {
                var myGuid = ComponentGuid;
                var toRemove = new List<IGH_DocumentObject>();
                foreach (var obj in doc.Objects) {
                    // 保留 Hanako 桥本身（用 GUID 判断）
                    try { if (obj.InstanceGuid == InstanceGuid) continue; } catch { }
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

        string DoQuery(string body) {
            try {
                var rhinoDoc = Rhino.RhinoDoc.ActiveDoc;
                if (rhinoDoc == null) return "{\"error\":\"no rhino doc\"}";
                var objs = rhinoDoc.Objects;
                var sb = new System.Text.StringBuilder();
                sb.Append("{\"objects\":[");
                int i = 0;
                foreach (var obj in objs) {
                    try {
                        var geo = obj.Geometry;
                        if (geo == null) continue;
                        string type = geo.GetType().Name;
                        var bbox = geo.GetBoundingBox(true);
                        bool valid = geo.IsValid;
                        if (i > 0) sb.Append(",");
                        sb.Append("{\"type\":\"" + type + "\"");
                        sb.Append(",\"valid\":" + valid.ToString().ToLower());
                        sb.Append(",\"bbox\":[" +
                            Math.Round(bbox.Min.X,1) + "," + Math.Round(bbox.Min.Y,1) + "," + Math.Round(bbox.Min.Z,1) + "," +
                            Math.Round(bbox.Max.X,1) + "," + Math.Round(bbox.Max.Y,1) + "," + Math.Round(bbox.Max.Z,1) + "]");
                        sb.Append(",\"dim\":[" + Math.Round(bbox.Max.X - bbox.Min.X,1) + "," + Math.Round(bbox.Max.Y - bbox.Min.Y,1) + "," + Math.Round(bbox.Max.Z - bbox.Min.Z,1) + "]}");
                        i++;
                    } catch { }
                }
                sb.Append("],\"count\":" + i + "}");
                return sb.ToString();
            } catch (Exception ex) { return "{\"error\":\"" + ex.Message + "\"}"; }
        }

        // ==== LOADGH ====
        string DoLoadGH(string body) {
            try {
                var def = _json.Deserialize<Dictionary<string, object>>(body);
                if (def == null || !def.ContainsKey("path")) return "{\"error\":\"need path\"}";
                string path = (string)def["path"];
                if (!File.Exists(path)) return "{\"error\":\"file not found\"}";

                var bytes = File.ReadAllBytes(path);
                var text = System.Text.Encoding.Unicode.GetString(bytes);

                var assemblies = new Dictionary<string, int>();
                int pos = 0;
                while (pos < text.Length) {
                    int found = text.IndexOf(".dll", pos, StringComparison.OrdinalIgnoreCase);
                    if (found < 0) break;
                    int start = found - 1;
                    while (start >= 0 && (char.IsLetterOrDigit(text[start]) || text[start] == '_' || text[start] == '.')) start--;
                    start++;
                    string name = text.Substring(start, found - start);
                    if (name.Length > 2 && !name.Contains("\\")) {
                        if (assemblies.ContainsKey(name)) assemblies[name]++;
                        else assemblies[name] = 1;
                    }
                    pos = found + 4;
                }
                pos = 0;
                while (pos < text.Length) {
                    int found = text.IndexOf(".gha", pos, StringComparison.OrdinalIgnoreCase);
                    if (found < 0) break;
                    int start = found - 1;
                    while (start >= 0 && (char.IsLetterOrDigit(text[start]) || text[start] == '_' || text[start] == '.')) start--;
                    start++;
                    string name = text.Substring(start, found - start);
                    if (name.Length > 2 && !name.Contains("\\")) {
                        if (assemblies.ContainsKey(name)) assemblies[name]++;
                        else assemblies[name] = 1;
                    }
                    pos = found + 4;
                }

                var sb = new System.Text.StringBuilder();
                sb.Append("{\"file\":\"" + Path.GetFileName(path) + "\"");
                sb.Append(",\"assemblies\":[");
                bool first = true;
                foreach (var kv in assemblies) {
                    if (!first) sb.Append(",");
                    sb.Append("{\"name\":\"" + kv.Key + "\",\"count\":" + kv.Value + "}");
                    first = false;
                }
                sb.Append("]}");
                return sb.ToString();
            } catch (Exception ex) { return "{\"error\":\"" + ex.Message + "\"}"; }
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
                    string toGuid = (string)wl[2];
                    // 按 InstanceGuid 查找画布上的组件
                    IGH_DocumentObject fromObj = null, toObj = null;
                    foreach (var obj in doc.Objects) {
                        if (obj.InstanceGuid.ToString() == fromGuid) fromObj = obj;
                        if (obj.InstanceGuid.ToString() == toGuid) toObj = obj;
                    }
                    if (fromObj == null || toObj == null) continue;
                    int fromOut = ResolvePortOnObject(wl[1], fromObj, false);
                    int toIn = ResolvePortOnObject(wl[3], toObj, true);
                    if (fromOut < 0 || toIn < 0) continue;
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
                        var obj = TryResolveComponent(guid);
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

        // ==== DIAGNOSE ====
        string DoDiagnose() {
            var doc = _ghDoc;
            if (doc == null) return _json.Serialize(new { error = "no doc" });
            try {
                // 先触发一次求解，确保几何体是最新的
                try { doc.NewSolution(false); } catch { }

                var problems = new List<object>();
                int healthyComps = 0, problemComps = 0;

                foreach (var obj in doc.Objects) {
                    if (obj is HanakoBridgeComponent) continue;

                    string compName = obj.GetType().Name;
                    string nickName = "";
                    try { dynamic d = obj; nickName = d.NickName ?? ""; } catch { }
                    string guidPrefix = obj.InstanceGuid.ToString().Substring(0, 8);
                    int pivotX = 0, pivotY = 0;
                    try { dynamic d = obj; pivotX = (int)d.Attributes.Pivot.X; pivotY = (int)d.Attributes.Pivot.Y; } catch { }

                    bool hasProblem = false;
                    var issues = new List<object>();

                    try {
                        var p = obj.GetType().GetProperty("Params").GetValue(obj, null);
                        if (p == null) continue;
                        dynamic dp = p;
                        var outs = (IList)dp.Output;
                        if (outs == null) continue;

                        for (int i = 0; i < outs.Count; i++) {
                            try {
                                var ghOut = (IGH_Param)outs[i];
                                string outName = "";
                                try { outName = ghOut.NickName ?? ""; } catch { }
                                string outType = ghOut.TypeName ?? "";

                                var data = ghOut.VolatileData;
                                if (data == null || data.IsEmpty) {
                                    // 输出为空 — 可能还没求解或输入缺失
                                    // 只有非空输入但空输出才算问题
                                    bool hasInput = false;
                                    try {
                                        var ins = (IList)dp.Input;
                                        if (ins != null) {
                                            for (int j = 0; j < ins.Count; j++) {
                                                try { if (((IGH_Param)ins[j]).SourceCount > 0) { hasInput = true; break; } } catch { }
                                            }
                                        }
                                    } catch { }
                                    if (hasInput) {
                                        hasProblem = true;
                                        issues.Add(new { output = i, name = outName, type = outType, problem = "empty_output", detail = "有输入但输出为空" });
                                    }
                                    continue;
                                }

                                // 检查每条数据
                                int itemIdx = 0;
                                foreach (var item in data.AllData(true)) {
                                    try {
                                        if (item == null) {
                                            hasProblem = true;
                                            issues.Add(new { output = i, name = outName, item = itemIdx, problem = "null_item", detail = "数据项为 null" });
                                            itemIdx++;
                                            continue;
                                        }

                                        var valProp = item.GetType().GetProperty("Value");
                                        if (valProp == null) { itemIdx++; continue; }
                                        var val = valProp.GetValue(item, null);
                                        if (val == null) {
                                            hasProblem = true;
                                            issues.Add(new { output = i, name = outName, item = itemIdx, problem = "null_value", detail = "Value 为 null" });
                                            itemIdx++;
                                            continue;
                                        }

                                        // 根据类型检查退化
                                        string typeName = val.GetType().Name;

                                        if (val is Rhino.Geometry.Point3d) {
                                            var pt = (Rhino.Geometry.Point3d)val;
                                            if (pt.X == 0 && pt.Y == 0 && pt.Z == 0) {
                                                // 点在原点 — 可能是未初始化，标记为可疑
                                                // 不直接报错，因为原点可能是合法的
                                            }
                                        }
                                        else if (val is Rhino.Geometry.Point3f) {
                                            var pt = (Rhino.Geometry.Point3f)val;
                                            if (pt.X == 0 && pt.Y == 0 && pt.Z == 0) {
                                                // 同上
                                            }
                                        }
                                        else if (val is Rhino.Geometry.Curve) {
                                            var crv = (Rhino.Geometry.Curve)val;
                                            if (crv.GetLength() < 1e-6) {
                                                hasProblem = true;
                                                issues.Add(new { output = i, name = outName, item = itemIdx, problem = "zero_length_curve", detail = "曲线长度=" + crv.GetLength().ToString("E2"), length = Math.Round(crv.GetLength(), 6) });
                                            }
                                        }
                                        else if (val is Rhino.Geometry.Surface) {
                                            var srf = (Rhino.Geometry.Surface)val;
                                            var mass = Rhino.Geometry.AreaMassProperties.Compute(srf);
                                            if (mass == null || mass.Area < 1e-6) {
                                                hasProblem = true;
                                                issues.Add(new { output = i, name = outName, item = itemIdx, problem = "zero_area_surface", detail = "面积=" + (mass?.Area.ToString("E2") ?? "null") });
                                            }
                                        }
                                        else if (val is Rhino.Geometry.Brep) {
                                            var brep = (Rhino.Geometry.Brep)val;
                                            var mass = Rhino.Geometry.AreaMassProperties.Compute(brep);
                                            if (mass == null || mass.Area < 1e-6) {
                                                hasProblem = true;
                                                issues.Add(new { output = i, name = outName, item = itemIdx, problem = "zero_area_brep", detail = "Brep 面积=" + (mass?.Area.ToString("E2") ?? "null") });
                                            }
                                            if (brep.Faces.Count == 0) {
                                                hasProblem = true;
                                                issues.Add(new { output = i, name = outName, item = itemIdx, problem = "empty_brep", detail = "Brep 无面" });
                                            }
                                        }
                                        else if (val is Rhino.Geometry.Mesh) {
                                            var mesh = (Rhino.Geometry.Mesh)val;
                                            if (mesh.Faces.Count == 0) {
                                                hasProblem = true;
                                                issues.Add(new { output = i, name = outName, item = itemIdx, problem = "empty_mesh", detail = "Mesh 无面" });
                                            }
                                        }
                                        else if (val is Rhino.Geometry.Line) {
                                            var line = (Rhino.Geometry.Line)val;
                                            if (line.Length < 1e-6) {
                                                hasProblem = true;
                                                issues.Add(new { output = i, name = outName, item = itemIdx, problem = "zero_length_line", detail = "线段长度=" + line.Length.ToString("E2") });
                                            }
                                        }
                                        else if (val is Rhino.Geometry.Circle) {
                                            var circ = (Rhino.Geometry.Circle)val;
                                            if (circ.Radius < 1e-6) {
                                                hasProblem = true;
                                                issues.Add(new { output = i, name = outName, item = itemIdx, problem = "zero_radius_circle", detail = "圆半径=" + circ.Radius.ToString("E2") });
                                            }
                                        }
                                        else if (val is Rhino.Geometry.Plane) {
                                            // 退化平面：法向量为零
                                            var pl = (Rhino.Geometry.Plane)val;
                                            if (pl.ZAxis.Length < 1e-6) {
                                                hasProblem = true;
                                                issues.Add(new { output = i, name = outName, item = itemIdx, problem = "degenerate_plane", detail = "平面法向量退化" });
                                            }
                                        }
                                    } catch { }
                                    itemIdx++;
                                }
                            } catch { }
                        }
                    } catch { }

                    if (hasProblem) {
                        problemComps++;
                        problems.Add(new {
                            component = compName,
                            nickname = nickName,
                            guid = guidPrefix,
                            position = new { x = pivotX, y = pivotY },
                            issues
                        });
                    } else {
                        healthyComps++;
                    }
                }

                return _json.Serialize(new {
                    result = "diagnose",
                    totalComponents = problemComps + healthyComps,
                    healthy = healthyComps,
                    problems = problemComps,
                    details = problems
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
        // ==== CANVAS ====
        string DoCanvas()
        {
            var doc = _ghDoc;
            if (doc == null) return "{\"error\":\"no doc\"}";
            try
            {
                var comps = new List<object>();
                foreach (var obj in doc.Objects)
                {
                    if (obj is HanakoBridgeComponent) continue;
                    string name = "?", tn = "?";
                    try { name = obj.NickName ?? obj.GetType().Name; } catch { name = obj.GetType().Name; }
                    try { tn = obj.GetType().Name; } catch { }
                    string g = obj.InstanceGuid.ToString();
                    double x = 0, y = 0;
                    try { var a = obj.Attributes; if (a != null) { x = a.Pivot.X; y = a.Pivot.Y; } } catch { }
                    var ins = new List<object>();
                    var outs = new List<object>();
                    try
                    {
                        var p = obj.GetType().GetProperty("Params").GetValue(obj, null);
                        if (p != null)
                        {
                            dynamic dp = p;
                            var il = (IList)dp.Input;
                            if (il != null)
                                for (int i = 0; i < il.Count; i++)
                                {
                                    var pi = (IGH_Param)il[i];
                                    string iname = "?"; try { iname = pi.NickName ?? ("in" + i); } catch { }
                                    ins.Add(new { port = i, name = iname, type = pi.TypeName ?? "?", connected = pi.SourceCount > 0 });
                                }
                            var ol = (IList)dp.Output;
                            if (ol != null)
                                for (int j = 0; j < ol.Count; j++)
                                {
                                    var po = (IGH_Param)ol[j];
                                    string oname = "?"; try { oname = po.NickName ?? ("out" + j); } catch { }
                                    int tc = 0;
                                    foreach (var o2 in doc.Objects)
                                    {
                                        try
                                        {
                                            var p2 = o2.GetType().GetProperty("Params").GetValue(o2, null);
                                            if (p2 != null) { dynamic dp2 = p2; var i2 = (IList)dp2.Input; if (i2 != null) for (int k = 0; k < i2.Count; k++) { var pi2 = (IGH_Param)i2[k]; if (pi2.SourceCount > 0) for (int s = 0; s < pi2.SourceCount; s++) if (pi2.Sources[s].InstanceGuid == po.InstanceGuid) tc++; } }
                                        }
                                        catch { }
                                    }
                                    outs.Add(new { port = j, name = oname, connected = tc > 0, targetCount = tc });
                                }
                        }
                    }
                    catch { }
                    comps.Add(new { name, type = tn, guid = g, x, y, inputs = ins, outputs = outs });
                }
                return _json.Serialize(new { totalComponents = comps.Count, components = comps });
            }
            catch (Exception ex) { return "canvas err:" + ex.Message; }
        }

        // ==== EXPLAIN ====
        string DoExplain(string body)
        {
            var doc = _ghDoc; if (doc == null) return "no doc";
            try
            {
                var def = _json.Deserialize<Dictionary<string, object>>(body);
                var gl = new List<string>();
                if (def != null && def.ContainsKey("guids")) { var a = (ArrayList)def["guids"]; foreach (string s in a) gl.Add(s); }
                if (gl.Count == 0) return "need guids";
                var sel = new List<IGH_DocumentObject>(); var sgs = new HashSet<Guid>();
                foreach (var o in doc.Objects) { string ig = o.InstanceGuid.ToString(); foreach (var g2 in gl) if (ig.StartsWith(g2)) { sel.Add(o); sgs.Add(o.InstanceGuid); break; } }
                if (sel.Count == 0) return "no match";
                var nm = new Dictionary<Guid, string>();
                foreach (var o in sel) { string n = "?"; try { n = o.NickName; if (string.IsNullOrEmpty(n)) n = o.GetType().Name; } catch { n = o.GetType().Name; } nm[o.InstanceGuid] = n; }
                var sb = new StringBuilder(); sb.Append("选中组件共" + sel.Count + "个。\n");
                var ino = new List<string>(); var outo = new List<string>();
                foreach (var o in sel)
                {
                    try
                    {
                        var p = o.GetType().GetProperty("Params").GetValue(o, null); if (p == null) continue;
                        dynamic dp = p;
                        var il = (IList)dp.Input;
                        if (il != null)
                            for (int i = 0; i < il.Count; i++)
                            {
                                var pi = (IGH_Param)il[i]; string pn = "?"; try { pn = pi.NickName ?? ("in" + i); } catch { }
                                if (pi.SourceCount == 0) ino.Add(nm[o.InstanceGuid] + "." + pn + "(未连线)");
                                else
                                {
                                    bool fs = false;
                                    for (int s = 0; s < pi.SourceCount; s++)
                                    {
                                        var sg = pi.Sources[s].InstanceGuid;
                                        if (sgs.Contains(sg))
                                        {
                                            fs = true; string sn = "?";
                                            foreach (var o2 in doc.Objects)
                                            {
                                                try { var pp = o2.GetType().GetProperty("Params").GetValue(o2, null); if (pp != null) { dynamic dpp = pp; var ol2 = (IList)dpp.Output; if (ol2 != null) for (int oj = 0; oj < ol2.Count; oj++) if (((IGH_Param)ol2[oj]).InstanceGuid == sg) { try { sn = o2.NickName ?? o2.GetType().Name; } catch { } break; } } } catch { }
                                            }
                                            sb.Append("  " + sn + ".out → " + nm[o.InstanceGuid] + "." + pn + "\n");
                                        }
                                    }
                                    if (!fs) { string sn = "外部"; try { var src = pi.Sources[0]; foreach (var o2 in doc.Objects) { try { var pp = o2.GetType().GetProperty("Params").GetValue(o2, null); if (pp != null) { dynamic dpp = pp; var ol2 = (IList)dpp.Output; if (ol2 != null) for (int oj = 0; oj < ol2.Count; oj++) if (((IGH_Param)ol2[oj]).InstanceGuid == src.InstanceGuid) { try { sn = o2.NickName ?? o2.GetType().Name; } catch { } break; } } } catch { } } } catch { } ino.Add(nm[o.InstanceGuid] + "." + pn + "(←" + sn + ")"); }
                                }
                            }
                        var ol3 = (IList)dp.Output;
                        if (ol3 != null)
                            for (int j = 0; j < ol3.Count; j++)
                            {
                                var po = (IGH_Param)ol3[j]; string on = "?"; try { on = po.NickName ?? ("out" + j); } catch { }
                                bool any = false;
                                foreach (var o2 in doc.Objects) { try { var p2 = o2.GetType().GetProperty("Params").GetValue(o2, null); if (p2 != null) { dynamic dp2 = p2; var i2 = (IList)dp2.Input; if (i2 != null) for (int k = 0; k < i2.Count; k++) { var pi2 = (IGH_Param)i2[k]; if (pi2.SourceCount > 0) for (int s = 0; s < pi2.SourceCount; s++) if (pi2.Sources[s].InstanceGuid == po.InstanceGuid) { any = true; break; } } } } catch { } if (any) break; }
                                if (!any) outo.Add(nm[o.InstanceGuid] + "." + on + "(未连线)");
                            }
                    }
                    catch { }
                }
                if (ino.Count > 0) { sb.Append("\n外部输入/未连线端口：\n"); foreach (var s in ino) sb.Append("  ◇ " + s + "\n"); }
                if (outo.Count > 0) { sb.Append("\n外部输出/未连线端口：\n"); foreach (var s in outo) sb.Append("  ◇ " + s + "\n"); }
                return sb.ToString();
            }
            catch (Exception ex) { return "explain err:" + ex.Message; }
        }

        // ==== CYCLE ====
        string DoCycle(string body) {
            int count = 5;
            try {
                var d = _json.Deserialize<Dictionary<string, object>>(body);
                if (d != null && d.ContainsKey("count")) count = Convert.ToInt32(d["count"]);
            } catch { }
            var doc = _ghDoc; if (doc == null) return "{\"error\":\"no doc\"}";
            for (int i = 0; i < count; i++) {
                doc.ScheduleSolution(1 + i * 2, (d2) => { try { d2.NewSolution(false); } catch { } });
            }
            return "{\"ok\":true,\"cycles\":" + count + "}";
        }

        // ==== DESCRIBE ====
        string DoDescribe()
        {
            try
            {
                DoBake();
                var rDoc = Rhino.RhinoDoc.ActiveDoc;
                if (rDoc == null) return _json.Serialize(new { error = "no rhino doc" });
                var results = new List<object>();
                foreach (var obj in rDoc.Objects)
                {
                    var geo = obj.Geometry; if (geo == null) continue;
                    var d = new Dictionary<string, object>();
                    d["type"] = geo.GetType().Name;
                    var bb = geo.GetBoundingBox(true);
                    if (bb.IsValid)
                    {
                        double dx = bb.Max.X - bb.Min.X, dy = bb.Max.Y - bb.Min.Y, dz = bb.Max.Z - bb.Min.Z;
                        d["size"] = new double[] { Math.Round(dx, 2), Math.Round(dy, 2), Math.Round(dz, 2) };
                        double maxD = Math.Max(dx, Math.Max(dy, dz)), minD = Math.Min(dx, Math.Min(dy, dz));
                        if (maxD > minD * 5) d["shape"] = "细长/管状";
                        else if (maxD < minD * 1.5) d["shape"] = "块状/近立方体";
                        else d["shape"] = "扁平/片状";
                        if (maxD > 0.1)
                        {
                            int ma = dx >= dy && dx >= dz ? 0 : (dy >= dz ? 1 : 2);
                            double[] dims = { dx, dy, dz };
                            int samples = Math.Min(10, Math.Max(3, (int)(dims[ma] / 0.5)));
                            var radii = new List<double>();
                            for (int s = 0; s <= samples; s++) radii.Add(Math.Round(Math.Sqrt(dims[(ma + 1) % 3] * dims[(ma + 2) % 3] / Math.PI) / 2, 3));
                            if (radii.Count >= 3 && radii.Average() > 0.001)
                            {
                                double rf = radii[0], rm = radii[radii.Count / 2], rl = radii[radii.Count - 1], avg = radii.Average();
                                if (Math.Abs(rf - rl) < avg * 0.1) d["taper"] = "等径";
                                else if (rm > rf * 1.2 && rm > rl * 1.2) d["taper"] = "中间粗两端细";
                                else if (rf < rl) d["taper"] = "渐粗（单向）";
                                else d["taper"] = "渐细（单向）";
                                d["radius_range"] = new double[] { radii.Min(), radii.Max() };
                            }
                        }
                    }
                    if (geo is Rhino.Geometry.Brep) try { d["volume"] = Math.Round(((Rhino.Geometry.Brep)geo).GetVolume(), 4); } catch { }
                    if (geo is Rhino.Geometry.Mesh) { try { d["vertices"] = ((Rhino.Geometry.Mesh)geo).Vertices.Count; } catch { } try { d["faces"] = ((Rhino.Geometry.Mesh)geo).Faces.Count; } catch { } }
                    if (geo is Rhino.Geometry.Curve) try { d["length"] = Math.Round(((Rhino.Geometry.Curve)geo).GetLength(), 2); } catch { }
                    results.Add(d);
                }
                return _json.Serialize(new { total = results.Count, results });
            }
            catch (Exception ex) { return _json.Serialize(new { error = ex.Message }); }
        }

        // ==== SCREENSHOT ====
        string DoScreenshot()
        {
            try
            {
                var rDoc = Rhino.RhinoDoc.ActiveDoc;
                if (rDoc == null) return _json.Serialize(new { error = "no rhino doc" });
                var v = rDoc.Views.ActiveView;
                if (v == null) return _json.Serialize(new { error = "no view" });
                var bmp = v.CaptureToBitmap();
                if (bmp == null) return _json.Serialize(new { error = "capture failed" });
                string p = "D:/agents/-A-hanako/screenshot.png";
                bmp.Save(p, System.Drawing.Imaging.ImageFormat.Png);
                return _json.Serialize(new { ok = true, path = p, width = bmp.Width, height = bmp.Height });
            }
            catch (Exception ex) { return _json.Serialize(new { error = ex.Message }); }
        }

        // ==== 箭头语法解析: "r→cir.R" → ["r", 0, "cir", "R"] ====
        Tuple<string, object, string, object> ParseArrowWire(string arrow) {
            try {
                int arrowPos = arrow.IndexOf('→');
                if (arrowPos < 0) arrowPos = arrow.IndexOf("->");
                if (arrowPos < 0) return null;
                string left = arrow.Substring(0, arrowPos).Trim();
                string right = arrow.Substring(arrowPos + 1).Trim();
                if (right.StartsWith(">")) right = right.Substring(1);
                // 左侧: id 或 id.portName
                string fromId; object fromPort = 0;
                int dotPos = left.LastIndexOf('.');
                if (dotPos > 0) { fromId = left.Substring(0, dotPos); fromPort = left.Substring(dotPos + 1); }
                else fromId = left;
                // 右侧: id.portName
                string toId; object toPort = 0;
                dotPos = right.LastIndexOf('.');
                if (dotPos > 0) { toId = right.Substring(0, dotPos); toPort = right.Substring(dotPos + 1); }
                else toId = right;
                return Tuple.Create(fromId, fromPort, toId, toPort);
            } catch { return null; }
        }

        // ==== NAME-to-GUID ====
        IGH_DocumentObject TryResolveComponent(string input)
        {
            try { var g = new Guid(input); var r = CI(input); if (r != null) return (IGH_DocumentObject)r; } catch { }
            if (input.Length >= 8) { try { var r = CI(input); if (r != null) return (IGH_DocumentObject)r; } catch { } }
            // FindObjectByName 优先于 CompDB，防止 CompDB 映射到过时的代理
            try { var proxy = Grasshopper.Instances.ComponentServer.FindObjectByName(input, true, true); if (proxy != null) { try { return (IGH_DocumentObject)CI(proxy.Guid.ToString().Substring(0, 8)); } catch { } } } catch { }
            if (CompDB != null)
            {
                string key = input.ToLowerInvariant();
                foreach (var kv in CompDB) { string name = kv.Value; int pipe = name.IndexOf('|'); if (pipe > 0) name = name.Substring(0, pipe); if (name.ToLowerInvariant() == key) { try { return (IGH_DocumentObject)CI(kv.Key); } catch { } } }
                foreach (var kv in CompDB) { string name = kv.Value; int pipe = name.IndexOf('|'); if (pipe > 0) name = name.Substring(0, pipe); if (name.ToLowerInvariant().Contains(key)) { try { return (IGH_DocumentObject)CI(kv.Key); } catch { } } }
            }
            return null;
        }

        int ResolvePortIndex(object val, Dictionary<string, object> created, string compId, bool isInput)
        {
            if (val is int i2) return i2; if (val is long l2) return (int)l2; if (val is double d2) return (int)d2;
            string name = val.ToString(); if (int.TryParse(name, out int portNum)) return portNum;
            object compObj = null;
            if (created.ContainsKey(compId)) compObj = created[compId];
            else if (_idMap.ContainsKey(compId)) { var g3 = _idMap[compId]; foreach (var o in _ghDoc.Objects) { if (o.InstanceGuid == g3) { compObj = o; break; } } }
            if (compObj != null) return FindPortByName((IGH_DocumentObject)compObj, name, isInput);
            return -1;
        }

        int ResolvePortOnObject(object val, IGH_DocumentObject obj, bool isInput)
        {
            if (val is int i3) return i3; if (val is long l3) return (int)l3; if (val is double d3) return (int)d3;
            string name = val.ToString(); if (int.TryParse(name, out int portNum)) return portNum;
            return FindPortByName(obj, name, isInput);
        }

        int FindPortByName(IGH_DocumentObject obj, string name, bool isInput)
        {
            try
            {
                if (obj is IGH_Param) { return 0; }
                var p = obj.GetType().GetProperty("Params").GetValue(obj, null);
                if (p == null) return -1;
                dynamic dp = p;
                var list = isInput ? (IList)dp.Input : (IList)dp.Output;
                if (list == null) return -1;
                string key = name.ToLowerInvariant();
                for (int i = 0; i < list.Count; i++) { var param = (IGH_Param)list[i]; if (param.Name.ToLowerInvariant() == key) return i; if ((param.NickName ?? "").ToLowerInvariant() == key) return i; }
                for (int i = 0; i < list.Count; i++) { var param = (IGH_Param)list[i]; if (param.Name.ToLowerInvariant().Contains(key)) return i; if ((param.NickName ?? "").ToLowerInvariant().Contains(key)) return i; }
            }
            catch { }
            return -1;
        }

    }
}
