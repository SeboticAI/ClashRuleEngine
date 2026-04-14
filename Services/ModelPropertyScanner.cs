using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;

namespace ClashRuleEngine.Services
{
    public static class ModelPropertyScanner
    {
        private static Dictionary<string, List<string>> _cache;
        private static string _cachedDocName;

        public static Dictionary<string, List<string>> GetAvailableProperties(bool forceRefresh = false)
        {
            var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null) return new Dictionary<string, List<string>>();

            string docName = doc.FileName ?? "untitled";
            if (!forceRefresh && _cache != null && _cachedDocName == docName)
                return _cache;

            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                int count = 0;
                int maxSample = 500;

                foreach (Model model in doc.Models)
                {
                    if (count >= maxSample) break;
                    foreach (ModelItem item in model.RootItem.Descendants)
                    {
                        if (count >= maxSample) break;
                        foreach (PropertyCategory cat in item.PropertyCategories)
                        {
                            string catName = cat.DisplayName;
                            if (string.IsNullOrWhiteSpace(catName)) continue;
                            if (!result.ContainsKey(catName))
                                result[catName] = new List<string>();

                            foreach (DataProperty prop in cat.Properties)
                            {
                                string propName = prop.DisplayName;
                                if (!string.IsNullOrWhiteSpace(propName) &&
                                    !result[catName].Contains(propName, StringComparer.OrdinalIgnoreCase))
                                    result[catName].Add(propName);
                            }
                        }
                        count++;
                    }
                }

                foreach (var key in result.Keys.ToList())
                    result[key] = result[key].OrderBy(p => p).ToList();

                _cache = result;
                _cachedDocName = docName;
            }
            catch { }
            return result;
        }

        public static List<string> GetPropertyValues(string categoryName, string propertyName, int maxSample = 200)
        {
            var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null) return new List<string>();

            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                int count = 0;
                foreach (Model model in doc.Models)
                {
                    if (count >= maxSample) break;
                    foreach (ModelItem item in model.RootItem.Descendants)
                    {
                        if (count >= maxSample) break;
                        foreach (PropertyCategory cat in item.PropertyCategories)
                        {
                            if (!string.Equals(cat.DisplayName, categoryName, StringComparison.OrdinalIgnoreCase))
                                continue;
                            foreach (DataProperty prop in cat.Properties)
                            {
                                if (!string.Equals(prop.DisplayName, propertyName, StringComparison.OrdinalIgnoreCase))
                                    continue;
                                string val = GetValueAsString(prop);
                                if (!string.IsNullOrWhiteSpace(val)) values.Add(val);
                            }
                        }
                        count++;
                    }
                }
            }
            catch { }
            return values.OrderBy(v => v).ToList();
        }

        private static string GetValueAsString(DataProperty prop)
        {
            if (prop.Value == null) return null;
            var val = prop.Value;
            if (val.IsDisplayString) return val.ToDisplayString();
            if (val.IsDouble) return val.ToDouble().ToString();
            if (val.IsInt32) return val.ToInt32().ToString();
            if (val.IsBoolean) return val.ToBoolean().ToString();
            if (val.IsDateTime) return val.ToDateTime().ToString();
            if (val.IsDoubleLength) return val.ToDoubleLength().ToString();
            if (val.IsDoubleArea) return val.ToDoubleArea().ToString();
            if (val.IsDoubleVolume) return val.ToDoubleVolume().ToString();
            if (val.IsDoubleAngle) return val.ToDoubleAngle().ToString();
            if (val.IsNamedConstant) return val.ToNamedConstant().DisplayName;
            return val.ToString();
        }

        public static void ClearCache() { _cache = null; _cachedDocName = null; }
    }
}
