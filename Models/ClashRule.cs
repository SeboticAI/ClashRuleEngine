using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashRuleEngine.Models
{
    [Serializable]
    public class ClashRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "New Rule";
        public string Description { get; set; } = string.Empty;
        public int Priority { get; set; } = 0;
        public bool IsEnabled { get; set; } = true;
        public string GroupName { get; set; } = string.Empty;

        /// <summary>
        /// Literal owner used when <see cref="AssigneeMode"/> is Named. For the
        /// relative modes it is only a fallback if the trade can't be classified.
        /// </summary>
        public string Assignee { get; set; } = string.Empty;

        /// <summary>How the assignee is determined: a fixed name, or relative to the clash's trades.</summary>
        public AssigneeMode AssigneeMode { get; set; } = AssigneeMode.Named;

        /// <summary>
        /// Which clashing item this rule is "about" — the anchor for Owning/Other.
        /// Owning trade = this item's trade; Other trade = the opposite item's trade.
        /// Only Item1/Item2 are meaningful here.
        /// </summary>
        public ClashItemTarget SubjectItem { get; set; } = ClashItemTarget.Item1;

        public string ClashStatus { get; set; } = "Active";

        /// <summary>Human-readable assignee label for the rule list (not persisted — get-only).</summary>
        public string AssigneeDisplay
        {
            get
            {
                string side = SubjectItem == ClashItemTarget.Item2 ? "B" : "A";
                switch (AssigneeMode)
                {
                    case AssigneeMode.OwningTrade: return $"Owning trade (Item {side})";
                    case AssigneeMode.OtherTrade:  return $"Other trade (vs Item {side})";
                    default: return string.IsNullOrWhiteSpace(Assignee) ? "Unassigned" : Assignee;
                }
            }
        }

        public List<RuleCondition> Conditions { get; set; } = new List<RuleCondition>();
        public LogicOperator ConditionLogic { get; set; } = LogicOperator.And;
        public string Color { get; set; } = "#2563EB";

        public bool Evaluate(Func<ClashItemTarget, string, string, string> getPropertyValue)
        {
            if (!IsEnabled || Conditions.Count == 0) return false;

            if (ConditionLogic == LogicOperator.And)
            {
                foreach (var c in Conditions)
                    if (!EvalCondition(c, getPropertyValue)) return false;
                return true;
            }
            else
            {
                foreach (var c in Conditions)
                    if (EvalCondition(c, getPropertyValue)) return true;
                return false;
            }
        }

        private bool EvalCondition(RuleCondition condition, Func<ClashItemTarget, string, string, string> getProp)
        {
            if (condition.Target == ClashItemTarget.Either)
            {
                string v1 = getProp(ClashItemTarget.Item1, condition.PropertyCategory, condition.PropertyName);
                string v2 = getProp(ClashItemTarget.Item2, condition.PropertyCategory, condition.PropertyName);
                return condition.Evaluate(v1) || condition.Evaluate(v2);
            }
            else
            {
                string v = getProp(condition.Target, condition.PropertyCategory, condition.PropertyName);
                return condition.Evaluate(v);
            }
        }

        public ClashRule Clone()
        {
            return new ClashRule
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = this.Name + " (Copy)",
                Description = this.Description,
                Priority = this.Priority,
                IsEnabled = this.IsEnabled,
                GroupName = this.GroupName,
                Assignee = this.Assignee,
                AssigneeMode = this.AssigneeMode,
                SubjectItem = this.SubjectItem,
                ClashStatus = this.ClashStatus,
                ConditionLogic = this.ConditionLogic,
                Color = this.Color,
                Conditions = this.Conditions.Select(c => new RuleCondition
                {
                    PropertyCategory = c.PropertyCategory,
                    PropertyName = c.PropertyName,
                    Operator = c.Operator,
                    Value = c.Value,
                    Target = c.Target
                }).ToList()
            };
        }
    }

    public enum LogicOperator { And, Or }

    /// <summary>
    /// How a rule's assignee is chosen.
    ///   Named       — a fixed person/company/trade (the Assignee string).
    ///   OwningTrade — the trade of the item this rule is about (SubjectItem).
    ///   OtherTrade  — the trade of the other item in the clash.
    /// The relative modes are resolved per-clash via the discipline classifier,
    /// so the result is always one of the two trades in that clash.
    /// </summary>
    public enum AssigneeMode { Named, OwningTrade, OtherTrade }
}
