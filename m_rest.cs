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

        // ==== NAME-to-GUID ====
        IGH_DocumentObject TryResolveComponent(string input)
        {
            try { var g = new Guid(input); var r = CI(input); if (r != null) return (IGH_DocumentObject)r; } catch { }
            if (input.Length >= 8) { try { var r = CI(input); if (r != null) return (IGH_DocumentObject)r; } catch { } }
            if (CompDB != null)
            {
                string key = input.ToLowerInvariant();
                foreach (var kv in CompDB) { string name = kv.Value; int pipe = name.IndexOf('|'); if (pipe > 0) name = name.Substring(0, pipe); if (name.ToLowerInvariant() == key) { try { return (IGH_DocumentObject)CI(kv.Key); } catch { } } }
                foreach (var kv in CompDB) { string name = kv.Value; int pipe = name.IndexOf('|'); if (pipe > 0) name = name.Substring(0, pipe); if (name.ToLowerInvariant().Contains(key)) { try { return (IGH_DocumentObject)CI(kv.Key); } catch { } } }
            }
            try { var proxy = Grasshopper.Instances.ComponentServer.FindObjectByName(input, true, true); if (proxy != null) { try { return (IGH_DocumentObject)CI(proxy.Guid.ToString().Substring(0, 8)); } catch { } } } catch { }
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
