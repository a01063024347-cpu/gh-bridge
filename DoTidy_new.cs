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
