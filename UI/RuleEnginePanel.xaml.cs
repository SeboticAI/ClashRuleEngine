using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClashRuleEngine.Models;
using ClashRuleEngine.Services;

namespace ClashRuleEngine.UI
{
    public partial class RuleEnginePanel : UserControl
    {
        private ProjectConfig _config;
        private TestRuleSet _currentTestRuleSet;
        private ObservableCollection<ClashRule> _rules;
        private ObservableCollection<ClashResultInfo> _clashResults;
        private ClashProcessingService _processor;
        private Point _dragStartPoint;

        // Which tab is active: true = Rules, false = Clashes
        private bool _rulesTabActive = true;

        public RuleEnginePanel()
        {
            InitializeComponent();
            _processor = new ClashProcessingService();
            _rules = new ObservableCollection<ClashRule>();
            _clashResults = new ObservableCollection<ClashResultInfo>();

            lstRules.ItemsSource = _rules;
            lstClashes.ItemsSource = _clashResults;

            LoadConfig();
            RefreshClashTests();
        }

        // ──────────────────────────────────────────────────────
        // Config + test loading
        // ──────────────────────────────────────────────────────

        private void LoadConfig()
        {
            try { _config = RulePersistenceService.Load(); }
            catch { _config = new ProjectConfig(); }
        }

        private void RefreshClashTests()
        {
            try
            {
                var tests = ClashTestScanner.GetClashTests();
                string previousSelection = cmbClashTest.SelectedItem?.ToString();

                cmbClashTest.Items.Clear();
                foreach (var test in tests)
                    cmbClashTest.Items.Add(test.ToString());

                if (tests.Count == 0)
                {
                    txtStatus.Text = "No clash tests found — open a model with clash tests";
                    return;
                }

                if (previousSelection != null && cmbClashTest.Items.Contains(previousSelection))
                    cmbClashTest.SelectedItem = previousSelection;
                else if (cmbClashTest.Items.Count > 0)
                    cmbClashTest.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Error: {ex.Message}";
            }
        }

        private string GetSelectedTestName()
        {
            var selected = cmbClashTest.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(selected)) return null;
            int parenIdx = selected.LastIndexOf(" (");
            return parenIdx > 0 ? selected.Substring(0, parenIdx) : selected;
        }

        private void OnClashTestChanged(object sender, SelectionChangedEventArgs e)
        {
            string testName = GetSelectedTestName();
            if (string.IsNullOrEmpty(testName))
            {
                _currentTestRuleSet = null;
                _rules.Clear();
                _clashResults.Clear();
                UpdateUI();
                return;
            }

            _currentTestRuleSet = _config.GetOrCreateTestRuleSet(testName);
            _currentTestRuleSet.ReindexPriorities();

            _rules.Clear();
            foreach (var r in _currentTestRuleSet.Rules)
                _rules.Add(r);

            // If the Clashes tab is active, refresh the clash list automatically
            if (!_rulesTabActive)
                LoadClashesForTest(testName);

            UpdateUI();
        }

        private void OnRefreshTests(object sender, RoutedEventArgs e)
        {
            RefreshClashTests();
        }

        private void UpdateUI()
        {
            string testName = GetSelectedTestName();
            if (_currentTestRuleSet != null)
            {
                int count = _currentTestRuleSet.Rules.Count;
                txtStatus.Text = $"{testName}: {count} rule{(count != 1 ? "s" : "")}";
            }
            else
            {
                txtStatus.Text = "Select a clash test";
            }

            bool hasRules = _rules.Count > 0;
            pnlEmptyState.Visibility = hasRules ? Visibility.Collapsed : Visibility.Visible;

            bool hasClashes = _clashResults.Count > 0;
            pnlClashEmptyState.Visibility = hasClashes ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SyncRulesToTestSet()
        {
            if (_currentTestRuleSet == null) return;
            _currentTestRuleSet.Rules = _rules.ToList();
            _currentTestRuleSet.ReindexPriorities();

            var temp = _rules.ToList();
            _rules.Clear();
            foreach (var r in temp) _rules.Add(r);
        }

        // ──────────────────────────────────────────────────────
        // Tab switching
        // ──────────────────────────────────────────────────────

        private void OnTabRulesClick(object sender, MouseButtonEventArgs e)
        {
            SetActiveTab(true);
        }

        private void OnTabClashesClick(object sender, MouseButtonEventArgs e)
        {
            SetActiveTab(false);
            string testName = GetSelectedTestName();
            if (!string.IsNullOrEmpty(testName) && _clashResults.Count == 0)
                LoadClashesForTest(testName);
        }

        private void SetActiveTab(bool rulesActive)
        {
            _rulesTabActive = rulesActive;
            pnlTabRules.Visibility    = rulesActive ? Visibility.Visible : Visibility.Collapsed;
            pnlTabClashes.Visibility  = rulesActive ? Visibility.Collapsed : Visibility.Visible;

            // Active tab: blue underline + blue text
            bdTabRules.BorderBrush    = rulesActive ? Brush("#2563EB") : Brushes.Transparent;
            txtTabRules.Foreground    = rulesActive ? Brush("#2563EB") : Brush("#9CA3AF");
            txtTabRules.FontWeight    = rulesActive ? FontWeights.SemiBold : FontWeights.Normal;

            bdTabClashes.BorderBrush  = rulesActive ? Brushes.Transparent : Brush("#2563EB");
            txtTabClashes.Foreground  = rulesActive ? Brush("#9CA3AF") : Brush("#2563EB");
            txtTabClashes.FontWeight  = rulesActive ? FontWeights.Normal : FontWeights.SemiBold;
        }

        // ──────────────────────────────────────────────────────
        // Clash list (Clashes tab)
        // ──────────────────────────────────────────────────────

        private void LoadClashesForTest(string testName)
        {
            _clashResults.Clear();
            try
            {
                var results = ClashTestScanner.GetClashResults(testName);
                foreach (var cr in results)
                    _clashResults.Add(cr);

                int total = results.Count;
                int active = results.Count(r => r.Status == Autodesk.Navisworks.Api.Clash.ClashResultStatus.Active);
                txtClashCount.Text = $"{total} clash{(total != 1 ? "es" : "")} ({active} active)";
            }
            catch (Exception ex)
            {
                txtClashCount.Text = $"Error loading clashes: {ex.Message}";
            }

            bool hasClashes = _clashResults.Count > 0;
            pnlClashEmptyState.Visibility = hasClashes ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnRefreshClashes(object sender, RoutedEventArgs e)
        {
            string testName = GetSelectedTestName();
            if (!string.IsNullOrEmpty(testName))
                LoadClashesForTest(testName);
        }

        private void OnInspectClash(object sender, RoutedEventArgs e)
        {
            var info = (sender as Button)?.Tag as ClashResultInfo;
            if (info?.SourceResult == null)
            {
                MessageBox.Show("Clash data is no longer available. Try refreshing the clash list.",
                    "Data Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var inspector = new ClashInspectorDialog(info.SourceResult, _config, _currentTestRuleSet);
            inspector.Owner = Window.GetWindow(this);

            if (inspector.ShowDialog() == true)
            {
                // User clicked "Create Rule" in the inspector
                // Switch to Rules tab and open the rule editor with pre-filled conditions
                SetActiveTab(true);

                var newRule = new ClashRule
                {
                    Name = $"Rule from {info.ClashName}",
                    Priority = _rules.Count,
                    GroupName = _currentTestRuleSet?.DefaultAssignee ?? "Unassigned",
                    Assignee = _currentTestRuleSet?.DefaultAssignee ?? "Unassigned",
                    Conditions = inspector.CreatedConditions,
                    ConditionLogic = LogicOperator.And
                };

                if (_currentTestRuleSet == null)
                {
                    MessageBox.Show("Please select a clash test first.", "No Test Selected",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var ruleEditor = new RuleEditorDialog(newRule, _config);
                ruleEditor.Owner = Window.GetWindow(this);
                if (ruleEditor.ShowDialog() == true)
                {
                    _rules.Add(ruleEditor.Rule);
                    SyncRulesToTestSet();
                    UpdateUI();
                }
            }
        }

        // ──────────────────────────────────────────────────────
        // Rule CRUD
        // ──────────────────────────────────────────────────────

        private void OnAddRule(object sender, RoutedEventArgs e)
        {
            if (_currentTestRuleSet == null)
            {
                MessageBox.Show("Please select a clash test first.", "No Test Selected",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var newRule = new ClashRule
            {
                Name = "New Rule",
                Priority = _rules.Count,
                GroupName = "Unassigned",
                Assignee = "Unassigned"
            };

            var dialog = new RuleEditorDialog(newRule, _config);
            dialog.Owner = Window.GetWindow(this);
            if (dialog.ShowDialog() == true)
            {
                _rules.Add(dialog.Rule);
                SyncRulesToTestSet();
                UpdateUI();
            }
        }

        private void OnEditRule(object sender, RoutedEventArgs e)
        {
            var rule = (sender as Button)?.Tag as ClashRule;
            if (rule == null) return;
            var dialog = new RuleEditorDialog(rule, _config);
            dialog.Owner = Window.GetWindow(this);
            if (dialog.ShowDialog() == true)
            {
                int idx = _rules.IndexOf(rule);
                if (idx >= 0) { _rules[idx] = dialog.Rule; SyncRulesToTestSet(); }
            }
        }

        private void OnDeleteRule(object sender, RoutedEventArgs e)
        {
            var rule = (sender as Button)?.Tag as ClashRule;
            if (rule == null) return;
            if (MessageBox.Show($"Delete \"{rule.Name}\"?", "Delete Rule", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                _rules.Remove(rule);
                SyncRulesToTestSet();
                UpdateUI();
            }
        }

        private void OnMoveRuleUp(object sender, RoutedEventArgs e)
        {
            var rule = (sender as Button)?.Tag as ClashRule;
            if (rule == null) return;
            int idx = _rules.IndexOf(rule);
            if (idx > 0) { _rules.Move(idx, idx - 1); SyncRulesToTestSet(); }
        }

        private void OnMoveRuleDown(object sender, RoutedEventArgs e)
        {
            var rule = (sender as Button)?.Tag as ClashRule;
            if (rule == null) return;
            int idx = _rules.IndexOf(rule);
            if (idx >= 0 && idx < _rules.Count - 1) { _rules.Move(idx, idx + 1); SyncRulesToTestSet(); }
        }

        // ──────────────────────────────────────────────────────
        // Save + Run
        // ──────────────────────────────────────────────────────

        private void OnSaveRules(object sender, RoutedEventArgs e)
        {
            try
            {
                SyncRulesToTestSet();
                RulePersistenceService.Save(_config);
                MessageBox.Show("Rules saved.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnRunSelectedTest(object sender, RoutedEventArgs e)
        {
            string testName = GetSelectedTestName();
            if (testName == null || _currentTestRuleSet == null)
            {
                MessageBox.Show("Select a clash test first.", "No Test", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                SyncRulesToTestSet();
                var result = _processor.ProcessSingleTest(testName, _currentTestRuleSet);
                txtResults.Text = result.GetSummary();
                pnlResults.Visibility = Visibility.Visible;
                MessageBox.Show(result.GetSummary(), "Results", MessageBoxButton.OK, MessageBoxImage.Information);

                // Refresh the clash list if it's loaded, to reflect updated statuses
                if (_clashResults.Count > 0)
                    LoadClashesForTest(testName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnRunAllTests(object sender, RoutedEventArgs e)
        {
            try
            {
                SyncRulesToTestSet();
                var result = _processor.ProcessAllTests(_config);
                txtResults.Text = result.GetSummary();
                pnlResults.Visibility = Visibility.Visible;
                MessageBox.Show(result.GetSummary(), "Results", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnSettings(object sender, RoutedEventArgs e)
        {
            var hierarchy = string.Join(" > ", _config.Hierarchy.Systems);
            MessageBox.Show(
                $"System hierarchy:\n{hierarchy}\n\n" +
                $"Assignees: {string.Join(", ", _config.GetAllAssignees())}\n\n" +
                $"Groups: {string.Join(", ", _config.GetAllGroupNames())}\n\n" +
                $"Test rule sets: {_config.TestRuleSets.Count}",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ──────────────────────────────────────────────────────
        // Drag-and-drop rule reordering
        // ──────────────────────────────────────────────────────

        private void OnRuleListPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void OnRuleListPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var diff = _dragStartPoint - e.GetPosition(null);
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                var lbi = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
                if (lbi?.DataContext is ClashRule rule)
                    DragDrop.DoDragDrop(lbi, new DataObject("ClashRule", rule), DragDropEffects.Move);
            }
        }

        private void OnRuleListDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent("ClashRule") ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnRuleListDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("ClashRule")) return;
            var dropped = e.Data.GetData("ClashRule") as ClashRule;
            if (dropped == null) return;
            var target = e.OriginalSource as FrameworkElement;
            while (target != null)
            {
                if (target.DataContext is ClashRule cr && cr != dropped)
                {
                    int from = _rules.IndexOf(dropped), to = _rules.IndexOf(cr);
                    if (from >= 0 && to >= 0 && from != to) { _rules.Move(from, to); SyncRulesToTestSet(); }
                    break;
                }
                target = VisualTreeHelper.GetParent(target) as FrameworkElement;
            }
        }

        // ──────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null) { if (current is T t) return t; current = VisualTreeHelper.GetParent(current); }
            return null;
        }

        private static SolidColorBrush Brush(string hex)
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { return Brushes.Gray; }
        }
    }
}
