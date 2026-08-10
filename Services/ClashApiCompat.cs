using System.Collections.Generic;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;

namespace ClashRuleEngine.Services
{
    /// <summary>
    /// Version compatibility layer for how clash tests are reached.
    ///
    ///   2026+:  DocumentClashTests.Value.TestsRoot  — ClashTestFolder tree, walked recursively
    ///   ≤2025:  DocumentClashTests.Tests            — flat SavedItemCollection
    ///
    /// NW_TESTS_TREE is defined by the csproj for NavisworksVersion >= 2026.
    ///
    /// The threshold is 2026, not 2027. Reflecting both installs on 2026-08-10 showed:
    ///   - 2026 has BOTH `DocumentClashTests.Tests` and `Value.TestsRoot`, and the
    ///     `ClashTestFolder` type — so 2026 supports clash-test folders as well.
    ///   - 2027 has `Value.TestsRoot` only; `Tests` is gone.
    /// So the earlier note that 2027 *introduced* clash-test folders was wrong. Using the flat
    /// `.Tests` collection on 2026 would compile and appear to work while silently MISSING every
    /// test filed inside a folder — a wrong-but-quiet result, which is worse than a build error.
    /// Both supported versions therefore take the same folder-aware path.
    ///
    /// The ≤2025 branch has never been compiled against a real 2024/2025 reference set (neither
    /// is installed here, and the company does not use them). Treat it as untested.
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
