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
