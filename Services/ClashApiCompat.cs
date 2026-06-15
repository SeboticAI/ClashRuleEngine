using System.Collections.Generic;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;

namespace ClashRuleEngine.Services
{
    /// <summary>
    /// Version compatibility layer for the Clash API surface that moved in
    /// Navisworks 2027 (verified via tools\Dump-NavisApi.ps1):
    ///
    ///   ≤2026:  DocumentClashTests.Tests            — flat SavedItemCollection
    ///   2027+:  DocumentClashTests.Value.TestsRoot  — ClashTestFolder tree
    ///            (2027 introduced clash test folders)
    ///
    /// NW_TESTS_TREE is defined by the csproj for NavisworksVersion >= 2027,
    /// so each per-version build compiles the matching typed code path.
    /// </summary>
    internal static class ClashApiCompat
    {
        /// <summary>All clash tests in the document, flattened across folders.</summary>
        public static List<ClashTest> GetAllTests(DocumentClashTests testsData)
        {
            var tests = new List<ClashTest>();
            if (testsData == null) return tests;
#if NW_TESTS_TREE
            CollectTests(testsData.Value.TestsRoot.Children, tests);
#else
            foreach (SavedItem item in testsData.Tests)
                if (item is ClashTest ct) tests.Add(ct);
#endif
            return tests;
        }

#if NW_TESTS_TREE
        private static void CollectTests(SavedItemCollection children, List<ClashTest> into)
        {
            foreach (SavedItem si in children)
            {
                if (si is ClashTest ct) into.Add(ct);
                else if (si is ClashTestFolder folder) CollectTests(folder.Children, into);
            }
        }
#endif
    }
}
