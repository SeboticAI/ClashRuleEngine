using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ClashRuleEngine.Services
{
    /// <summary>
    /// The rule sets SHIPPED INSIDE the plugin, so a new user has working rules the moment
    /// they install — no separate file to be sent, found and imported. They are EMBEDDED
    /// RESOURCES rather than loose files on purpose: a rule set that can go missing from
    /// Program Files is a rule set that will go missing.
    ///
    /// Two are shipped so the newer mined set can be trialled against the one in production
    /// without swapping files around:
    ///
    ///   "current" — a copy of the config that was actually in use during coordination:
    ///               923 element-pair rules + 25 per-test default assignees, approve floor
    ///               50 mm flat. NOTE this is a HYBRID that neither analyzer JSON reproduces
    ///               on its own (the pair rules came from the 923-rule file, which carries no
    ///               per-test defaults; the 25 defaults came from the mined file). That is
    ///               exactly why it ships as a .clashre and not as a kind-rules JSON.
    ///   "mined"   — the newest analyzer output: 274 deviation-only pair rules leaning on 25
    ///               per-test defaults, approve floor 25 mm plus 20 per-pair floors, and a
    ///               measured 81.8% replay against history. Approves MORE than "current",
    ///               which is why it is not the default.
    ///
    /// The default is deliberately the proven set: a rollout is the wrong moment to also
    /// change auto-approve behaviour, because an over-approved clash is one nobody looks at
    /// again.
    /// </summary>
    internal static class BuiltInRuleSets
    {
        public sealed class RuleSet
        {
            /// <summary>Stable id persisted in ProjectConfig.ActiveRuleSetId.</summary>
            public string Id { get; set; }
            /// <summary>Text for the picker.</summary>
            public string Label { get; set; }
            /// <summary>One-line explanation shown under the picker.</summary>
            public string Description { get; set; }
            /// <summary>Embedded resource name (see the csproj LogicalName).</summary>
            public string ResourceName { get; set; }

            public override string ToString() { return Label; }
        }

        public const string CurrentId = "current";
        public const string MinedId = "mined";

        private static readonly List<RuleSet> _all = new List<RuleSet>
        {
            new RuleSet
            {
                Id = CurrentId,
                Label = "Standard — in production (recommended)",
                Description = "923 element-pair rules + 25 per-test defaults. Auto-approves at "
                            + "50 mm clearance. The set used on coordinated jobs.",
                ResourceName = "ClashRuleEngine.RuleSets.current.clashre",
            },
            new RuleSet
            {
                Id = MinedId,
                Label = "Mined v2 — trial",
                Description = "274 deviation-only rules + 25 per-test defaults, reproduces 81.8% "
                            + "of past calls. Auto-approves from 25 mm with per-pair floors, so it "
                            + "approves MORE than Standard — compare on one test first.",
                ResourceName = "ClashRuleEngine.RuleSets.mined.json",
            },
        };

        /// <summary>All shipped rule sets, in display order. The first is the default.</summary>
        public static IList<RuleSet> All { get { return _all; } }

        public static RuleSet Default { get { return _all[0]; } }

        /// <summary>Look up by id; falls back to the default rather than returning null, so a
        /// config carrying an id from a newer build still lands on something usable.</summary>
        public static RuleSet Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            return _all.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The rule set's file content. Throws if the resource is missing — that is a build
        /// error (a wrong LogicalName in the csproj), not something to swallow at runtime.
        /// </summary>
        public static string LoadText(RuleSet set)
        {
            if (set == null) throw new ArgumentNullException("set");

            var asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream(set.ResourceName))
            {
                if (s == null)
                {
                    throw new InvalidOperationException(
                        "Built-in rule set '" + set.Id + "' is missing from the assembly (expected "
                        + "embedded resource '" + set.ResourceName + "'). Check the "
                        + "<EmbeddedResource><LogicalName> entries in ClashRuleEngine.csproj. "
                        + "Present: " + string.Join(", ", asm.GetManifestResourceNames()));
                }
                using (var r = new StreamReader(s, System.Text.Encoding.UTF8, true))
                    return r.ReadToEnd();
            }
        }
    }
}
