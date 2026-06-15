using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.Navisworks.Api;
using ClashRuleEngine.Models;

namespace ClashRuleEngine.Services
{
    /// <summary>
    /// Classifies a model element into a hierarchy discipline by matching each
    /// discipline's keywords against a "haystack" built from the element's
    /// source/model file name (its ancestor path) AND a handful of key property
    /// values (Workset, Category, Type, Family, Layer...). The "both" strategy —
    /// chosen because discipline can live in either the federated file name or a
    /// parameter depending on the model.
    ///
    /// Disciplines are tested in hierarchy order, so when keywords overlap the
    /// higher-precedence discipline wins. Pure read-only API use; safe to call
    /// per clash item.
    /// </summary>
    public static class DisciplineClassifier
    {
        // Property names worth inspecting for discipline cues. Matched case-
        // insensitively by DisplayName across all of the item's categories.
        private static readonly HashSet<string> CueProperties =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Workset", "Category", "Type Name", "Type", "Family", "Family Name",
            "Family and Type", "Layer", "Source File", "System Name",
            "System Classification", "System Type", "Material"
        };

        public static DisciplineDefinition Classify(ModelItem item, SystemHierarchy hierarchy)
        {
            if (item == null || hierarchy?.Disciplines == null || hierarchy.Disciplines.Count == 0)
                return null;

            string haystack = BuildHaystack(item);
            if (string.IsNullOrEmpty(haystack)) return null;

            foreach (var disc in hierarchy.Disciplines)
            {
                if (disc.Keywords == null) continue;
                foreach (var kw in disc.Keywords)
                {
                    if (string.IsNullOrWhiteSpace(kw)) continue;
                    if (haystack.IndexOf(kw.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                        return disc;
                }
            }
            return null;
        }

        /// <summary>
        /// Builds one lowercased search string from the element's ancestor display
        /// names (captures the source/model file name at the root) plus the values
        /// of cue properties on the element itself.
        /// </summary>
        private static string BuildHaystack(ModelItem item)
        {
            var sb = new StringBuilder(256);

            // Ancestor path — the root node is typically the linked file name.
            var cur = item;
            int depth = 0;
            while (cur != null && depth < 12)
            {
                if (!string.IsNullOrWhiteSpace(cur.DisplayName))
                    sb.Append(cur.DisplayName).Append(' ');
                cur = cur.Parent;
                depth++;
            }

            // Cue property values from the element itself.
            try
            {
                foreach (PropertyCategory cat in item.PropertyCategories)
                {
                    foreach (DataProperty prop in cat.Properties)
                    {
                        if (!CueProperties.Contains(prop.DisplayName)) continue;
                        string v = ValueToString(prop);
                        if (!string.IsNullOrEmpty(v))
                            sb.Append(v).Append(' ');
                    }
                }
            }
            catch { /* a partial haystack still classifies most elements */ }

            return sb.ToString();
        }

        private static string ValueToString(DataProperty prop)
        {
            try
            {
                var val = prop.Value;
                if (val == null) return null;
                if (val.IsDisplayString) return val.ToDisplayString();
                if (val.IsNamedConstant) return val.ToNamedConstant().DisplayName;
                if (val.IsDouble) return val.ToDouble().ToString(CultureInfo.InvariantCulture);
                if (val.IsInt32) return val.ToInt32().ToString(CultureInfo.InvariantCulture);
                if (val.IsBoolean) return val.ToBoolean().ToString();
                return val.ToString();
            }
            catch { return null; }
        }
    }
}
