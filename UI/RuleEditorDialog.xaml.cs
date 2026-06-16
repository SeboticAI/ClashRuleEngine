using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClashRuleEngine.Models;
using ClashRuleEngine.Services;

namespace ClashRuleEngine.UI
{
    public partial class RuleEditorDialog : Window
    {
        public ClashRule Rule { get; private set; }
        private ProjectConfig _config;
        private List<RuleCondition> _conditions;
        private Dictionary<string, List<string>> _modelProps;
        private bool _filterCommonOnly = false;

        // Categories shown when "Common only" filter is active.
        // Matches the most frequently useful BIM property tabs.
        private static readonly HashSet<string> CommonCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Item", "Dimensions", "Identity Data",
            "Mechanical", "Mechanical - Flow", "Fire Protection",
            "Electrical", "Plumbing", "Structural",
            "Constraints", "Phasing", "Other"
        };

        public RuleEditorDialog(ClashRule rule, ProjectConfig config)
        {
            InitializeComponent();
            _config = config;
            Rule = new ClashRule
            {
                Id = rule.Id, Name = rule.Name, Description = rule.Description,
                Priority = rule.Priority, IsEnabled = rule.IsEnabled,
                GroupName = rule.GroupName, Assignee = rule.Assignee,
                AssigneeMode = rule.AssigneeMode, SubjectItem = rule.SubjectItem,
                ClashStatus = rule.ClashStatus, ConditionLogic = rule.ConditionLogic,
                Color = rule.Color,
                Conditions = rule.Conditions.Select(c => new RuleCondition
                {
                    PropertyCategory = c.PropertyCategory, PropertyName = c.PropertyName,
                    Operator = c.Operator, Value = c.Value, Target = c.Target
                }).ToList()
            };
            _conditions = new List<RuleCondition>(Rule.Conditions);

            try { _modelProps = ModelPropertyScanner.GetAvailableProperties(); }
            catch { _modelProps = new Dictionary<string, List<string>>(); }

            PopulateForm();
            UpdateNoPropsWarning();
        }

        // ──────────────────────────────────────────────────────
        // Form population
        // ──────────────────────────────────────────────────────

        private void PopulateForm()
        {
            txtName.Text = Rule.Name;
            txtDescription.Text = Rule.Description;
            chkEnabled.IsChecked = Rule.IsEnabled;

            var groups = _config.GetAllGroupNames();
            if (!string.IsNullOrEmpty(Rule.GroupName) && !groups.Contains(Rule.GroupName))
                groups.Insert(0, Rule.GroupName);
            cmbGroup.ItemsSource = groups;
            cmbGroup.Text = Rule.GroupName;

            var assignees = _config.GetAllAssignees();
            if (!string.IsNullOrEmpty(Rule.Assignee) && !assignees.Contains(Rule.Assignee))
                assignees.Insert(0, Rule.Assignee);
            cmbAssignee.ItemsSource = assignees;
            cmbAssignee.Text = Rule.Assignee;

            // Assignee mode + subject anchor
            switch (Rule.AssigneeMode)
            {
                case AssigneeMode.OwningTrade: cmbAssigneeMode.SelectedIndex = 0; break;
                case AssigneeMode.OtherTrade:  cmbAssigneeMode.SelectedIndex = 1; break;
                default:                       cmbAssigneeMode.SelectedIndex = 2; break;  // Named
            }
            cmbSubject.SelectedIndex = Rule.SubjectItem == ClashItemTarget.Item2 ? 1 : 0;
            UpdateAssigneeModeVisibility();

            cmbLogic.SelectedIndex = Rule.ConditionLogic == LogicOperator.And ? 0 : 1;

            switch ((Rule.ClashStatus ?? "active").ToLowerInvariant())
            {
                case "reviewed": cmbStatus.SelectedIndex = 1; break;
                case "approved": cmbStatus.SelectedIndex = 2; break;
                case "resolved": cmbStatus.SelectedIndex = 3; break;
                default:         cmbStatus.SelectedIndex = 0; break;
            }

            for (int i = 0; i < cmbColor.Items.Count; i++)
                if ((cmbColor.Items[i] as ComboBoxItem)?.Tag?.ToString() == Rule.Color)
                    { cmbColor.SelectedIndex = i; break; }
            if (cmbColor.SelectedIndex < 0) cmbColor.SelectedIndex = 0;

            RebuildConditions();
        }

        private void UpdateNoPropsWarning()
        {
            pnlNoPropsWarning.Visibility = (_modelProps.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnAssigneeModeChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateAssigneeModeVisibility();
        }

        /// <summary>Show the subject picker for relative modes, the named owner for "Specific owner".</summary>
        private void UpdateAssigneeModeVisibility()
        {
            if (pnlSubject == null || pnlNamedAssignee == null) return;   // not built yet
            bool named = cmbAssigneeMode.SelectedIndex == 2;
            pnlSubject.Visibility = named ? Visibility.Collapsed : Visibility.Visible;
            pnlNamedAssignee.Visibility = named ? Visibility.Visible : Visibility.Collapsed;
        }

        // ──────────────────────────────────────────────────────
        // Category filter
        // ──────────────────────────────────────────────────────

        private void OnToggleCommonFilter(object sender, MouseButtonEventArgs e)
        {
            _filterCommonOnly = !_filterCommonOnly;

            if (_filterCommonOnly)
            {
                bdFilterToggle.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#EFF6FF"));
                bdFilterToggle.BorderBrush = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#93C5FD"));
                txtFilterToggle.Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#2563EB"));
                txtFilterToggle.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                bdFilterToggle.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#F3F4F6"));
                bdFilterToggle.BorderBrush = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#D1D5DB"));
                txtFilterToggle.Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#374151"));
                txtFilterToggle.FontWeight = FontWeights.Normal;
            }

            RebuildConditions();
        }

        /// <summary>Returns the category names to show in dropdowns, applying the common filter.</summary>
        private IEnumerable<string> GetVisibleCategories()
        {
            var keys = _modelProps.Keys.AsEnumerable();
            if (_filterCommonOnly)
                keys = keys.Where(k => CommonCategories.Contains(k));
            return keys.OrderBy(k => k);
        }

        // ──────────────────────────────────────────────────────
        // Condition rows
        // ──────────────────────────────────────────────────────

        private void RebuildConditions()
        {
            pnlConditions.Children.Clear();
            for (int i = 0; i < _conditions.Count; i++)
                pnlConditions.Children.Add(BuildConditionRow(_conditions[i], i));

            if (_conditions.Count == 0)
                pnlConditions.Children.Add(new TextBlock
                {
                    Text = "No conditions yet. Click '+ Add Condition' above.",
                    Foreground = Brushes.Gray,
                    FontSize = 12,
                    Margin = new Thickness(4, 8, 4, 8),
                    FontStyle = FontStyles.Italic
                });
        }

        private Border BuildConditionRow(RuleCondition cond, int idx)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#F9FAFB")),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 6),
                BorderBrush = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#E5E7EB")),
                BorderThickness = new Thickness(1)
            };

            var outerGrid = new Grid();
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var stack = new StackPanel();

            // ── Row 1: "WHEN" label row with target + category + property ──────────────

            // Label row above the controls
            var labelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
            labelRow.Children.Add(MakeLabel("WHEN", 80));
            labelRow.Children.Add(MakeLabel("CATEGORY", 158));
            labelRow.Children.Add(MakeLabel("", 8)); // dot spacer
            labelRow.Children.Add(MakeLabel("PROPERTY", 158));
            stack.Children.Add(labelRow);

            var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };

            var cmbTarget = new ComboBox { Width = 80, FontSize = 11, Margin = new Thickness(0, 0, 6, 0) };
            cmbTarget.Items.Add("Either");
            cmbTarget.Items.Add("Item A");
            cmbTarget.Items.Add("Item B");
            cmbTarget.SelectedIndex = (int)cond.Target;
            cmbTarget.SelectionChanged += (s, e) =>
                { cond.Target = (ClashItemTarget)(s as ComboBox).SelectedIndex; };

            var cmbCat = new ComboBox
            {
                Width = 158, FontSize = 11, IsEditable = true,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "Property category (e.g. Item, Dimensions)"
            };
            foreach (var cat in GetVisibleCategories()) cmbCat.Items.Add(cat);
            cmbCat.Text = cond.PropertyCategory;

            var cmbProp = new ComboBox
            {
                Width = 158, FontSize = 11, IsEditable = true,
                ToolTip = "Property name (e.g. Workset, Outside Diameter)"
            };
            cmbProp.Text = cond.PropertyName;
            PopulatePropDropdown(cmbProp, cond.PropertyCategory);

            cmbCat.SelectionChanged += (s, e) =>
            {
                cond.PropertyCategory = (s as ComboBox).Text;
                PopulatePropDropdown(cmbProp, (s as ComboBox).Text);
            };
            cmbCat.LostFocus += (s, e) =>
                { cond.PropertyCategory = (s as ComboBox).Text; };
            cmbProp.SelectionChanged += (s, e) =>
            {
                if ((s as ComboBox).SelectedItem != null)
                    cond.PropertyName = (s as ComboBox).SelectedItem.ToString();
            };
            cmbProp.LostFocus += (s, e) =>
                { cond.PropertyName = (s as ComboBox).Text; };

            row1.Children.Add(cmbTarget);
            row1.Children.Add(cmbCat);
            row1.Children.Add(new TextBlock
            {
                Text = ".",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 4, 0),
                FontWeight = FontWeights.Bold,
                FontSize = 14
            });
            row1.Children.Add(cmbProp);
            stack.Children.Add(row1);

            // ── Row 2: operator + value ───────────────────────────────────────────────

            var labelRow2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
            labelRow2.Children.Add(MakeLabel("OPERATOR", 148));
            labelRow2.Children.Add(MakeLabel("VALUE", 212));
            stack.Children.Add(labelRow2);

            var row2 = new StackPanel { Orientation = Orientation.Horizontal };

            var cmbOp = new ComboBox { Width = 148, FontSize = 11, Margin = new Thickness(0, 0, 6, 0) };
            cmbOp.Items.Add("= Equals");
            cmbOp.Items.Add("\u2260 Not Equals");
            cmbOp.Items.Add("Contains");
            cmbOp.Items.Add("Not Contains");
            cmbOp.Items.Add("Starts With");
            cmbOp.Items.Add("> Greater Than");
            cmbOp.Items.Add("< Less Than");
            cmbOp.Items.Add("\u2265 Greater or Equal");
            cmbOp.Items.Add("\u2264 Less or Equal");
            cmbOp.SelectedIndex = (int)cond.Operator;
            cmbOp.SelectionChanged += (s, e) =>
                { cond.Operator = (ConditionOperator)(s as ComboBox).SelectedIndex; };

            var cmbVal = new ComboBox
            {
                Width = 212, FontSize = 11, IsEditable = true,
                ToolTip = "Value to match (type or pick a sampled value)"
            };
            cmbVal.Text = cond.Value;

            // Lazy-load: populate values only when the dropdown is first opened.
            // The value cache in ModelPropertyScanner makes repeated opens instant.
            bool valuesLoaded = false;
            cmbVal.DropDownOpened += (s, e) =>
            {
                if (valuesLoaded) return;
                valuesLoaded = true;
                try
                {
                    var vals = ModelPropertyScanner.GetPropertyValues(cmbCat.Text, cmbProp.Text);
                    foreach (var v in vals.Take(50)) cmbVal.Items.Add(v);
                }
                catch { }
            };

            // When property changes, reset so the next dropdown-open reloads
            cmbProp.SelectionChanged += (s, e) =>
            {
                cmbVal.Items.Clear();
                valuesLoaded = false;
            };
            cmbVal.LostFocus += (s, e) =>
                { cond.Value = (s as ComboBox).Text; };
            cmbVal.SelectionChanged += (s, e) =>
            {
                if ((s as ComboBox).SelectedItem != null)
                    cond.Value = (s as ComboBox).SelectedItem.ToString();
            };

            row2.Children.Add(cmbOp);
            row2.Children.Add(cmbVal);
            stack.Children.Add(row2);

            // ── Delete button ──────────────────────────────────────────────────────────

            Grid.SetColumn(stack, 0);
            outerGrid.Children.Add(stack);

            var delBtn = new Button
            {
                Content = "\u2715",
                Width = 26, Height = 26,
                FontSize = 12,
                Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#DC2626")),
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#FCA5A5")),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, 0, 0, 0),
                Tag = idx
            };
            delBtn.Click += (s, e) =>
            {
                int i = (int)(s as Button).Tag;
                if (i >= 0 && i < _conditions.Count) { _conditions.RemoveAt(i); RebuildConditions(); }
            };

            Grid.SetColumn(delBtn, 1);
            outerGrid.Children.Add(delBtn);
            border.Child = outerGrid;
            return border;
        }

        private static TextBlock MakeLabel(string text, double width)
        {
            return new TextBlock
            {
                Text = text,
                Width = width,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#9CA3AF")),
                Margin = new Thickness(0, 0, 0, 0)
            };
        }

        private void PopulatePropDropdown(ComboBox cmb, string catName)
        {
            string cur = cmb.Text;
            cmb.Items.Clear();
            if (!string.IsNullOrEmpty(catName) && _modelProps.ContainsKey(catName))
                foreach (var p in _modelProps[catName]) cmb.Items.Add(p);
            cmb.Text = cur;
        }

        // ──────────────────────────────────────────────────────
        // Button handlers
        // ──────────────────────────────────────────────────────

        private void OnAddCondition(object sender, RoutedEventArgs e)
        {
            _conditions.Add(new RuleCondition());
            RebuildConditions();
        }

        private void OnScanModel(object sender, RoutedEventArgs e)
        {
            try
            {
                _modelProps = ModelPropertyScanner.GetAvailableProperties(true);
                int cats = _modelProps.Count;
                int props = _modelProps.Values.Sum(v => v.Count);
                MessageBox.Show(
                    $"Scan complete: {cats} categories, {props} properties found.\n" +
                    $"Property dropdowns will now autocomplete.",
                    "Scan Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                RebuildConditions();
                UpdateNoPropsWarning();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error scanning model: {ex.Message}", "Scan Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtName.Text))
            {
                MessageBox.Show("Please enter a rule name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtName.Focus();
                return;
            }

            Rule.Name = txtName.Text.Trim();
            Rule.Description = txtDescription.Text?.Trim() ?? "";
            Rule.IsEnabled = chkEnabled.IsChecked ?? true;
            Rule.GroupName = cmbGroup.Text?.Trim() ?? "";
            Rule.Assignee = cmbAssignee.Text?.Trim() ?? "";
            Rule.ConditionLogic = cmbLogic.SelectedIndex == 0 ? LogicOperator.And : LogicOperator.Or;
            Rule.ClashStatus = (cmbStatus.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Active";
            Rule.Conditions = _conditions.ToList();
            Rule.Color = (cmbColor.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "#2563EB";

            switch (cmbAssigneeMode.SelectedIndex)
            {
                case 0:  Rule.AssigneeMode = AssigneeMode.OwningTrade; break;
                case 1:  Rule.AssigneeMode = AssigneeMode.OtherTrade; break;
                default: Rule.AssigneeMode = AssigneeMode.Named; break;
            }
            Rule.SubjectItem = cmbSubject.SelectedIndex == 1 ? ClashItemTarget.Item2 : ClashItemTarget.Item1;

            // Remember literal owners/groups for the dropdowns (skip blanks and relative modes).
            if (Rule.AssigneeMode == AssigneeMode.Named && !string.IsNullOrWhiteSpace(Rule.Assignee)
                && !_config.Assignees.Contains(Rule.Assignee, StringComparer.OrdinalIgnoreCase))
                _config.Assignees.Add(Rule.Assignee);
            if (!string.IsNullOrWhiteSpace(Rule.GroupName)
                && !_config.GroupNames.Contains(Rule.GroupName, StringComparer.OrdinalIgnoreCase))
                _config.GroupNames.Add(Rule.GroupName);

            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
