        string DoReadFile(string body) {
            string path = "";
            try { var d = _json.Deserialize<Dictionary<string, object>>(body); if (d != null && d.ContainsKey("path")) path = (string)d["path"]; } catch { }
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return "{\"error\":\"file not found\"}";
            try {
                var archive = new GH_IO.Serialization.GH_Archive();
                if (!archive.ReadFromFile(path)) return "{\"error\":\"read failed\"}";
                string tmpXml = System.IO.Path.GetTempFileName() + ".ghx";
                archive.WriteToFile(tmpXml, false, false);
                string xml = System.IO.File.ReadAllText(tmpXml);
                try { System.IO.File.Delete(tmpXml); } catch { }
                // dump full xml for inspection
                return _json.Serialize(new { file = path, xmlLen = xml.Length, xml = xml });
            } catch (Exception ex) { return "{\"error\":\"" + ex.Message + "\"}"; }
        }
