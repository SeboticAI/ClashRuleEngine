using System;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;

namespace ClashRuleEngine.Services
{
    /// <summary>
    /// Drives the Navisworks 3D viewport from a clash result: selects (highlights)
    /// the two clashing elements and frames the clash in the active view.
    ///
    /// Framing uses the clash's OWN saved viewpoint via
    /// DocumentClashTests.TestsViewpointForResult — the same camera Clash Detective
    /// jumps to when you click a clash in its results grid. This is the only
    /// managed-API way to "zoom to a clash"; there is no plain zoom-to-selection
    /// in the managed surface (that lives in the COM bridge, which we don't
    /// reference). When a result has no saved viewpoint the selection still
    /// highlights, so the user can locate it manually.
    ///
    /// API verified against the 2027 DLLs (tools\navis-api-2027.txt):
    ///   ClashResult.CompositeItemSelection1/2  [R] ModelItemCollection (whole elements)
    ///   ClashResult.Selection1/2               [R] ModelItemCollection (clash geometry)
    ///   ClashResult.HasSavedViewpoint          [R] Boolean
    ///   DocumentClashTests.TestsViewpointForResult(IClashResult) -> Viewpoint
    ///   Document.CurrentSelection.CopyFrom(ModelItemCollection)
    ///   Document.CurrentViewpoint.CopyFrom(Viewpoint)
    /// </summary>
    public static class ClashNavigationService
    {
        /// <summary>
        /// Selects both clashing items and frames the clash in the active 3D view.
        /// All steps are best-effort and non-fatal — a failure to frame never
        /// prevents the selection from happening, and vice versa.
        /// </summary>
        public static void NavigateTo(ClashResult clash)
        {
            if (!IsUsable(clash)) return;
            var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null) return;

            SelectItems(doc, clash);
            FrameClash(doc, clash);
        }

        /// <summary>
        /// True only if the result is a live, non-disposed object. After rules run,
        /// TestsEditTestFromCopy swaps the test and disposes the old ClashResult
        /// objects; touching their geometry/viewpoint then is an AccessViolation
        /// that crashes Navisworks (uncatchable by managed try/catch), so we must
        /// check BEFORE any member access.
        /// </summary>
        public static bool IsUsable(ClashResult clash)
        {
            if (clash == null) return false;
            try { return !clash.IsDisposed; }
            catch { return false; }
        }

        private static void SelectItems(Document doc, ClashResult clash)
        {
            try
            {
                var items = new ModelItemCollection();

                // Prefer the composite (whole-element) selection so the user sees
                // the full pipe/duct/beam highlighted, matching Clash Detective.
                AddRange(items, SafeGet(() => clash.CompositeItemSelection1));
                AddRange(items, SafeGet(() => clash.CompositeItemSelection2));

                // Fall back to the precise clash geometry, then to single items.
                if (items.Count == 0)
                {
                    AddRange(items, SafeGet(() => clash.Selection1));
                    AddRange(items, SafeGet(() => clash.Selection2));
                }
                if (items.Count == 0)
                {
                    var i1 = SafeGet(() => clash.Item1);
                    var i2 = SafeGet(() => clash.Item2);
                    if (i1 != null) items.Add(i1);
                    if (i2 != null) items.Add(i2);
                }

                doc.CurrentSelection.CopyFrom(items);
            }
            catch { /* selection is best-effort */ }
        }

        private static void FrameClash(Document doc, ClashResult clash)
        {
            try
            {
                var clashPlugin = doc.GetClash();
                if (clashPlugin == null) return;

                var vp = clashPlugin.TestsData.TestsViewpointForResult(clash);
                if (vp != null)
                    doc.CurrentViewpoint.CopyFrom(vp);
            }
            catch { /* framing is best-effort — selection alone still helps locate it */ }
        }

        private static void AddRange(ModelItemCollection into, ModelItemCollection from)
        {
            if (from == null) return;
            foreach (ModelItem mi in from)
                if (mi != null) into.Add(mi);
        }

        private static T SafeGet<T>(Func<T> getter) where T : class
        {
            try { return getter(); } catch { return null; }
        }
    }
}
