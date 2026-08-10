namespace ClashRuleEngine.Plugin
{
    /// <summary>
    /// Navisworks plugin identity, in ONE place.
    ///
    /// A plugin's lookup key for Application.Plugins.FindPlugin is "&lt;Name&gt;.&lt;DeveloperId&gt;",
    /// composed from its [Plugin] attribute. Those used to be two independent literals
    /// (the attribute and a hardcoded "ClashRuleEngine.ACME" lookup string), which is a
    /// silent-failure waiting to happen: rename one and FindPlugin just returns null, so
    /// the button does nothing with no error. Everything derives from these consts now.
    ///
    /// DeveloperId must be a 4-character developer ID or a GUID. "ACME" was the sample
    /// placeholder and showed up in the Navisworks plugin list for every user.
    /// </summary>
    internal static class PluginIds
    {
        public const string Developer = "OCON";

        public const string PanelName = "ClashRuleEngine";
        public const string RibbonName = "ClashRuleEngineRibbon";
        public const string BatchExtractName = "ClashBatchExtract";
        public const string MarkerRenderName = "ClashRuleEngineMarkers.Render";
        public const string MarkerInputName = "ClashRuleEngineMarkers.Input";

        /// <summary>Lookup key for the dock pane: "ClashRuleEngine.OCON".</summary>
        public const string Panel = PanelName + "." + Developer;

        /// <summary>
        /// Lookup key for the headless extractor, passed to
        /// Automation.ExecuteAddInPlugin by tools\BatchExtractor and
        /// tools\NwdClashLearner. Those are separate projects and carry their own copy
        /// of this string — keep them in step (grep for ClashBatchExtract).
        /// </summary>
        public const string BatchExtract = BatchExtractName + "." + Developer;
    }
}
