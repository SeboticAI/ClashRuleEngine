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

        // Live clash-data change subscription (auto-refresh the list when clashes
        // change). Suppressed while we run our own rules to avoid churn.
        private DocumentClashTests _subscribedTests;
        private bool _suppressClashRefresh;

        // Setup tabs (Hierarchy / Assignees & Groups / General) — moved out of the
        // old Settings dialog into the main tab bar. Editing works on a clone of the
        // disciplines so it commits on Save / tab-leave.
        private int _activeTab;   // 0 Rules, 1 Clashes, 2 Hierarchy, 3 Lists, 4 General
        private System.Collections.ObjectModel.ObservableCollection<DisciplineDefinition> _disciplines;

        /// <summary>Assignee suggestions for the per-discipline owner dropdowns (bound from XAML).</summary>
        public List<string> AvailableAssignees { get; private set; } = new List<string>();

        // Subscribe the global UI-thread safety net once per process.
        private static bool _dispatcherHooked;

        public RuleEnginePanel()
        {
            InitializeComponent();
            HookDispatcherSafetyNet();
            _processor = new ClashProcessingService();
            _rules = new ObservableCollection<ClashRule>();
            _clashResults = new ObservableCollection<ClashResultInfo>();

            lstRules.ItemsSource = _rules;
            lstClashes.ItemsSource = _clashResults;

            try
            {
                LoadConfig();
                RefreshClashTests();
                LoadSettingsTabs();
                SubscribeClashChanges();
                ClashMarkerService.MarkerClicked += OnMarkerClicked;
            }
            catch (Exception ex)
            {
                try { txtStatus.Text = "Startup error: " + ex.Message; } catch { }
            }

            Unloaded += (s, e) =>
            {
                UnsubscribeClashChanges();
                try { ClashMarkerService.MarkerClicked -= OnMarkerClicked; } catch { }
                try { ClashMarkerService.Enabled = false; ClashMarkerService.Clear(); ClashMarkerService.RequestRedraw(); } catch { }
            };
        }

        /// <summary>
        /// Last-resort safety net: an unhandled exception on the WPF UI thread (the
        /// Navisworks main thread) normally TERMINATES Navisworks. We mark such
        /// exceptions handled and surface a message instead, so a bug in any dialog
        /// or handler degrades to an error popup rather than a crash. Hooked once.
        /// </summary>
        private static void HookDispatcherSafetyNet()
        {
            if (_dispatcherHooked) return;
            _dispatcherHooked = true;
            try
            {
                var shown = new HashSet<string>(StringComparer.Ordinal);
                System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException += (s, ex) =>
                {
                    ex.Handled = true;   // keep Navisworks running, always
                    try
                    {
                        // De-dupe: show each distinct error ONCE so a repeated binding/
                        // layout error can never flood the screen with dialogs.
                        string msg = ex.Exception?.Message ?? "Unknown error";
                        if (!shown.Add(msg)) return;
                        MessageBox.Show(
                            "The Clash Rule Engine hit an unexpected error but Navisworks was kept alive:\n\n"
                            + msg + "\n\n(Further identical errors are suppressed.)",
                            "Clash Rule Engine", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    catch { }
                };
            }
            catch { _dispatcherHooked = false; }
        }

        // ──────────────────────────────────────────────────────
        // Live clash-data change events (stale-ref-safe auto-refresh)
        // ──────────────────────────────────────────────────────

        private void SubscribeClashChanges()
        {
            try
            {
                var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
                var clashPlugin = doc?.GetClash();
                if (clashPlugin == null) return;
                _subscribedTests = clashPlugin.TestsData;
                _subscribedTests.Changed += OnClashTestsChanged;
            }
            catch { _subscribedTests = null; }
        }

        private void UnsubscribeClashChanges()
        {
            try { if (_subscribedTests != null) _subscribedTests.Changed -= OnClashTestsChanged; }
            catch { }
            _subscribedTests = null;
        }

        /// <summary>
        /// Fires whenever clash test data changes (re-run, regroup, edit) — from us,
        /// from Clash Detective, or from undo/redo. Reloads the current test's clash
        /// list so we never hold disposed result objects and counts stay live.
        /// Suppressed during our own runs (we reload explicitly there instead).
        /// </summary>
        private void OnClashTestsChanged(object sender, Autodesk.Navisworks.Api.SavedItemChangedEventArgs e)
        {
            if (_suppressClashRefresh) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        RefreshClashTests();
                        string testName = GetSelectedTestName();
                        if (_allClashResults.Count > 0 && !string.IsNullOrEmpty(testName))
                            LoadClashesForTest(testName);
                    }
                    catch { /* refresh is best-effort */ }
                }));
            }
            catch { }
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
            // Fires automatically as the panel opens. An unhandled exception on the
            // WPF UI thread here would terminate Navisworks, so never let one escape.
            try
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
            catch (Exception ex)
            {
                try { txtStatus.Text = "Error loading test: " + ex.Message; } catch { }
                MessageBox.Show("Could not load this clash test's rules:\n\n" + ex,
                    "Clash Rule Engine", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnRefreshTests(object sender, RoutedEventArgs e)
        {
            RefreshClashTests();
        }

        private void OnShowMatrix(object sender, RoutedEventArgs e)
        {
            try
            {
                var tests = ClashTestScanner.GetClashTests();
                if (tests.Count == 0)
                {
                    MessageBox.Show("No clash tests found in this document.", "Clash Matrix",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var dlg = new ClashMatrixDialog(tests) { Owner = Window.GetWindow(this) };
                if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.SelectedTestName))
                {
                    if (SelectTestByName(dlg.SelectedTestName))
                    {
                        SetActiveTab(false);                       // jump to the Clashes tab
                        LoadClashesForTest(dlg.SelectedTestName);  // show that pair's clashes
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not build the matrix: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Selects the combo item whose (stripped) test name matches.</summary>
        private bool SelectTestByName(string testName)
        {
            foreach (var item in cmbClashTest.Items)
            {
                string s = item?.ToString();
                if (string.IsNullOrEmpty(s)) continue;
                int parenIdx = s.LastIndexOf(" (", StringComparison.Ordinal);
                string name = parenIdx > 0 ? s.Substring(0, parenIdx) : s;
                if (string.Equals(name, testName, StringComparison.OrdinalIgnoreCase))
                {
                    cmbClashTest.SelectedItem = item;
                    return true;
                }
            }
            return false;
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

        private void OnTabRulesClick(object sender, MouseButtonEventArgs e) => SetActiveTab(0);

        private void OnTabClashesClick(object sender, MouseButtonEventArgs e)
        {
            SetActiveTab(1);
            string testName = GetSelectedTestName();
            if (!string.IsNullOrEmpty(testName) && _allClashResults.Count == 0)
                LoadClashesForTest(testName);
        }

        private void OnTabHierarchyClick(object sender, MouseButtonEventArgs e) => SetActiveTab(2);
        private void OnTabListsClick(object sender, MouseButtonEventArgs e) => SetActiveTab(3);
        private void OnTabGeneralClick(object sender, MouseButtonEventArgs e) => SetActiveTab(4);

        /// <summary>Legacy bool overload (true = Rules, false = Clashes).</summary>
        private void SetActiveTab(bool rulesActive) => SetActiveTab(rulesActive ? 0 : 1);

        private void SetActiveTab(int idx)
        {
            // Leaving a setup tab commits its edits so nothing is lost on tab change.
            if (_activeTab >= 2 && idx != _activeTab) CommitSettingsTabs();

            _activeTab = idx;
            _rulesTabActive = idx == 0;

            pnlTabRules.Visibility     = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
            pnlTabClashes.Visibility   = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
            pnlTabHierarchy.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
            pnlTabLists.Visibility     = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
            pnlTabGeneral.Visibility   = idx == 4 ? Visibility.Visible : Visibility.Collapsed;

            SetTabStyle(bdTabRules,     txtTabRules,     idx == 0);
            SetTabStyle(bdTabClashes,   txtTabClashes,   idx == 1);
            SetTabStyle(bdTabHierarchy, txtTabHierarchy, idx == 2);
            SetTabStyle(bdTabLists,     txtTabLists,     idx == 3);
            SetTabStyle(bdTabGeneral,   txtTabGeneral,   idx == 4);
        }

        private void SetTabStyle(System.Windows.Controls.Border bd, TextBlock tx, bool active)
        {
            bd.BorderBrush = active ? Brush("#2563EB") : Brushes.Transparent;
            tx.Foreground  = active ? Brush("#2563EB") : Brush("#9CA3AF");
            tx.FontWeight  = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        // ──────────────────────────────────────────────────────
        // Setup tabs: Hierarchy / Assignees & Groups / General
        // ──────────────────────────────────────────────────────

        private void LoadSettingsTabs()
        {
            if (_config == null) return;
            _config.Hierarchy?.EnsureSeeded();
            AvailableAssignees = _config.GetAllAssignees();

            _disciplines = new System.Collections.ObjectModel.ObservableCollection<DisciplineDefinition>(
                (_config.Hierarchy?.Disciplines ?? new List<DisciplineDefinition>()).Select(CloneDiscipline));
            lstDisciplines.ItemsSource = _disciplines;

            chkFallback.IsChecked = _config.UseHierarchyFallback;

            cmbGroupMode.SelectedIndex = (int)_config.GroupingMode;
            txtThreshold.Text = _config.ProximityThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture);
            chkAssignByGroup.IsChecked = _config.AssignByGroup;

            txtAssignees.Text = string.Join(Environment.NewLine, _config.Assignees ?? new List<string>());
            txtGroups.Text = string.Join(Environment.NewLine, _config.GroupNames ?? new List<string>());
            txtProjectName.Text = _config.ProjectName;
            txtApiKey.Text = _config.ApiKey;
            txtConfigStats.Text = $"{_config.TestRuleSets.Count} test rule set(s) configured.";
        }

        private void CommitSettingsTabs()
        {
            if (_config == null || _disciplines == null) return;
            CommitFocusedEdit();

            _config.UseHierarchyFallback = chkFallback.IsChecked ?? true;

            int gm = cmbGroupMode.SelectedIndex;
            if (gm >= 0 && Enum.IsDefined(typeof(ClashGroupingMode), gm))
                _config.GroupingMode = (ClashGroupingMode)gm;
            if (double.TryParse(txtThreshold.Text, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double thr) && thr > 0)
                _config.ProximityThreshold = thr;
            _config.AssignByGroup = chkAssignByGroup.IsChecked ?? false;

            if (_config.Hierarchy == null) _config.Hierarchy = new SystemHierarchy();
            _config.Hierarchy.Disciplines = _disciplines.Where(d => !string.IsNullOrWhiteSpace(d.Name)).ToList();
            _config.Assignees = SplitLines(txtAssignees.Text);
            _config.GroupNames = SplitLines(txtGroups.Text);
            _config.ProjectName = string.IsNullOrWhiteSpace(txtProjectName.Text) ? "Untitled Project" : txtProjectName.Text.Trim();
            _config.ApiKey = txtApiKey.Text?.Trim() ?? "";
        }

        private void OnSaveSetup(object sender, RoutedEventArgs e)
        {
            try
            {
                CommitSettingsTabs();
                RulePersistenceService.Save(_config);
                AvailableAssignees = _config.GetAllAssignees();   // refresh owner suggestions
                UpdateUI();
                txtStatus.Text = "Settings saved.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnMoveUp(object sender, RoutedEventArgs e)
        {
            var d = (sender as Button)?.Tag as DisciplineDefinition;
            if (d == null || _disciplines == null) return;
            int i = _disciplines.IndexOf(d);
            if (i > 0) _disciplines.Move(i, i - 1);
        }

        private void OnMoveDown(object sender, RoutedEventArgs e)
        {
            var d = (sender as Button)?.Tag as DisciplineDefinition;
            if (d == null || _disciplines == null) return;
            int i = _disciplines.IndexOf(d);
            if (i >= 0 && i < _disciplines.Count - 1) _disciplines.Move(i, i + 1);
        }

        private void OnDeleteDiscipline(object sender, RoutedEventArgs e)
        {
            var d = (sender as Button)?.Tag as DisciplineDefinition;
            if (d != null) _disciplines?.Remove(d);
        }

        private void OnAddDiscipline(object sender, RoutedEventArgs e)
        {
            _disciplines?.Add(new DisciplineDefinition { Name = "New Discipline", Color = "#6B7280" });
        }

        private void OnRestoreDefaults(object sender, RoutedEventArgs e)
        {
            if (_disciplines == null) return;
            if (MessageBox.Show(
                    "Replace the current disciplines with the standard defaults?\nYour keyword/assignee edits in this list will be lost.",
                    "Restore Defaults", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _disciplines.Clear();
            foreach (var d in SystemHierarchy.DefaultDisciplines()) _disciplines.Add(d);
        }

        private static DisciplineDefinition CloneDiscipline(DisciplineDefinition d) => new DisciplineDefinition
        {
            Name = d.Name,
            Keywords = new List<string>(d.Keywords ?? new List<string>()),
            Assignee = d.Assignee,
            AssigneeMode = d.AssigneeMode,
            GroupName = d.GroupName,
            Color = d.Color
        };

        private void CommitFocusedEdit()
        {
            if (Keyboard.FocusedElement is TextBox tb)
                tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            else if (Keyboard.FocusedElement is ComboBox cb)
                cb.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
        }

        private static List<string> SplitLines(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            return text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(s => s.Trim())
                       .Where(s => s.Length > 0)
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();
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

            RefreshMarkers();
        }

        // ──────────────────────────────────────────────────────
        // 3D clash markers (overlay shared with the Render/Input plugins)
        // ──────────────────────────────────────────────────────

        /// <summary>Pushes the currently visible clashes to the marker overlay.</summary>
        private void RefreshMarkers()
        {
            try
            {
                var markers = _clashResults
                    .Where(r => r.Center != null && r.ResultGuid != Guid.Empty)
                    .Select(r => new ClashMarkerService.Marker
                    {
                        ResultGuid = r.ResultGuid,
                        Center = r.Center,
                        Color = ClashMarkerService.ColorForStatus(r.Status)
                    });
                ClashMarkerService.SetMarkers(markers);
                if (ClashMarkerService.Enabled) ClashMarkerService.RequestRedraw();
            }
            catch { /* markers are best-effort */ }
        }

        private void OnToggleMarkers(object sender, RoutedEventArgs e)
        {
            ClashMarkerService.Enabled = btnMarkers.IsChecked == true;
            if (ClashMarkerService.Enabled) RefreshMarkers();
            ClashMarkerService.RequestRedraw();
        }

        /// <summary>
        /// A marker was clicked in the 3D view (fired from the InputPlugin, possibly
        /// off the UI thread). Select the matching clash in the list and frame it.
        /// </summary>
        private void OnMarkerClicked(Guid guid)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        ClashMarkerService.SelectedMarker = guid;
                        var match = _clashResults.FirstOrDefault(r => r.ResultGuid == guid);
                        if (match != null)
                        {
                            lstClashes.SelectedItem = match;   // triggers navigate via OnClashSelectionChanged
                            lstClashes.ScrollIntoView(match);
                        }
                        else
                        {
                            // Visible filter hides it — still navigate to the clash.
                            ClashNavigationService.NavigateTo(guid);
                        }
                        ClashMarkerService.RequestRedraw();
                    }
                    catch { }
                }));
            }
            catch { }
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
            if (info == null) return;
            // Keep the 3D marker highlight in sync with the list selection.
            ClashMarkerService.SelectedMarker = info.ResultGuid;
            if (ClashMarkerService.Enabled) ClashMarkerService.RequestRedraw();
            // Resolve a fresh live result by GUID — never touch a held (possibly
            // disposed) object, which would crash Navisworks.
            try { ClashNavigationService.NavigateTo(info.ResultGuid); }
            catch { /* navigation is best-effort; never disrupt the UI */ }
        }

        private void OnInspectClash(object sender, RoutedEventArgs e)
        {
            var info = (sender as Button)?.Tag as ClashResultInfo;
            var live = info != null ? ClashNavigationService.ResolveLive(info.ResultGuid) : null;
            if (live == null)
            {
                MessageBox.Show("Clash data is no longer available (the test was re-run). Click Refresh to reload the clash list.",
                    "Data Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var inspector = new ClashInspectorDialog(live, _config, _currentTestRuleSet);
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

            try
            {
                var dialog = new RuleEditorDialog(newRule, _config);
                dialog.Owner = Window.GetWindow(this);
                if (dialog.ShowDialog() == true)
                {
                    _rules.Add(dialog.Rule);
                    SyncRulesToTestSet();
                    UpdateUI();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the rule editor:\n\n" + ex.Message,
                    "Clash Rule Engine", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnEditRule(object sender, RoutedEventArgs e)
        {
            var rule = (sender as Button)?.Tag as ClashRule;
            if (rule == null) return;
            try
            {
                var dialog = new RuleEditorDialog(rule, _config);
                dialog.Owner = Window.GetWindow(this);
                if (dialog.ShowDialog() == true)
                {
                    int idx = _rules.IndexOf(rule);
                    if (idx >= 0) { _rules[idx] = dialog.Rule; SyncRulesToTestSet(); }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the rule editor:\n\n" + ex.Message,
                    "Clash Rule Engine", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        /// <summary>
        /// Compact per-test assignment summary across ALL tests (or the selected
        /// one) — totals, status, and per-assignee element-type patterns. Tiny
        /// output, meant to be pasted into an AI prompt.
        /// </summary>
        private void OnExportSummary(object sender, RoutedEventArgs e)
        {
            try
            {
                var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
                if (doc == null || string.IsNullOrEmpty(doc.FileName))
                {
                    MessageBox.Show("Open a document first.", "No Document", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Default to ALL tests (the summary is the cross-test overview);
                // offer to narrow to the selected test.
                string onlyTest = null;
                string selected = GetSelectedTestName();
                if (selected != null)
                {
                    var choice = MessageBox.Show(
                        "Summarise ALL tests?\n\n" +
                        "Yes — every test (recommended — this is the overview)\n" +
                        $"No — only the selected test '{selected}'",
                        "Summary Scope", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (choice == MessageBoxResult.Cancel) return;
                    if (choice == MessageBoxResult.No) onlyTest = selected;
                }

                string baseName = System.IO.Path.GetFileNameWithoutExtension(doc.FileName);

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export assignment summary",
                    Filter = "Summary JSON (*.summary.json)|*.summary.json|All files (*.*)|*.*",
                    InitialDirectory = System.IO.Path.GetDirectoryName(doc.FileName),
                    FileName = baseName + ".summary.json"
                };
                if (dlg.ShowDialog() != true) return;

                var progress = new ExportProgressWindow();
                progress.Show();
                string summary;
                try
                {
                    summary = SessionExportService.ExportSummary(
                        dlg.FileName, onlyTest, progress.Report, () => progress.Cancelled);
                }
                finally
                {
                    progress.Close();
                }
                MessageBox.Show(summary, "Assignment Summary", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Summary failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            _suppressClashRefresh = true;
            var progress = new ExportProgressWindow("Applying rules");
            progress.Show();
            try
            {
                SyncRulesToTestSet();
                CommitSettingsTabs();   // capture the latest grouping/hierarchy UI
                _processor.ApplyGroupingSettings(_config);
                var result = _processor.ProcessSingleTest(testName, _currentTestRuleSet,
                    _config.Hierarchy, _config.UseHierarchyFallback,
                    progress.Report, () => progress.Cancelled);
                progress.Close();
                ShowResults(result);

                // Refresh the clash list if it's loaded, to reflect updated statuses
                if (_allClashResults.Count > 0)
                    LoadClashesForTest(testName);
            }
            catch (Exception ex)
            {
                progress.Close();
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { try { progress.Close(); } catch { } _suppressClashRefresh = false; }
        }

        private void OnRunAllTests(object sender, RoutedEventArgs e)
        {
            _suppressClashRefresh = true;
            var progress = new ExportProgressWindow("Applying rules — all tests");
            progress.Show();
            try
            {
                SyncRulesToTestSet();
                CommitSettingsTabs();   // capture the latest grouping/hierarchy UI
                _processor.ApplyGroupingSettings(_config);
                var result = _processor.ProcessAllTests(_config, progress.Report, () => progress.Cancelled);
                progress.Close();
                ShowResults(result);

                // Running rules swaps each test and disposes the old ClashResult
                // objects our list holds. Reload so we never navigate stale refs.
                string testName = GetSelectedTestName();
                if (_allClashResults.Count > 0 && !string.IsNullOrEmpty(testName))
                    LoadClashesForTest(testName);
            }
            catch (Exception ex)
            {
                progress.Close();
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { try { progress.Close(); } catch { } _suppressClashRefresh = false; }
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
                txtResultWarningsHeader.Text = $"{result.Errors.Count} warning{(result.Errors.Count != 1 ? "s" : "")}";
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

        /// <summary>
        /// Imports a .clashre config from anywhere (rules, hierarchy, assignees,
        /// groups, API key), makes it the active config, and saves it alongside the
        /// current document so it persists. The Rules tab repopulates from the
        /// imported per-test rules for whatever test is selected.
        /// </summary>
        private void OnImportConfig(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Import config (.clashre) or kind-rules (.json)",
                    Filter = "Config or kind rules (*.clashre;*.json)|*.clashre;*.json|All files (*.*)|*.*",
                    CheckFileExists = true
                };
                if (dlg.ShowDialog() != true) return;

                string text = System.IO.File.ReadAllText(dlg.FileName);

                // Kind-rule list (the summary response derived from the batch JSONL):
                // merge into the CURRENT config — keeps your rules/grouping, adds the
                // element-kind hierarchy + trade taxonomy.
                if (KindRuleImport.LooksLikeKindRules(text))
                {
                    var parsed = KindRuleImport.Parse(text);
                    _config.KindRules = parsed.Rules;
                    _config.UseKindRules = true;
                    if (parsed.Trades != null && parsed.Trades.Count > 0)
                        _config.Hierarchy.Disciplines = parsed.Trades;   // for owner/other resolution
                    RulePersistenceService.Save(_config);
                    LoadSettingsTabs();
                    UpdateUI();
                    MessageBox.Show(
                        $"Imported {parsed.Rules.Count} element-kind rule(s)" +
                        (parsed.Trades.Count > 0 ? $" and {parsed.Trades.Count} trade(s)" : "") +
                        ".\n\nThese now drive assignment (after any per-test rules, before the trade fallback).",
                        "Kind rules imported", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var imported = ProjectConfig.FromXml(text);
                if (imported == null)
                {
                    MessageBox.Show("That file could not be read as a .clashre config.",
                        "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Grouping is an ADDIN setting, not dictated by an imported file —
                // import brings in rules + hierarchy + lists; the grouping you've set
                // in the panel wins. Capture current settings and re-apply them.
                try { CommitSettingsTabs(); } catch { }
                var keepMode = _config?.GroupingMode ?? Models.ClashGroupingMode.None;
                var keepThreshold = (_config?.ProximityThreshold ?? 1.0) > 0 ? _config.ProximityThreshold : 1.0;
                var keepAssignByGroup = _config?.AssignByGroup ?? false;

                _config = imported;
                _config.Hierarchy?.EnsureSeeded();
                _config.GroupingMode = keepMode;
                _config.ProximityThreshold = keepThreshold;
                _config.AssignByGroup = keepAssignByGroup;

                // Persist into THIS document's sidecar so it loads next time too.
                RulePersistenceService.Save(_config);

                // Repopulate everything from the imported config.
                RefreshClashTests();                 // re-selects a test → reloads its rules
                LoadSettingsTabs();                  // refresh Hierarchy / Lists / General tabs
                string testName = GetSelectedTestName();
                if (!string.IsNullOrEmpty(testName))
                {
                    _currentTestRuleSet = _config.GetOrCreateTestRuleSet(testName);
                    _currentTestRuleSet.ReindexPriorities();
                    _rules.Clear();
                    foreach (var r in _currentTestRuleSet.Rules) _rules.Add(r);
                }
                SetActiveTab(true);   // show the Rules tab so the imported rules are visible
                UpdateUI();

                int ruleCount = _config.TestRuleSets?.Sum(t => t.Rules.Count) ?? 0;
                int discCount = _config.Hierarchy?.Disciplines?.Count ?? 0;
                MessageBox.Show(
                    $"Imported config:\n\n• {ruleCount} rule(s) across {_config.TestRuleSets?.Count ?? 0} test(s)\n" +
                    $"• {discCount} discipline(s) in the hierarchy\n• hierarchy fallback: {(_config.UseHierarchyFallback ? "on" : "off")}\n\n" +
                    "Saved alongside this document.",
                    "Import complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed: {ex.Message}", "Import", MessageBoxButton.OK, MessageBoxImage.Error);
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
