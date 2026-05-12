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
using Grasshopper.Kernel.Data;
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
        private static List<Tuple<IGH_Param, IGH_Param>> _deferredPaths = new List<Tuple<IGH_Param, IGH_Param>>();
        private static volatile bool _hasDeferredPaths;
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        public static Dictionary<string, string> ProxyDB;
        public static Dictionary<string, string> CompDB;
        private static Dictionary<string, Guid> _idMap = new Dictionary<string, Guid>();
        private static GH_Document _ghDoc;

        public HanakoBridgeComponent() : base("Hanako", "Hanako", "Bridge", "Hanako", "Bridge") { }
        public override Guid ComponentGuid { get { return new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"); } }
        protected override Bitmap Icon { get { return null; } }
        public override GH_Exposure Exposure { get { return GH_Exposure.primary; } }
        protected override void RegisterInputParams(GH_InputParamManager pM) { }
        protected override void RegisterOutputParams(GH_OutputParamManager pM) { pM.AddTextParameter("S", "S", "", GH_ParamAccess.item); }

        protected override void SolveInstance(IGH_DataAccess DA) {
            _ghDoc = OnPingDocument();
            if (_hasDeferredPaths) {
                var deferred = Interlocked.Exchange(ref _deferredPaths, new List<Tuple<IGH_Param, IGH_Param>>());
                _hasDeferredPaths = false;
                DA.SetData(0, "deferred:" + deferred.Count);
                return;
            }
            var cmd = Interlocked.Exchange(ref _pendingCmd, null);
            if (cmd != null) {
                _solving = true; _solveStart = DateTime.Now;
                try {
                    _lastStatus = Exec(cmd);
                    if (_asyncBuild) { _asyncResult = _lastStatus; _asyncBuild = false; }
                } catch (Exception ex) { _lastStatus = "exec_err:" + ex.Message; }
                _solving = false;
                var wr = Interlocked.Exchange(ref _pendingWait, null);
                if (wr != null) { _pendingResult = _lastStatus; wr.Set(); }
                if (cmd != null && cmd.Contains("\"build\"") && !_secondSolve) {
                    _secondSolve = true; _pendingCmd = null;
                    try { ExpireSolution(false); var cv = Grasshopper.Instances.ActiveCanvas; if (cv != null) { var d = cv.Document; if (d != null) d.NewSolution(false); } } catch { }
                } else { _secondSolve = false; }
            }
            if (!_running) { _running = true; this.Message = "OK"; _lastStatus = "OK"; _lisThread = new Thread(LisRun) { IsBackground = true }; _lisThread.Start(); }
            if (_solving && (DateTime.Now - _solveStart).TotalSeconds > 60) { _solving = false; _lastStatus = "timeout"; var w2 = Interlocked.Exchange(ref _pendingWait, null); if (w2 != null) { _pendingResult = _lastStatus; w2.Set(); } }
            DA.SetData(0, _lastStatus);
        }

        string Exec(string body) {
            try {
                try {
                    var def = _json.Deserialize<Dictionary<string, object>>(body);
                    if (def != null && def.ContainsKey("action")) {
                        string a = (def["action"] as string) ?? "";
                        switch (a) {
                            case "ping": return "pong";
                            case "scan": return DoScan();
                            case "inspect": return DoInspect(body);
                            case "check": return DoCheck();
                            case "builddb": return BuildDB();
                            case "qdb": return QueryDB(body);
                            case "build": return DoBuild(body);
                            case "get_levels": return DoGetLevels();
                            case "get_families": return DoGetFamilies();
                            case "get_wall_types": return DoGetWallTypes();
                            case "get_active_view": return DoGetActiveView();
                            case "wires": return DoCheckWires();
                            case "scene": return DoScene();
                            case "clear": return DoClear();
                            case "bake": return DoBake();
                            case "wire": return DoWire(body);
                            case "verify": return DoVerify(body);
                            case "diag": return DoDiag();
                            case "diagnose": return DoDiagnose();
                            case "cancel": return "{\"ok\":true,\"action\":\"cancel\"}";
                            case "query": return DoQuery(body);
                            case "describe": return DoDescribe();
                            case "screenshot": return DoScreenshot();
                            case "canvas": return DoCanvas();
                            case "explain": return DoExplain(body);
                            case "loadgh": return DoLoadGH(body);
                            case "set": return DoSet(body);
                            case "gettype": return DoGetType(body);
                            case "values": return DoValues();
                            case "createpanel": return DoCreatePanel(body);
                            case "geomcheck": return DoGeomCheck();
                            default: return "?";
                        }
                    }
                } catch { }
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
                while (true) { var ctx = _listener.GetContext(); try { Reply(ctx); } catch { try { ctx.Response.OutputStream.Close(); } catch { } } }
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
                else if (body.Contains("\"cancel\"")) {
                    lock (_cmdLock) { _pendingCmd = null; var w = Interlocked.Exchange(ref _pendingWait, null); if (w != null) { _pendingResult = "cancelled"; w.Set(); } _solving = false; }
                    try { var cv = Grasshopper.Instances.ActiveCanvas; if (cv != null) { var d = cv.Document; if (d != null) { d.ScheduleSolution(1); } } } catch { }
                    result = "{\"ok\":true,\"action\":\"cancel\"}";
                }
                else if ((body.Contains("\"build\"") || body.Contains("\"wire\"") || body.Contains("\"set\"") || body.Contains("\"describe\"")) && (body.Contains("\"wait\":false") || body.Contains("\"wait\": false"))) {
                    lock (_cmdLock) {
                        if (_solving) { var oldW = _pendingWait; if (oldW != null) oldW.WaitOne(5000); if (_solving) { _pendingCmd = null; var w = Interlocked.Exchange(ref _pendingWait, null); if (w != null) { _pendingResult = "prev_timeout"; w.Set(); } _solving = false; } }
                        _pendingCmd = body; _pendingWait = null; _pendingResult = null; _asyncBuild = true;
                    }
                    try { Rhino.RhinoApp.MainApplicationWindow.Invoke((Action)(() => { try { ExpireSolution(false); var cv = Grasshopper.Instances.ActiveCanvas; if (cv != null) { var d = cv.Document; if (d != null) d.NewSolution(false); } } catch { } })); } catch { }
                    result = "{\"async\":true}";
                }
                else {
                    lock (_cmdLock) {
                        if (_solving) { var oldW = _pendingWait; if (oldW != null) oldW.WaitOne(5000); if (_solving) { _pendingCmd = null; var w = Interlocked.Exchange(ref _pendingWait, null); if (w != null) { _pendingResult = "prev_timeout"; w.Set(); } _solving = false; } }
                        _pendingCmd = body; var w2 = new ManualResetEvent(false); _pendingWait = w2; _pendingResult = null;
                    }
                    try { Rhino.RhinoApp.MainApplicationWindow.Invoke((Action)(() => { try { ExpireSolution(false); var cv = Grasshopper.Instances.ActiveCanvas; if (cv != null) { var d = cv.Document; if (d != null) d.NewSolution(false); } } catch { } })); } catch { }
                    if (_pendingWait != null && _pendingWait.WaitOne(120000)) { result = _pendingResult ?? "nope"; }
                    else { result = "{\"async\":true}"; }
                }
            } else result = "{\"ok\":false}";
            var rb = Encoding.UTF8.GetBytes(result);
            resp.ContentType = "application/json"; resp.ContentLength64 = rb.Length;
            resp.OutputStream.Write(rb, 0, rb.Length); resp.OutputStream.Close();
        }

        // ==== BUILD ====
        string DoBuild(string body) {
            var doc = _ghDoc; if (doc == null) return "no doc";
            try {
                var def = _json.Deserialize<Dictionary<string, object>>(body);
                if (!def.ContainsKey("components")) return "need components";
                var comps = (ArrayList)def["components"];
                var created = new Dictionary<string, object>();
                foreach (var c in comps) {
                    var cp = (Dictionary<string, object>)c;
                    string id = (string)cp["id"]; string guid = (string)cp["guid"];
                    var obj = TryResolveComponent(guid);
                    if (obj == null) return "missing:" + guid;
                    if (cp.ContainsKey("nick")) { try { dynamic d = obj; d.NickName = (string)cp["nick"]; } catch { } }
                    created[id] = obj;
                    try { doc.AddObject((IGH_DocumentObject)obj, false); } catch { }
                    if (cp.ContainsKey("val")) { try { double v = Convert.ToDouble(cp["val"]); var slider = obj as Grasshopper.Kernel.Special.GH_NumberSlider; if (slider != null) { double min2 = cp.ContainsKey("min") ? Convert.ToDouble(cp["min"]) : Math.Min(0, v); double max2 = cp.ContainsKey("max") ? Convert.ToDouble(cp["max"]) : Math.Max(v > 1000 ? v : 1000, v * 2); slider.Slider.Minimum = (decimal)min2; slider.Slider.Maximum = (decimal)max2; slider.Slider.DecimalPlaces = 1; slider.SetSliderValue((decimal)v); } else { dynamic d2 = obj; d2.Value = v; } } catch { } }
                }
                if (def.ContainsKey("wires")) {
                    var wires = (ArrayList)def["wires"];
                    foreach (var w in wires) {
                        var wl = (IList)w;
                        string fromId = (string)wl[0]; string toId = (string)wl[2];
                        int fromOut = ResolvePortIndex(wl[1], created, fromId, false);
                        int toIn = ResolvePortIndex(wl[3], created, toId, true);
                        if (fromOut < 0 || toIn < 0) continue;
                        object fromObj = null, toObj = null;
                        if (created.ContainsKey(fromId)) fromObj = created[fromId];
                        if (created.ContainsKey(toId)) toObj = created[toId];
                        if (fromObj == null || toObj == null) continue;
                        try {
                            IGH_Param srcParam;
                            if (fromObj is IGH_Param) { srcParam = (IGH_Param)fromObj; }
                            else { var fromP = fromObj.GetType().GetProperty("Params").GetValue(fromObj, null); dynamic fromPd = fromP; srcParam = (IGH_Param)((IList)fromPd.Output)[fromOut]; }
                            var toP = toObj.GetType().GetProperty("Params").GetValue(toObj, null); dynamic toPd2 = toP; var toParam = (IGH_Param)((IList)toPd2.Input)[toIn];
                            if (toParam.TypeName == "Path") { lock (_cmdLock) { _deferredPaths.Add(Tuple.Create(srcParam, toParam)); _hasDeferredPaths = true; } }
                            else { toParam.AddSource(srcParam); }
                        } catch { }
                    }
                }
                if (def.ContainsKey("positions")) { var posDict = (Dictionary<string, object>)def["positions"]; foreach (var kv in posDict) { if (!created.ContainsKey(kv.Key)) continue; var pos = (IList)kv.Value; try { dynamic d = created[kv.Key]; d.Attributes.Pivot = new PointF(Convert.ToSingle(pos[0]), Convert.ToSingle(pos[1])); } catch { } } }
                else {
                    var deps = new Dictionary<string, List<string>>(); foreach (var kv in created) deps[kv.Key] = new List<string>();
                    if (def.ContainsKey("wires")) { var wlForLayout = (ArrayList)def["wires"]; foreach (var w in wlForLayout) { var wl = (IList)w; string fid = (string)wl[0]; string tid = (string)wl[2]; if (deps.ContainsKey(tid)) deps[tid].Add(fid); } }
                    var depth = new Dictionary<string, int>(); int maxDepth = 0; bool changed = true;
                    while (changed) { changed = false; foreach (var id2 in created.Keys) { int maxDep = 0; bool allKnown = true; foreach (var dep in deps[id2]) { if (depth.ContainsKey(dep)) maxDep = Math.Max(maxDep, depth[dep] + 1); else if (created.ContainsKey(dep)) { allKnown = false; break; } } if (allKnown && (!depth.ContainsKey(id2) || depth[id2] != maxDep)) { depth[id2] = maxDep; if (maxDep > maxDepth) maxDepth = maxDep; changed = true; } } }
                    var colRows = new Dictionary<int, int>(); var idRow = new Dictionary<string, int>();
                    foreach (var id2 in created.Keys) { int d2 = depth.ContainsKey(id2) ? depth[id2] : maxDepth + 1; if (!colRows.ContainsKey(d2)) colRows[d2] = 0; idRow[id2] = colRows[d2]; colRows[d2]++; }
                    foreach (var id2 in created.Keys) { int col = depth.ContainsKey(id2) ? depth[id2] : maxDepth + 1; int row = idRow[id2]; float x = col * 250f; float y = row * 60f; try { dynamic d = created[id2]; d.Attributes.Pivot = new PointF(x, y); } catch { } }
                }
                var idMap = new List<object>(); foreach (var kv in created) { try { var obj2 = (IGH_DocumentObject)kv.Value; idMap.Add(new { id = kv.Key, guid = obj2.InstanceGuid.ToString() }); _idMap[kv.Key] = obj2.InstanceGuid; } catch { } }
                return _json.Serialize(new { result = "built:" + created.Count + " comps", components = idMap });
            } catch (Exception ex) { return "build err:" + ex.Message; }
        }

        // ==== WIRE ====
        string DoWire(string body) { var doc = _ghDoc; if (doc == null) return "no doc"; try { var def = _json.Deserialize<Dictionary<string, object>>(body); if (!def.ContainsKey("wires")) return "need wires"; var wires = (ArrayList)def["wires"]; int count = 0; foreach (var w in wires) { var wl = (IList)w; string fromGuid = (string)wl[0]; string toGuid = (string)wl[2]; IGH_DocumentObject fromObj = null, toObj = null; foreach (var obj in doc.Objects) { if (obj.InstanceGuid.ToString() == fromGuid) fromObj = obj; if (obj.InstanceGuid.ToString() == toGuid) toObj = obj; } if (fromObj == null || toObj == null) continue; int fromOut = ResolvePortOnObject(wl[1], fromObj, false); int toIn = ResolvePortOnObject(wl[3], toObj, true); if (fromOut < 0 || toIn < 0) continue; try { IGH_Param srcParam; if (fromObj is IGH_Param) { srcParam = (IGH_Param)fromObj; } else { var fromP = fromObj.GetType().GetProperty("Params").GetValue(fromObj, null); dynamic fromPd = fromP; srcParam = (IGH_Param)((IList)fromPd.Output)[fromOut]; } var toP = toObj.GetType().GetProperty("Params").GetValue(toObj, null); dynamic toPd2 = toP; ((IGH_Param)((IList)toPd2.Input)[toIn]).AddSource(srcParam); count++; } catch { } } if (count > 0) { try { doc.ScheduleSolution(5, (d2) => { try { d2.NewSolution(false); } catch { } }); } catch { } } return "wired:" + count; } catch (Exception ex) { return "wire err:" + ex.Message; } }

        // ==== INSPECT ====
        string DoInspect(string body) { string guid = ""; int idx = body.IndexOf("\"guid\""); if (idx >= 0) { int start = body.IndexOf('"', idx + 7) + 1; int end = body.IndexOf('"', start); if (end > start) guid = body.Substring(start, end - start); } if (guid.Length == 0) return "need guid"; var obj = TryResolveComponent(guid); if (obj == null) return "not found: " + guid; try { var p = obj.GetType().GetProperty("Params").GetValue(obj, null); dynamic dp = p; var inputs = new List<object>(); var outputs = new List<object>(); foreach (dynamic inp in dp.Input) inputs.Add(new { n = inp.NickName ?? "", t = inp.TypeName ?? "" }); foreach (dynamic outp in dp.Output) outputs.Add(new { n = outp.NickName ?? "", t = outp.TypeName ?? "" }); return _json.Serialize(new { name = obj.GetType().Name, inputs, outputs }); } catch { return "no params"; } }
