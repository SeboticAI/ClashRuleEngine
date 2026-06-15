using System;

namespace ClashRuleEngine.Models
{
    [Serializable]
    public class RuleCondition
    {
        /// <summary>
        /// Pseudo-category meaning "match against the element's tree hierarchy
        /// path" (Category / Family / Type ...) instead of a real property.
        /// Coordination NWCs carry element type in the tree, not in properties,
        /// so this lets rules say e.g. path Contains "Conduit".
        /// </summary>
        public const string TreeCategory = "Tree";

        public string PropertyCategory { get; set; } = string.Empty;
        public string PropertyName { get; set; } = string.Empty;
        public ConditionOperator Operator { get; set; } = ConditionOperator.Equals;
        public string Value { get; set; } = string.Empty;
        public ClashItemTarget Target { get; set; } = ClashItemTarget.Either;

        /// <summary>True when this condition matches the tree path, not a property.</summary>
        public static bool IsTreePathRef(string category, string property)
        {
            return string.Equals(category, TreeCategory, StringComparison.OrdinalIgnoreCase)
                || string.Equals(property, "Path", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property, "TreePath", StringComparison.OrdinalIgnoreCase);
        }

        public bool Evaluate(string actualValue)
        {
            if (string.IsNullOrEmpty(actualValue))
                return false;

            switch (Operator)
            {
                case ConditionOperator.Equals:
                    return string.Equals(actualValue, Value, StringComparison.OrdinalIgnoreCase);
                case ConditionOperator.NotEquals:
                    return !string.Equals(actualValue, Value, StringComparison.OrdinalIgnoreCase);
                case ConditionOperator.Contains:
                    return actualValue.IndexOf(Value, StringComparison.OrdinalIgnoreCase) >= 0;
                case ConditionOperator.DoesNotContain:
                    return actualValue.IndexOf(Value, StringComparison.OrdinalIgnoreCase) < 0;
                case ConditionOperator.StartsWith:
                    return actualValue.StartsWith(Value, StringComparison.OrdinalIgnoreCase);
                case ConditionOperator.GreaterThan:
                    if (double.TryParse(actualValue, out double gtA) && double.TryParse(Value, out double gtB))
                        return gtA > gtB;
                    return string.Compare(actualValue, Value, StringComparison.OrdinalIgnoreCase) > 0;
                case ConditionOperator.LessThan:
                    if (double.TryParse(actualValue, out double ltA) && double.TryParse(Value, out double ltB))
                        return ltA < ltB;
                    return string.Compare(actualValue, Value, StringComparison.OrdinalIgnoreCase) < 0;
                case ConditionOperator.GreaterThanOrEqual:
                    if (double.TryParse(actualValue, out double gteA) && double.TryParse(Value, out double gteB))
                        return gteA >= gteB;
                    return string.Compare(actualValue, Value, StringComparison.OrdinalIgnoreCase) >= 0;
                case ConditionOperator.LessThanOrEqual:
                    if (double.TryParse(actualValue, out double lteA) && double.TryParse(Value, out double lteB))
                        return lteA <= lteB;
                    return string.Compare(actualValue, Value, StringComparison.OrdinalIgnoreCase) <= 0;
                default:
                    return false;
            }
        }

        public override string ToString()
        {
            string t = Target == ClashItemTarget.Item1 ? "[A] " :
                       Target == ClashItemTarget.Item2 ? "[B] " : "";
            string field = IsTreePathRef(PropertyCategory, PropertyName)
                ? "Tree Path"
                : $"{PropertyCategory}.{PropertyName}";
            return $"{t}{field} {GetOp()} \"{Value}\"";
        }

        private string GetOp()
        {
            switch (Operator)
            {
                case ConditionOperator.Equals: return "=";
                case ConditionOperator.NotEquals: return "\u2260";
                case ConditionOperator.Contains: return "contains";
                case ConditionOperator.DoesNotContain: return "not contains";
                case ConditionOperator.StartsWith: return "starts with";
                case ConditionOperator.GreaterThan: return ">";
                case ConditionOperator.LessThan: return "<";
                case ConditionOperator.GreaterThanOrEqual: return "\u2265";
                case ConditionOperator.LessThanOrEqual: return "\u2264";
                default: return "?";
            }
        }
    }

    public enum ConditionOperator
    {
        Equals, NotEquals, Contains, DoesNotContain, StartsWith,
        GreaterThan, LessThan, GreaterThanOrEqual, LessThanOrEqual
    }

    public enum ClashItemTarget
    {
        Either, Item1, Item2
    }
}
