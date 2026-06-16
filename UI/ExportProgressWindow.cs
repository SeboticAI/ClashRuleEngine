using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClashRuleEngine.UI
{
    /// <summary>
    /// Lightweight progress window for long-running exports. Code-only (no XAML).
    /// The Navisworks API must be called from the main thread, so the export runs
    /// there too — Pump() keeps this window responsive between work items.
    /// </summary>
    public class ExportProgressWindow : Window
    {
        private readonly TextBlock _status;
        public bool Cancelled { get; private set; }

        public ExportProgressWindow(string title = "Exporting session")
        {
            Title = title;
            Width = 460;
            Height = 150;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            Background = Brushes.White;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _status = new TextBlock
            {
                Text = "Starting…",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51))
            };
            grid.Children.Add(_status);

            var btn = new Button
            {
                Content = "Cancel",
                Width = 90,
                Height = 26,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            btn.Click += (s, e) => { Cancelled = true; _status.Text = "Cancelling…"; };
            Grid.SetRow(btn, 2);
            grid.Children.Add(btn);

            Content = grid;
        }

        public void Report(string message)
        {
            _status.Text = message;
            Pump();
        }

        /// <summary>Processes pending UI messages so the window repaints and the
        /// Cancel button works while the export occupies the main thread.</summary>
        public void Pump()
        {
            Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        }
    }
}
