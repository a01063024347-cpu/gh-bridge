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
