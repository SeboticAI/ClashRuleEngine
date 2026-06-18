using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.Navisworks.Api;

namespace ClashRuleEngine.Services
{
    /// <summary>
    /// The "kind" of a clashing element — Category / Family / Type / System words +
    /// a diameter in mm — harvested by walking up the ancestor chain (the clash node
    /// itself is usually a bare geometry "Solid" with no properties of its own).
    ///
    /// THE single source of truth used by BOTH the headless batch extractor and the
    /// live engine, so a rule derived from the batch JSONL matches exactly what the
    /// engine computes at run time.
    /// </summary>
    public sealed class ElementKindInfo
    {
        /// <summary>Lowercased haystack of all kind words (category/family/type/system/tree)
        /// — rule keywords are matched (substring) against this.</summary>
        public string Text { get; set; } = "";
        /// <summary>Diameter in millimetres (0 if unknown).</summary>
        public double DiameterMm { get; set; }
        /// <summary>Best single label for display/grouping (System, else a tree/family token).</summary>
        public string Label { get; set; } = "";
        public string Category { get; set; } = "";
        public string System { get; set; } = "";
        /// <summary>Revit Family name (e.g. "DGM Straight").</summary>
        public string Family { get; set; } = "";
        /// <summary>Full Type Name, descriptors INTACT (e.g. "20_CS_Copper - Soldered").</summary>
        public string Type { get; set; } = "";
        /// <summary>Raw descriptive leaf tree token, numbers KEPT (e.g. "DGM Straight: 1300",
        /// "DGM Bend Radius") — the nuance the cleaned Label throws away.</summary>
        public string Leaf { get; set; } = "";

        public bool ContainsAny(IEnumerable<string> needles)
        {
            if (needles == null) return false;
            foreach (var n in needles)
                if (!string.IsNullOrWhiteSpace(n) && Text.IndexOf(n.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
    }

    public static class ElementKind
    {
        private static readonly string[] CategoryProps = { "Category" };
        private static readonly string[] FamilyProps = { "Family", "Family Name", "Family and Type" };
        // "Type Name" rarely exists on coordination NWCs; the plain "Type" property
        // carries the rich value (e.g. "Pipes: Pipe Types: 20_CS_Copper - Soldered").
        private static readonly string[] TypeProps = { "Type Name", "Type" };
        private static readonly string[] SystemProps = { "System Name", "System Classification", "System Type", "System Abbreviation" };
        private static readonly string[] DiameterProps = { "Outside Diameter", "Diameter", "Inside Diameter", "Nominal Diameter" };

        private static readonly HashSet<string> Noise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Solid", "Standard", "Default", "Internal", "<Not Shared>", "Default Site", "Generic Models", "Direct Shape" };

        public static ElementKindInfo Compute(ModelItem item)
        {
            var info = new ElementKindInfo();
            if (item == null) return info;

            var sb = new StringBuilder(256);
            string category = null, family = null, type = null, system = null, leafToken = null, rawLeaf = null;
            double diaM = 0;

            var cur = item;
            int depth = 0;
            while (cur != null && depth < 16)
            {
                // tree token (nearest meaningful ancestor name)
                string dn = null;
                try { dn = cur.DisplayName; } catch { }
                string tok = CleanToken(dn);
                if (tok != null)
                {
                    sb.Append(tok).Append(' ');
                    if (leafToken == null) leafToken = tok;
                }
                if (rawLeaf == null)
                {
                    string rl = RawLeafToken(dn);   // descriptive leaf, numbers kept
                    if (rl != null) rawLeaf = rl;
                }

                try
                {
                    foreach (PropertyCategory cat in cur.PropertyCategories)
                        foreach (DataProperty p in cat.Properties)
                        {
                            string nm = p.DisplayName;
                            if (nm == null) continue;
                            if (category == null && Match(nm, CategoryProps)) category = Val(p);
                            else if (family == null && Match(nm, FamilyProps)) family = Val(p);
                            else if (type == null && Match(nm, TypeProps)) type = Val(p);
                            else if (system == null && Match(nm, SystemProps)) system = Val(p);
                            else if (diaM <= 0 && Match(nm, DiameterProps)) diaM = Num(p);
                        }
                }
                catch { }

                try { cur = cur.Parent; } catch { cur = null; }
                depth++;
            }

            if (!string.IsNullOrWhiteSpace(category)) sb.Append(category).Append(' ');
            if (!string.IsNullOrWhiteSpace(family)) sb.Append(family).Append(' ');
            if (!string.IsNullOrWhiteSpace(type)) sb.Append(type).Append(' ');
            if (!string.IsNullOrWhiteSpace(system)) sb.Append(system).Append(' ');
            if (!string.IsNullOrWhiteSpace(rawLeaf)) sb.Append(rawLeaf).Append(' ');   // descriptors into the haystack

            info.Text = sb.ToString().ToLowerInvariant();
            info.DiameterMm = diaM > 0 ? diaM * 1000.0 : 0;
            info.Category = category ?? "";
            info.System = system ?? "";
            info.Family = family ?? "";
            info.Type = type ?? "";
            info.Leaf = rawLeaf ?? "";
            info.Label = !string.IsNullOrWhiteSpace(system) ? system
                       : !string.IsNullOrWhiteSpace(leafToken) ? leafToken
                       : !string.IsNullOrWhiteSpace(family) ? family
                       : (category ?? "");
            return info;
        }

        private static bool Match(string name, string[] names)
        {
            for (int i = 0; i < names.Length; i++)
                if (string.Equals(name, names[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string CleanToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();
            if (s.IndexOf(".rvt", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (s.IndexOf(".ifc", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (s.IndexOf(".nwc", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (s.IndexOf(" : ", StringComparison.Ordinal) >= 0) return null;
            int br = s.IndexOf('['); if (br > 0) s = s.Substring(0, br).Trim();
            int e = s.Length; while (e > 0 && (char.IsDigit(s[e - 1]) || s[e - 1] == ' ')) e--;
            if (e >= 2 && e < s.Length) s = s.Substring(0, e).Trim();
            if (s.Length < 2 || Noise.Contains(s)) return null;
            return s;
        }

        // Generic container nodes that aren't descriptive — skip past them to the real name.
        private static readonly HashSet<string> LeafNoise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Solid", "Standard", "Default", "Internal", "<Not Shared>", "Default Site",
            "Generic Models", "Direct Shape", "Pipe Types", "Duct Types", "Cable Tray Types",
            "Conduit Types", "Pipe Fitting Types", "Duct Fitting Types", "Cable Tray Fitting Types"
        };

        /// <summary>Nearest descriptive leaf token, KEEPING numbers/size/length descriptors
        /// (unlike CleanToken). Skips file/location nodes and generic "* Types" containers so
        /// it lands on e.g. "DGM Straight: 1300" / "20_CS_Copper - Soldered".</summary>
        private static string RawLeafToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();
            if (s.IndexOf(".rvt", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (s.IndexOf(".ifc", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (s.IndexOf(".nwc", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (s.IndexOf(" : ", StringComparison.Ordinal) >= 0) return null;   // file/location nodes
            int br = s.IndexOf('['); if (br > 0) s = s.Substring(0, br).Trim(); // drop [instance id]
            if (s.Length < 2 || LeafNoise.Contains(s)) return null;
            return s;
        }

        private static string Val(DataProperty p)
        {
            try
            {
                var v = p.Value; if (v == null) return null;
                if (v.IsDisplayString) return v.ToDisplayString();
                if (v.IsNamedConstant) return v.ToNamedConstant().DisplayName;
                if (v.IsDouble) return v.ToDouble().ToString(CultureInfo.InvariantCulture);
                if (v.IsInt32) return v.ToInt32().ToString(CultureInfo.InvariantCulture);
                if (v.IsBoolean) return v.ToBoolean().ToString();
                return v.ToString();
            }
            catch { return null; }
        }

        private static double Num(DataProperty p)
        {
            try
            {
                var v = p.Value; if (v == null) return 0;
                if (v.IsDoubleLength) return v.ToDoubleLength();
                if (v.IsDouble) return v.ToDouble();
                if (v.IsInt32) return v.ToInt32();
            }
            catch { }
            return 0;
        }
    }
}
