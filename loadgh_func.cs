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
