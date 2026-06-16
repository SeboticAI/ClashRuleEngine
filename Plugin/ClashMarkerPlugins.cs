using System;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;
using ClashRuleEngine.Services;

namespace ClashRuleEngine.Plugin
{
    /// <summary>
    /// Draws the clash-marker overlay (filled circles at each clash centre, coloured
    /// by status) on top of the 3D/2D view. Navisworks instantiates this on its own
    /// and calls OverlayRender every frame; it reads the shared ClashMarkerService
    /// snapshot, so it never touches a live ClashResult. Toggled from the panel via
    /// ClashMarkerService.Enabled.
    /// </summary>
    [Plugin("ClashRuleEngineMarkers.Render", "ACME", DisplayName = "Clash Rule Engine Markers")]
    public class ClashMarkerRenderPlugin : RenderPlugin
    {
        public override void OverlayRender(View view, Graphics graphics)
        {
            if (!ClashMarkerService.Enabled) return;
            try { ClashMarkerService.DrawOverlay(view, graphics); }
            catch { /* overlay is best-effort — never break the render loop */ }
        }
    }

    /// <summary>
    /// Click-to-select for clash markers. When the overlay is enabled, a left click
    /// that lands on a marker is intercepted and reported to the panel (which selects
    /// the clash and frames it). A click that misses every marker passes through to
    /// the active navigation tool unchanged.
    /// </summary>
    [Plugin("ClashRuleEngineMarkers.Input", "ACME", DisplayName = "Clash Rule Engine Marker Input")]
    public class ClashMarkerInputPlugin : InputPlugin
    {
        public override bool MouseDown(View view, KeyModifiers modifiers, ushort button,
            int x, int y, double timeOffset)
        {
            if (!ClashMarkerService.Enabled) return false;
            if (button != 1) return false;   // left button only

            Guid hit;
            try { hit = ClashMarkerService.HitTest(x, y); }
            catch { return false; }

            if (hit == Guid.Empty) return false;   // no marker — let the tool handle it

            ClashMarkerService.RaiseMarkerClicked(hit);
            return true;   // intercept so the click doesn't also drive navigation
        }
    }
}
