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
