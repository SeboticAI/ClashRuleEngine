using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Navisworks.Api.Clash;
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
        private readonly List<ClashResultInfo> _allClashResults = new List<ClashResultInfo>();
        private ClashResultStatus? _clashFilter;   // null = show all statuses
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
            catch { _config = RulePersistenceService.NewSeeded(); }
            _config.Hierarchy?.EnsureSeeded();
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

            // Clash data belongs to the previous test — drop it so the Clashes tab
            // reloads fresh (now, or when next opened).
            _allClashResults.Clear();
            _clashResults.Clear();
            pnlClashFilters.Children.Clear();

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
            if (!string.IsNullOrEmpty(testName) && _allClashResults.Count == 0)
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
            _allClashResults.Clear();
            try
            {
                _allClashResults.AddRange(ClashTestScanner.GetClashResults(testName));
            }
            catch (Exception ex)
            {
                _allClashResults.Clear();
                _clashResults.Clear();
                pnlClashFilters.Children.Clear();
                txtClashCount.Text = $"Error loading clashes: {ex.Message}";
                pnlClashEmptyState.Visibility = Visibility.Visible;
                return;
            }

            BuildClashFilterChips();
            ApplyClashFilter();
        }

        /// <summary>Re-fills the visible list from the full set per the active status filter.</summary>
        private void ApplyClashFilter()
        {
            _clashResults.Clear();
            IEnumerable<ClashResultInfo> view = _allClashResults;
            if (_clashFilter.HasValue)
                view = view.Where(r => r.Status == _clashFilter.Value);
            foreach (var r in view)
                _clashResults.Add(r);

            int total = _allClashResults.Count;
            int shown = _clashResults.Count;
            string scope = _clashFilter.HasValue ? _clashFilter.Value.ToString().ToLowerInvariant() : "all";
            txtClashCount.Text = total > 0
                ? $"showing {shown} of {total} ({scope})  ·  click a clash to zoom to it"
                : "No clashes in this test";
            pnlClashEmptyState.Visibility = shown > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Builds the status filter chips with live per-status counts.</summary>
        private void BuildClashFilterChips()
        {
            // If the active filter no longer has any results, fall back to "All".
            if (_clashFilter.HasValue && !_allClashResults.Any(r => r.Status == _clashFilter.Value))
                _clashFilter = null;

            pnlClashFilters.Children.Clear();
            if (_allClashResults.Count == 0) return;

            pnlClashFilters.Children.Add(MakeFilterChip("All", null, _allClashResults.Count));
            foreach (var st in new[] { ClashResultStatus.Active, ClashResultStatus.Reviewed,
                                       ClashResultStatus.Approved, ClashResultStatus.Resolved })
            {
                int c = _allClashResults.Count(r => r.Status == st);
                if (c > 0)
                    pnlClashFilters.Children.Add(MakeFilterChip(st.ToString(), st, c));
            }
        }

        private Border MakeFilterChip(string label, ClashResultStatus? status, int count)
        {
            bool active = Equals(_clashFilter, status);
            var border = new Border
            {
                Background = active ? Brush("#2563EB") : Brush("#F3F4F6"),
                BorderBrush = active ? Brush("#2563EB") : Brush("#E5E7EB"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 5, 5),
                Cursor = Cursors.Hand,
                Tag = status
            };
            border.Child = new TextBlock
            {
                Text = $"{label} · {count}",
                FontSize = 11,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = active ? Brushes.White : Brush("#374151")
            };
            border.MouseLeftButtonDown += OnClashFilterChipClick;
            return border;
        }

        private void OnClashFilterChipClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                _clashFilter = (sender as Border)?.Tag as ClashResultStatus?;
                BuildClashFilterChips();   // refresh highlight
                ApplyClashFilter();
            }
            catch { /* filtering is non-critical; never crash the host */ }
        }

        private void OnRefreshClashes(object sender, RoutedEventArgs e)
        {
            string testName = GetSelectedTestName();
            if (!string.IsNullOrEmpty(testName))
                LoadClashesForTest(testName);
        }

        /// <summary>
        /// Clicking a clash in the list selects (highlights) its elements in the
        /// model and frames the clash in the active 3D view, using the clash's own
        /// saved viewpoint. The primary "find this clash" interaction.
        /// </summary>
        private void OnClashSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var info = lstClashes.SelectedItem as ClashResultInfo;
            if (info?.SourceResult == null) return;
            try { ClashNavigationService.NavigateTo(info.SourceResult); }
            catch { /* navigation is best-effort; never disrupt the UI */ }
        }

        private void OnInspectClash(object sender, RoutedEventArgs e)
        {
            var info = (sender as Button)?.Tag as ClashResultInfo;
            if (info?.SourceResult == null || !ClashNavigationService.IsUsable(info.SourceResult))
            {
                MessageBox.Show("Clash data is no longer available (the test was re-run). Click Refresh to reload the clash list.",
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

        private void OnToggleRuleEnabled(object sender, RoutedEventArgs e)
        {
            try
            {
                var rule = (sender as CheckBox)?.Tag as ClashRule;
                if (rule == null) return;
                rule.IsEnabled = (sender as CheckBox).IsChecked ?? true;
                // Rebuild the list so the row opacity reflects the new state
                // (ClashRule isn't INotifyPropertyChanged).
                SyncRulesToTestSet();
            }
            catch { /* never let a UI toggle crash the host */ }
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

        private void OnExportSession(object sender, RoutedEventArgs e)
        {
            try
            {
                var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
                if (doc == null || string.IsNullOrEmpty(doc.FileName))
                {
                    MessageBox.Show("Open a document first.", "No Document", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Scope: selected test only (fast) or the whole document (can be
                // hundreds of MB on a real federated model).
                string onlyTest = null;
                string selected = GetSelectedTestName();
                if (selected != null)
                {
                    var choice = MessageBox.Show(
                        $"Export only the selected test '{selected}'?\n\n" +
                        "Yes — just this test (fast)\n" +
                        "No — ALL tests (can take several minutes on a large model)",
                        "Export Scope", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (choice == MessageBoxResult.Cancel) return;
                    if (choice == MessageBoxResult.Yes) onlyTest = selected;
                }

                string baseName = System.IO.Path.GetFileNameWithoutExtension(doc.FileName);
                if (onlyTest != null)
                {
                    foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                        selected = selected.Replace(c, '_');
                    baseName += "." + selected;
                }

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export coordination session",
                    Filter = "Session JSON (*.session.json)|*.session.json|All files (*.*)|*.*",
                    InitialDirectory = System.IO.Path.GetDirectoryName(doc.FileName),
                    FileName = baseName + ".session.json"
                };
                if (dlg.ShowDialog() != true) return;

                var progress = new ExportProgressWindow();
                progress.Show();
                string summary;
                try
                {
                    summary = SessionExportService.ExportTests(
                        dlg.FileName, onlyTest, progress.Report, () => progress.Cancelled);
                }
                finally
                {
                    progress.Close();
                }
                MessageBox.Show(summary, "Session Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                var result = _processor.ProcessSingleTest(testName, _currentTestRuleSet,
                    _config.Hierarchy, _config.UseHierarchyFallback);
                ShowResults(result);

                // Refresh the clash list if it's loaded, to reflect updated statuses
                if (_allClashResults.Count > 0)
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
                ShowResults(result);

                // Running rules swaps each test and disposes the old ClashResult
                // objects our list holds. Reload so we never navigate stale refs.
                string testName = GetSelectedTestName();
                if (_allClashResults.Count > 0 && !string.IsNullOrEmpty(testName))
                    LoadClashesForTest(testName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ──────────────────────────────────────────────────────
        // Run results view (replaces the old blocking MessageBox dump)
        // ──────────────────────────────────────────────────────

        private void ShowResults(ProcessingResult result)
        {
            txtResultsTitle.Text = $"Run results — {result.TestName}";

            // Stat chips: evaluated / assigned / unmatched / skipped / groups
            pnlResultStats.Children.Clear();
            pnlResultStats.Children.Add(MakeStatChip("evaluated", result.ClashesProcessed, "#EFF6FF", "#1E40AF"));
            pnlResultStats.Children.Add(MakeStatChip("by rules", result.Assigned, "#F0FDF4", "#166534"));
            if (result.HierarchyAssigned > 0)
                pnlResultStats.Children.Add(MakeStatChip("by hierarchy", result.HierarchyAssigned, "#F5F3FF", "#5B21B6"));
            pnlResultStats.Children.Add(MakeStatChip("unmatched", result.Unmatched, "#FFFBEB", "#92400E"));
            pnlResultStats.Children.Add(MakeStatChip("groups", result.GroupsCreated, "#F5F3FF", "#5B21B6"));
            if (result.Skipped > 0)
                pnlResultStats.Children.Add(MakeStatChip("skipped", result.Skipped, "#F3F4F6", "#6B7280"));

            // Per-rule breakdown — largest first
            lstResultRules.ItemsSource = result.AssignmentsByRule.Values
                .OrderByDescending(a => a.Count).ToList();

            // Warnings (collapsible)
            if (result.Errors.Count > 0)
            {
                txtResultWarningsHeader.Text = $"⚠ {result.Errors.Count} warning{(result.Errors.Count != 1 ? "s" : "")}";
                txtResultWarnings.Text = string.Join(Environment.NewLine,
                    result.Errors.Take(20).Select(e => "• " + e));
                expResultWarnings.Visibility = Visibility.Visible;
                expResultWarnings.IsExpanded = false;
            }
            else
            {
                expResultWarnings.Visibility = Visibility.Collapsed;
            }

            pnlResults.Visibility = Visibility.Visible;
        }

        private void OnDismissResults(object sender, RoutedEventArgs e)
        {
            pnlResults.Visibility = Visibility.Collapsed;
        }

        private Border MakeStatChip(string label, int value, string bgHex, string fgHex)
        {
            var fg = Brush(fgHex);
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text = value.ToString(),
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Foreground = fg
            });
            panel.Children.Add(new TextBlock
            {
                Text = " " + label,
                FontSize = 11,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center
            });
            return new Border
            {
                Background = Brush(bgHex),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 6, 0),
                Child = panel
            };
        }

        private void OnAiRules(object sender, RoutedEventArgs e)
        {
            string testName = GetSelectedTestName();
            if (testName == null || _currentTestRuleSet == null)
            {
                MessageBox.Show("Select a clash test first.", "No Test", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Gather example clashes (with any existing assignments) to teach the model.
            List<ClashResultInfo> examples = _allClashResults.Count > 0
                ? _allClashResults
                : SafeGetClashes(testName);

            var dlg = new AiAssistDialog(_config, testName, examples) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.GeneratedRules.Count > 0)
            {
                foreach (var r in dlg.GeneratedRules)
                {
                    r.Priority = _rules.Count;
                    _rules.Add(r);
                }
                SyncRulesToTestSet();
                UpdateUI();
                MessageBox.Show(
                    $"Added {dlg.GeneratedRules.Count} rule(s) to {testName}.\n\nReview them, reorder if needed, then Save and Run.",
                    "AI Rules Added", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private static List<ClashResultInfo> SafeGetClashes(string testName)
        {
            try { return ClashTestScanner.GetClashResults(testName); }
            catch { return new List<ClashResultInfo>(); }
        }

        private void OnSettings(object sender, RoutedEventArgs e)
        {
            _config.Hierarchy?.EnsureSeeded();
            var dialog = new SettingsDialog(_config);
            dialog.Owner = Window.GetWindow(this);
            if (dialog.ShowDialog() == true)
            {
                RulePersistenceService.Save(_config);
                UpdateUI();
            }
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
