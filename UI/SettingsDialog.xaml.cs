using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClashRuleEngine.Models;

namespace ClashRuleEngine.UI
{
    public partial class SettingsDialog : Window
    {
        private readonly ProjectConfig _config;
        private readonly ObservableCollection<DisciplineDefinition> _disciplines;

        /// <summary>Assignee suggestions for the per-discipline owner dropdowns.</summary>
        public List<string> AvailableAssignees { get; private set; }

        public SettingsDialog(ProjectConfig config)
        {
            InitializeComponent();
            _config = config;
            _config.Hierarchy?.EnsureSeeded();

            AvailableAssignees = _config.GetAllAssignees();

            // Work on clones so Cancel discards edits cleanly.
            _disciplines = new ObservableCollection<DisciplineDefinition>(
                _config.Hierarchy.Disciplines.Select(Clone));
            lstDisciplines.ItemsSource = _disciplines;

            chkFallback.IsChecked = _config.UseHierarchyFallback;
            txtAssignees.Text = string.Join(Environment.NewLine, _config.Assignees ?? new List<string>());
            txtGroups.Text = string.Join(Environment.NewLine, _config.GroupNames ?? new List<string>());
            txtProjectName.Text = _config.ProjectName;
            txtApiKey.Text = _config.ApiKey;
            txtConfigStats.Text = $"{_config.TestRuleSets.Count} test rule set(s) configured.";
        }

        private static DisciplineDefinition Clone(DisciplineDefinition d) => new DisciplineDefinition
        {
            Name = d.Name,
            Keywords = new List<string>(d.Keywords ?? new List<string>()),
            Assignee = d.Assignee,
            GroupName = d.GroupName,
            Color = d.Color
        };

        // ── Discipline list operations ─────────────────────────────────────────

        private void OnMoveUp(object sender, RoutedEventArgs e)
        {
            var d = (sender as Button)?.Tag as DisciplineDefinition;
            if (d == null) return;
            int i = _disciplines.IndexOf(d);
            if (i > 0) _disciplines.Move(i, i - 1);
        }

        private void OnMoveDown(object sender, RoutedEventArgs e)
        {
            var d = (sender as Button)?.Tag as DisciplineDefinition;
            if (d == null) return;
            int i = _disciplines.IndexOf(d);
            if (i >= 0 && i < _disciplines.Count - 1) _disciplines.Move(i, i + 1);
        }

        private void OnDeleteDiscipline(object sender, RoutedEventArgs e)
        {
            var d = (sender as Button)?.Tag as DisciplineDefinition;
            if (d != null) _disciplines.Remove(d);
        }

        private void OnAddDiscipline(object sender, RoutedEventArgs e)
        {
            _disciplines.Add(new DisciplineDefinition { Name = "New Discipline", Color = "#6B7280" });
        }

        private void OnRestoreDefaults(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(
                    "Replace the current disciplines with the standard defaults?\nYour keyword/assignee edits in this list will be lost.",
                    "Restore Defaults", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _disciplines.Clear();
            foreach (var d in SystemHierarchy.DefaultDisciplines())
                _disciplines.Add(d);
        }

        // ── Save / Cancel ──────────────────────────────────────────────────────

        private void OnSave(object sender, RoutedEventArgs e)
        {
            CommitFocusedEdit();   // flush a field the user is still editing

            _config.UseHierarchyFallback = chkFallback.IsChecked ?? true;
            _config.Hierarchy.Disciplines = _disciplines
                .Where(d => !string.IsNullOrWhiteSpace(d.Name))
                .ToList();
            _config.Assignees = SplitLines(txtAssignees.Text);
            _config.GroupNames = SplitLines(txtGroups.Text);
            _config.ProjectName = string.IsNullOrWhiteSpace(txtProjectName.Text)
                ? "Untitled Project" : txtProjectName.Text.Trim();
            _config.ApiKey = txtApiKey.Text?.Trim() ?? "";

            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// LostFocus bindings don't fire when the user clicks Save while a field is
        /// still focused — push that field's value to its source explicitly.
        /// </summary>
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
    }
}
