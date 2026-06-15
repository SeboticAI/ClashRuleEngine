using System;
using System.Collections.Generic;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;

namespace ClashRuleEngine.Services
{
    /// <summary>
    /// Discovers available clash tests and their results from the active Navisworks document.
    /// </summary>
    public static class ClashTestScanner
    {
        /// <summary>
        /// Get all clash results for a specific test (for the Clash Inspector).
        /// Returns live ClashResult references — only valid while the document is open.
        /// </summary>
        public static List<ClashResultInfo> GetClashResults(string testName)
        {
            var results = new List<ClashResultInfo>();
            var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null) return results;
            try
            {
                var clashPlugin = doc.GetClash();
                if (clashPlugin == null) return results;
                foreach (ClashTest ct in ClashApiCompat.GetAllTests(clashPlugin.TestsData))
                {
                    if (!string.Equals(ct.DisplayName, testName, StringComparison.OrdinalIgnoreCase)) continue;
                    CollectResultInfos(ct.Children, results);
                    break; // found the test
                }
            }
            catch { }
            return results;
        }

        /// <summary>
        /// Get all clash test names from the document
        /// </summary>
        public static List<ClashTestInfo> GetClashTests()
        {
            var tests = new List<ClashTestInfo>();
            var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null) return tests;

            try
            {
                var clashPlugin = doc.GetClash();
                if (clashPlugin == null) return tests;

                foreach (ClashTest ct in ClashApiCompat.GetAllTests(clashPlugin.TestsData))
                {
                    int totalClashes = 0;
                    int activeClashes = 0;
                    int resolvedClashes = 0;
                    CountResults(ct.Children, ref totalClashes, ref activeClashes, ref resolvedClashes);

                    tests.Add(new ClashTestInfo
                    {
                        Name = ct.DisplayName,
                        TotalClashes = totalClashes,
                        ActiveClashes = activeClashes,
                        ResolvedClashes = resolvedClashes,
                        Status = ct.Status.ToString(),
                        LastRun = ct.LastRun ?? DateTime.MinValue
                    });
                }
            }
            catch { }

            return tests;
        }

        /// <summary>
        /// Recursively collects result infos, descending into clash groups
        /// (results live inside ClashResultGroups once grouping has run).
        /// </summary>
        private static void CollectResultInfos(SavedItemCollection children, List<ClashResultInfo> results)
        {
            foreach (SavedItem child in children)
            {
                if (child is ClashResultGroup grp)
                {
                    CollectResultInfos(grp.Children, results);
                }
                else if (child is ClashResult cr)
                {
                    results.Add(new ClashResultInfo
                    {
                        ClashName = cr.DisplayName,
                        Status = cr.Status,
                        Description = cr.Description ?? string.Empty,
                        Item1Name = cr.Item1?.DisplayName ?? "Unknown",
                        Item2Name = cr.Item2?.DisplayName ?? "Unknown",
                        SourceResult = cr
                    });
                }
            }
        }

        private static void CountResults(SavedItemCollection children,
            ref int total, ref int active, ref int resolved)
        {
            foreach (SavedItem child in children)
            {
                if (child is ClashResultGroup grp)
                {
                    CountResults(grp.Children, ref total, ref active, ref resolved);
                }
                else if (child is ClashResult cr)
                {
                    total++;
                    if (cr.Status == ClashResultStatus.Resolved) resolved++;
                    else active++;
                }
            }
        }
    }

    public class ClashTestInfo
    {
        public string Name { get; set; }
        public int TotalClashes { get; set; }
        public int ActiveClashes { get; set; }
        public int ResolvedClashes { get; set; }
        public string Status { get; set; }
        public DateTime LastRun { get; set; }

        public override string ToString()
        {
            return $"{Name} ({ActiveClashes} active / {TotalClashes} total)";
        }
    }

    /// <summary>
    /// Summary data for a single clash result, used in the Clash Inspector tab.
    /// SourceResult is a live reference to the Navisworks object.
    /// </summary>
    public class ClashResultInfo
    {
        public string ClashName { get; set; }
        public ClashResultStatus Status { get; set; }
        public string Description { get; set; }
        public string Item1Name { get; set; }
        public string Item2Name { get; set; }

        // Live reference — only valid while the document/test is unchanged
        internal ClashResult SourceResult { get; set; }

        public string StatusColor
        {
            get
            {
                switch (Status)
                {
                    case ClashResultStatus.Active:   return "#EF4444";
                    case ClashResultStatus.Reviewed: return "#F59E0B";
                    case ClashResultStatus.Approved: return "#10B981";
                    case ClashResultStatus.Resolved: return "#6B7280";
                    default:                         return "#6B7280";
                }
            }
        }

        public string StatusLabel
        {
            get { return Status.ToString(); }
        }
    }
}
