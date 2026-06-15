using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ClashRuleEngine.Models;
using ClashRuleEngine.Services;

namespace ClashRuleEngine.UI
{
    public partial class AiAssistDialog : Window
    {
        private readonly ProjectConfig _config;
        private readonly string _testName;
        private readonly List<ClashResultInfo> _clashes;

        /// <summary>Rules the user accepted (empty until "Add Rules").</summary>
        public List<ClashRule> GeneratedRules { get; private set; } = new List<ClashRule>();

        private List<ClashRule> _proposed = new List<ClashRule>();

        public AiAssistDialog(ProjectConfig config, string testName, IEnumerable<ClashResultInfo> clashes)
        {
            InitializeComponent();
            _config = config;
            _testName = testName;
            _clashes = (clashes ?? Enumerable.Empty<ClashResultInfo>()).ToList();

            int assigned = _clashes.Count(c => c?.SourceResult != null);
            txtSubtitle.Text = $"Test: {testName}  ·  {_clashes.Count} example clash(es) available  ·  "
                + (string.IsNullOrWhiteSpace(_config?.ApiKey)
                    ? "⚠ No API key — add one in Settings → General"
                    : "API key set");
        }

        private async void OnGenerate(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_config?.ApiKey))
            {
                MessageBox.Show("Add your Claude API key in Settings → General first.",
                    "No API Key", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Build the prompt on the UI (main) thread — reading clash properties
            // touches the Navisworks API, which is main-thread-only.
            string system, user;
            try
            {
                system = AiRuleGenerator.BuildSystemPrompt(_config);
                user = AiRuleGenerator.BuildUserPrompt(_testName, txtInstructions.Text, _clashes);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not build the request: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SetBusy(true, "Asking Claude to write the rules… (this can take up to a minute)");
            try
            {
                string response = await ClaudeApiService.SendAsync(_config.ApiKey, system, user);
                _proposed = AiRuleGenerator.ParseRules(response);

                lstProposed.ItemsSource = null;
                lstProposed.ItemsSource = _proposed;
                txtEmpty.Visibility = _proposed.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
                btnAccept.IsEnabled = _proposed.Count > 0;

                txtStatus.Text = _proposed.Count > 0
                    ? $"Claude proposed {_proposed.Count} rule(s). Review, then Add."
                    : "Claude returned no rules. Try giving more detail or assign a few clashes first.";
            }
            catch (ClaudeApiException ex)
            {
                txtStatus.Text = "";
                MessageBox.Show(ex.Message, "Claude", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                txtStatus.Text = "";
                MessageBox.Show($"Unexpected error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private void SetBusy(bool busy, string status)
        {
            btnGenerate.IsEnabled = !busy;
            Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
            if (status != null) txtStatus.Text = status;
        }

        private void OnAccept(object sender, RoutedEventArgs e)
        {
            GeneratedRules = _proposed ?? new List<ClashRule>();
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
