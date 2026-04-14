using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using ClashRuleEngine.Models;

namespace ClashRuleEngine.UI
{
    public partial class ClashInspectorDialog : Window
    {
        private readonly ClashResult _clashResult;
        private readonly ProjectConfig _config;
        private readonly TestRuleSet _ruleSet;
        private readonly List<SelectedPropertyInfo> _selectedConditions = new List<SelectedPropertyInfo>();

        // Priority categories shown first in the property view
        private static readonly string[] PriorityCategories = new[]
        {
            "Item", "Dimensions", "Identity Data",
            "Mechanical", "Mechanical - Flow", "Fire Protection",
            "Constraints", "Phasing", "Other"
        };

        /// <summary>
        /// When the user clicks "Create Rule", this is populated with the selected conditions.
        /// Empty list means "open rule editor with no pre-filled conditions".
        /// </summary>
        public List<RuleCondition> CreatedConditions { get; private set; } = new List<RuleCondition>();

        public ClashInspectorDialog(ClashResult clashResult, ProjectConfig config, TestRuleSet ruleSet)
        {
            InitializeComponent();
            _clashResult = clashResult;
            _config = config;
            _ruleSet = ruleSet;
            LoadClashData();
        }

        // ──────────────────────────────────────────────────────
        // Data loading
        // ──────────────────────────────────────────────────────

        private void LoadClashData()
        {
            txtClashName.Text = _clashResult.DisplayName;

            string desc = _clashResult.Description ?? string.Empty;
            txtDescription.Text = string.IsNullOrWhiteSpace(desc) ? "(No description)" : desc;

            // Status badge colour
            string statusColor;
            switch (_clashResult.Status)
            {
                case ClashResultStatus.Active:   statusColor = "#EF4444"; break;
                case ClashResultStatus.Reviewed: statusColor = "#F59E0B"; break;
                case ClashResultStatus.Approved: statusColor = "#10B981"; break;
                case ClashResultStatus.Resolved: statusColor = "#6B7280"; break;
                default:                         statusColor = "#6B7280"; break;
            }
            bdStatus.Background = Hex(statusColor);
            txtStatus.Text = _clashResult.Status.ToString();

            // Build property panels
            if (_clashResult.Item1 != null)
            {
                txtItem1Name.Text = GetItemPath(_clashResult.Item1);
                BuildItemProperties(pnlItem1Props, _clashResult.Item1, ClashItemTarget.Item1);
            }
            else
            {
                txtItem1Name.Text = "(Item A not available)";
            }

            if (_clashResult.Item2 != null)
            {
                txtItem2Name.Text = GetItemPath(_clashResult.Item2);
                BuildItemProperties(pnlItem2Props, _clashResult.Item2, ClashItemTarget.Item2);
            }
            else
            {
                txtItem2Name.Text = "(Item B not available)";
            }

            UpdateConditionsPreview();
        }

        /// <summary>
        /// Returns the display path up to 3 ancestor levels deep.
        /// </summary>
        private static string GetItemPath(ModelItem item)
        {
            var parts = new List<string>();
            var cur = item;
            int depth = 0;
            while (cur != null && depth < 4)
            {
                if (!string.IsNullOrWhiteSpace(cur.DisplayName))
                    parts.Insert(0, cur.DisplayName);
                cur = cur.Parent;
                depth++;
            }
            return string.Join(" \u203A ", parts); // › separator
        }

        // ──────────────────────────────────────────────────────
        // Property panel construction
        // ──────────────────────────────────────────────────────

        private void BuildItemProperties(StackPanel panel, ModelItem item, ClashItemTarget target)
        {
            panel.Children.Clear();

            // Collect all properties (walks up parent chain)
            var categories = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            CollectProperties(item, categories, 0);

            if (categories.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "No properties found for this item.",
                    Foreground = Brushes.Gray,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(12, 8, 12, 0),
                    FontSize = 12
                });
                return;
            }

            bool isItemA = target == ClashItemTarget.Item1;
            string catHeaderBg  = isItemA ? "#EFF6FF" : "#FFF7ED";
            string catHeaderFg  = isItemA ? "#1E40AF" : "#C2410C";
            string hoverBg      = isItemA ? "#DBEAFE" : "#FFEDD5";
            string selectedBg   = isItemA ? "#2563EB" : "#EA580C";

            // Order: priority categories first, then alphabetical
            var orderedCats = categories.Keys
                .OrderBy(k =>
                {
                    int idx = Array.IndexOf(PriorityCategories, k);
                    return idx >= 0 ? idx : 999;
                })
                .ThenBy(k => k)
                .ToList();

            foreach (var catName in orderedCats)
            {
                var props = categories[catName];
                if (props.Count == 0) continue;

                // Category header row
                var catBorder = new Border
                {
                    Background = Hex(catHeaderBg),
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(0, 4, 0, 0)
                };
                catBorder.Child = new TextBlock
                {
                    Text = catName.ToUpperInvariant(),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = Hex(catHeaderFg)
                };
                panel.Children.Add(catBorder);

                // Property rows
                foreach (var kvp in props.OrderBy(p => p.Key))
                {
                    var row = BuildPropertyRow(catName, kvp.Key, kvp.Value, target, hoverBg, selectedBg);
                    panel.Children.Add(row);
                }
            }
        }

        private Border BuildPropertyRow(string catName, string propName, string propValue,
            ClashItemTarget target, string hoverBg, string selectedBg)
        {
            var row = new Border
            {
                Padding = new Thickness(10, 3, 10, 3),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var nameBlock = new TextBlock
            {
                Text = propName,
                FontSize = 11,
                Foreground = Hex("#6B7280"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            var valueBlock = new TextBlock
            {
                Text = propValue,
                FontSize = 11,
                Foreground = Hex("#111827"),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };

            Grid.SetColumn(nameBlock, 0);
            Grid.SetColumn(valueBlock, 1);
            grid.Children.Add(nameBlock);
            grid.Children.Add(valueBlock);
            row.Child = grid;

            // Hover effect
            row.MouseEnter += (s, e) =>
            {
                if ((s as Border)?.Tag?.ToString() != "selected")
                    ((Border)s).Background = Hex(hoverBg);
            };
            row.MouseLeave += (s, e) =>
            {
                if ((s as Border)?.Tag?.ToString() != "selected")
                    ((Border)s).Background = Brushes.Transparent;
            };

            // Click to toggle selection
            row.MouseLeftButtonUp += (s, e) =>
            {
                var r = (Border)s;
                if (r.Tag?.ToString() == "selected")
                {
                    // Deselect
                    r.Tag = null;
                    r.Background = Brushes.Transparent;
                    nameBlock.Foreground = Hex("#6B7280");
                    valueBlock.Foreground = Hex("#111827");
                    valueBlock.FontWeight = FontWeights.SemiBold;
                    _selectedConditions.RemoveAll(c =>
                        c.Category == catName && c.PropertyName == propName && c.Target == target);
                }
                else
                {
                    // Select
                    r.Tag = "selected";
                    r.Background = Hex(selectedBg);
                    nameBlock.Foreground = Brushes.White;
                    valueBlock.Foreground = Brushes.White;
                    valueBlock.FontWeight = FontWeights.Bold;

                    // Avoid duplicate
                    _selectedConditions.RemoveAll(c =>
                        c.Category == catName && c.PropertyName == propName && c.Target == target);
                    _selectedConditions.Add(new SelectedPropertyInfo
                    {
                        Category = catName,
                        PropertyName = propName,
                        Value = propValue,
                        Target = target
                    });
                }
                UpdateConditionsPreview();
            };

            return row;
        }

        // ──────────────────────────────────────────────────────
        // Property extraction (walks parent chain up to depth 6)
        // ──────────────────────────────────────────────────────

        private static void CollectProperties(ModelItem item,
            Dictionary<string, Dictionary<string, string>> categories, int depth)
        {
            if (item == null || depth > 6) return;

            foreach (PropertyCategory cat in item.PropertyCategories)
            {
                string catName = cat.DisplayName;
                if (string.IsNullOrWhiteSpace(catName)) continue;

                if (!categories.ContainsKey(catName))
                    categories[catName] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (DataProperty prop in cat.Properties)
                {
                    string propName = prop.DisplayName;
                    if (string.IsNullOrWhiteSpace(propName)) continue;
                    if (categories[catName].ContainsKey(propName)) continue; // item-level wins over parent

                    string val = GetValueString(prop);
                    if (!string.IsNullOrWhiteSpace(val))
                        categories[catName][propName] = val;
                }
            }

            CollectProperties(item.Parent, categories, depth + 1);
        }

        private static string GetValueString(DataProperty prop)
        {
            if (prop.Value == null) return null;
            var v = prop.Value;
            if (v.IsDisplayString)   return v.ToDisplayString();
            if (v.IsDouble)          return v.ToDouble().ToString("G6");
            if (v.IsInt32)           return v.ToInt32().ToString();
            if (v.IsBoolean)         return v.ToBoolean().ToString();
            if (v.IsDoubleLength)    return v.ToDoubleLength().ToString("G6");
            if (v.IsDoubleArea)      return v.ToDoubleArea().ToString("G6");
            if (v.IsDoubleVolume)    return v.ToDoubleVolume().ToString("G6");
            if (v.IsDoubleAngle)     return v.ToDoubleAngle().ToString("G6");
            if (v.IsNamedConstant)   return v.ToNamedConstant().DisplayName;
            return v.ToString();
        }

        // ──────────────────────────────────────────────────────
        // Selection preview panel
        // ──────────────────────────────────────────────────────

        private void UpdateConditionsPreview()
        {
            pnlSelectedConditions.Children.Clear();

            if (_selectedConditions.Count == 0)
            {
                bdSelectedConditions.Visibility = Visibility.Collapsed;
                txtSelectionHint.Text = "Select properties above to pre-fill the rule editor, or click Create Rule for an empty rule";
                return;
            }

            bdSelectedConditions.Visibility = Visibility.Visible;
            txtSelectionHint.Text = $"{_selectedConditions.Count} condition{(_selectedConditions.Count == 1 ? "" : "s")} selected";

            foreach (var sel in _selectedConditions)
            {
                string targetLabel = sel.Target == ClashItemTarget.Item1 ? "[A]" : "[B]";
                pnlSelectedConditions.Children.Add(new TextBlock
                {
                    Text = $"  {targetLabel}  {sel.Category}.{sel.PropertyName}  =  \"{sel.Value}\"",
                    FontSize = 11,
                    Foreground = Hex("#166534"),
                    FontFamily = new FontFamily("Consolas"),
                    Margin = new Thickness(0, 1, 0, 1)
                });
            }
        }

        // ──────────────────────────────────────────────────────
        // Event handlers
        // ──────────────────────────────────────────────────────

        private void OnClearSelection(object sender, RoutedEventArgs e)
        {
            _selectedConditions.Clear();
            // Rebuild both panels to reset all visual states
            if (_clashResult.Item1 != null)
                BuildItemProperties(pnlItem1Props, _clashResult.Item1, ClashItemTarget.Item1);
            if (_clashResult.Item2 != null)
                BuildItemProperties(pnlItem2Props, _clashResult.Item2, ClashItemTarget.Item2);
            UpdateConditionsPreview();
        }

        private void OnCreateRule(object sender, RoutedEventArgs e)
        {
            CreatedConditions = _selectedConditions.Select(s => new RuleCondition
            {
                PropertyCategory = s.Category,
                PropertyName = s.PropertyName,
                Value = s.Value,
                Operator = ConditionOperator.Equals,
                Target = s.Target
            }).ToList();

            DialogResult = true;
            Close();
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ──────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────

        private static SolidColorBrush Hex(string hex)
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { return Brushes.Gray; }
        }
    }

    /// <summary>Property selected for inclusion in the new rule.</summary>
    internal class SelectedPropertyInfo
    {
        public string Category { get; set; }
        public string PropertyName { get; set; }
        public string Value { get; set; }
        public ClashItemTarget Target { get; set; }
    }
}
