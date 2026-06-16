using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;

namespace ClashRuleEngine.Services
{
    /// <summary>
    /// Shared state + drawing for the 3D clash-marker overlay. The RenderPlugin and
    /// InputPlugin (which Navisworks instantiates on its own) read this static state;
    /// the panel writes it (which markers, enabled on/off, selected marker). This is
    /// the documented pattern from Autodesk's ClashMarkers sample — a static utility
    /// shared between the render/input plugins and the UI.
    ///
    /// Markers are drawn from a SNAPSHOT (Guid + Center + Color) so OverlayRender
    /// never touches a live (possibly disposed) ClashResult — it only projects a
    /// cached Point3D. Click hit-testing returns the marker GUID; the panel resolves
    /// the live result by GUID (ClashNavigationService.ResolveLive) when reacting.
    /// </summary>
    public static class ClashMarkerService
    {
        public sealed class Marker
        {
            public Guid ResultGuid;
            public Point3D Center;
            public Color Color;       // fill colour (by status / rule)
        }

        // Transient per-frame projection used for click hit-testing.
        private sealed class Projected
        {
            public Guid ResultGuid;
            public double X, Y, Depth;
        }

        private const double MarkerRadius = 7.0;
        private const double OutlineThickness = 2.0;
        private const double FillAlpha = 0.7;

        private static readonly object _sync = new object();
        private static List<Marker> _markers = new List<Marker>();
        private static List<Projected> _hitList = new List<Projected>();

        /// <summary>Whether the overlay is drawn / interactive.</summary>
        public static bool Enabled { get; set; }

        /// <summary>GUID of the marker the user has selected (drawn highlighted).</summary>
        public static Guid SelectedMarker { get; set; }

        /// <summary>Raised (with the clicked marker's GUID) when a marker is clicked
        /// in the 3D view. The panel subscribes to select + navigate to it.</summary>
        public static event Action<Guid> MarkerClicked;

        /// <summary>Replace the set of markers to draw (snapshot — no live refs held).</summary>
        public static void SetMarkers(IEnumerable<Marker> markers)
        {
            lock (_sync)
                _markers = (markers ?? Enumerable.Empty<Marker>()).Where(m => m?.Center != null).ToList();
        }

        public static void Clear()
        {
            lock (_sync) { _markers = new List<Marker>(); _hitList = new List<Projected>(); }
        }

        public static int Count
        {
            get { lock (_sync) return _markers.Count; }
        }

        /// <summary>Maps a clash status to a marker colour (matches the list's status chips).</summary>
        public static Color ColorForStatus(ClashResultStatus status)
        {
            switch (status)
            {
                case ClashResultStatus.Active:   return Color.FromByteRGB(239, 68, 68);   // red
                case ClashResultStatus.Reviewed: return Color.FromByteRGB(245, 158, 11);  // amber
                case ClashResultStatus.Approved: return Color.FromByteRGB(16, 185, 129);  // green
                case ClashResultStatus.Resolved: return Color.FromByteRGB(107, 114, 128); // grey
                default:                         return Color.FromByteRGB(107, 114, 128);
            }
        }

        /// <summary>
        /// Draws every marker as a filled, outlined circle at its projected screen
        /// position, with the selected marker emphasised. Called from
        /// RenderPlugin.OverlayRender. Rebuilds the hit-test list as a side effect.
        /// </summary>
        public static void DrawOverlay(View view, Graphics g)
        {
            if (!Enabled || view == null || g == null) return;

            List<Marker> snapshot;
            lock (_sync) snapshot = _markers;
            if (snapshot.Count == 0) { lock (_sync) _hitList = new List<Projected>(); return; }

            var hits = new List<Projected>(snapshot.Count);

            // Back-to-front so nearer markers draw on top.
            foreach (var m in snapshot)
            {
                ProjectionResult pr;
                try { pr = view.ProjectPoint(m.Center, true, true); }
                catch { continue; }
                if (pr == null) continue;   // clipped / off-screen

                hits.Add(new Projected { ResultGuid = m.ResultGuid, X = pr.X, Y = pr.Y, Depth = pr.Depth });
            }

            foreach (var p in hits.OrderByDescending(h => h.Depth))
            {
                var m = snapshot.FirstOrDefault(x => x.ResultGuid == p.ResultGuid);
                if (m == null) continue;
                var pos = new Point2D(p.X, p.Y);

                g.Color(m.Color, FillAlpha);
                g.Circle(pos, MarkerRadius, true);

                g.LineWidth((float)OutlineThickness);
                g.Color(Color.FromByteRGB(0, 0, 0), 1);
                g.Circle(pos, MarkerRadius, false);

                if (m.ResultGuid == SelectedMarker && SelectedMarker != Guid.Empty)
                {
                    g.LineWidth(2f);
                    g.Color(Color.FromByteRGB(37, 99, 235), 1);   // blue selection ring
                    g.Circle(pos, MarkerRadius + 4.0, false);
                }
            }

            lock (_sync) _hitList = hits;
        }

        /// <summary>
        /// Nearest marker to a screen point within the click radius, or Guid.Empty.
        /// Called from InputPlugin.MouseDown.
        /// </summary>
        public static Guid HitTest(int x, int y)
        {
            List<Projected> hits;
            lock (_sync) hits = _hitList;
            double clickRadius = MarkerRadius + OutlineThickness / 2.0;

            double bestDepth = -1;
            Guid best = Guid.Empty;
            foreach (var h in hits)
            {
                double dist = Math.Sqrt(Math.Pow(h.X - x, 2) + Math.Pow(h.Y - y, 2));
                if (dist < clickRadius && (bestDepth < 0 || h.Depth < bestDepth))
                {
                    bestDepth = h.Depth;
                    best = h.ResultGuid;
                }
            }
            return best;
        }

        /// <summary>Invoked by the InputPlugin to notify subscribers of a click.</summary>
        public static void RaiseMarkerClicked(Guid guid)
        {
            try { MarkerClicked?.Invoke(guid); } catch { }
        }

        /// <summary>Ask the active 3D view to repaint the overlay layer.</summary>
        public static void RequestRedraw()
        {
            try
            {
                Autodesk.Navisworks.Api.Application.ActiveDocument?
                    .ActiveView?.RequestDelayedRedraw(ViewRedrawRequests.OverlayRender);
            }
            catch { }
        }
    }
}
