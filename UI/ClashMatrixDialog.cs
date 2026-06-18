using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClashRuleEngine.Services;

namespace ClashRuleEngine.UI
{
    /// <summary>
    /// Read-only clash matrix: every clash test laid out as a discipline-vs-discipline
    /// grid, coloured by how many active clashes remain. Test-pair axes are parsed
    /// from the test names ("MC v EC", "HC vs SC", …). Clicking a populated cell
    /// selects that test in the main panel. Code-only (no XAML), like
    /// ExportProgressWindow, to keep the build simple.
    /// </summary>
    public class ClashMatrixDialog : Window
    {
        /// <summary>The test the user clicked, or null if they just closed the dialog.</summary>
        public string SelectedTestName { get; private set; }

        private sealed class Cell
        {
            public string TestName;
            public int Total;
            public int Active;
        }

        // Preferred axis order by discipline code (system hierarchy precedence).
        private static readonly Dictionary<string, int> Order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "SC", 0 }, // Structural
            { "MC", 1 }, // Mechanical
            { "HC", 2 }, // Hydraulic
            { "FC", 3 }, // Fire
            { "EC", 4 }, // Electrical
        };

        public ClashMatrixDialog(IEnumerable<ClashTestInfo> tests)
        {
            Title = "Clash Matrix";
            Width = 720;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xF9, 0xFA));

            var list = (tests ?? Enumerable.Empty<ClashTestInfo>()).ToList();

            var axes = new List<string>();
            var cells = new Dictionary<string, Cell>(StringComparer.OrdinalIgnoreCase);
            int unparsed = 0;

            foreach (var t in list)
            {
                string a, b;
                if (!TryParsePair(t.Name, out a, out b)) { unparsed++; continue; }
                AddAxis(axes, a);
                AddAxis(axes, b);

                string key = PairKey(a, b);
                if (!cells.TryGetValue(key, out var c))
                    cells[key] = c = new Cell { TestName = t.Name };
                c.Total += t.TotalClashes;
                c.Active += t.ActiveClashes;
            }

            axes = axes.OrderBy(AxisRank).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

            Content = BuildContent(axes, cells, unparsed, list.Count);
        }

        private UIElement BuildContent(List<string> axes, Dictionary<string, Cell> cells, int unparsed, int testCount)
        {
            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new TextBlock
            {
                Text = "Clash matrix — active / total per test pair. Click a cell to open that test.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E)),
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(header);

            if (axes.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = testCount == 0
                        ? "No clash tests found in this document."
                        : "Could not parse discipline pairs from the test names (expected e.g. \"MC v EC\").",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                Grid.SetRow(empty, 1);
                root.Children.Add(empty);
            }
            else
            {
                var scroll = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };
                Grid.SetRow(scroll, 1);
                scroll.Content = BuildMatrixGrid(axes, cells);
                root.Children.Add(scroll);
            }

            var footer = new TextBlock
            {
                Text = unparsed > 0 ? $"{unparsed} test(s) not shown (name not a recognisable A v B pair)." : "",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                Margin = new Thickness(0, 12, 0, 0)
            };
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            return root;
        }

        private Grid BuildMatrixGrid(List<string> axes, Dictionary<string, Cell> cells)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // corner
            for (int i = 0; i < axes.Count; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });       // headers
            for (int i = 0; i < axes.Count; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });

            // Column headers
            for (int c = 0; c < axes.Count; c++)
            {
                var h = HeaderCell(axes[c]);
                Grid.SetRow(h, 0);
                Grid.SetColumn(h, c + 1);
                grid.Children.Add(h);
            }

            for (int r = 0; r < axes.Count; r++)
            {
                // Row header
                var rh = HeaderCell(axes[r]);
                Grid.SetRow(rh, r + 1);
                Grid.SetColumn(rh, 0);
                grid.Children.Add(rh);

                for (int c = 0; c < axes.Count; c++)
                {
                    UIElement el;
                    if (r == c)
                    {
                        el = DiagonalCell();
                    }
                    else
                    {
                        cells.TryGetValue(PairKey(axes[r], axes[c]), out var cell);
                        el = DataCell(cell);
                    }
                    Grid.SetRow(el, r + 1);
                    Grid.SetColumn(el, c + 1);
                    grid.Children.Add(el);
                }
            }

            return grid;
        }

        private static Border HeaderCell(string text)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xEF, 0xF6, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(1),
                Child = new TextBlock
                {
                    Text = text,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        private static Border DiagonalCell()
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
                Margin = new Thickness(1)
            };
        }

        private UIElement DataCell(Cell cell)
        {
            if (cell == null)
            {
                return new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = "—",
                        Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
            }

            Color bg, fg;
            if (cell.Active > 0)       { bg = Color.FromRgb(0xFE, 0xF2, 0xF2); fg = Color.FromRgb(0xDC, 0x26, 0x26); } // red
            else if (cell.Total > 0)  { bg = Color.FromRgb(0xF0, 0xFD, 0xF4); fg = Color.FromRgb(0x16, 0xA3, 0x4A); } // green
            else                      { bg = Color.FromRgb(0xF9, 0xFA, 0xFB); fg = Color.FromRgb(0x6B, 0x72, 0x80); } // empty

            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new TextBlock
            {
                Text = cell.Active.ToString(),
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                Foreground = new SolidColorBrush(fg),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = "/ " + cell.Total,
                FontSize = 10,
                Foreground = new SolidColorBrush(fg),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            var btn = new Button
            {
                Content = content,
                Background = new SolidColorBrush(bg),
                Foreground = new SolidColorBrush(fg),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"{cell.TestName}\n{cell.Active} active of {cell.Total} clashes"
            };
            btn.Click += (s, e) =>
            {
                SelectedTestName = cell.TestName;
                DialogResult = true;
                Close();
            };
            return btn;
        }

        // ── name parsing ─────────────────────────────────────────────

        private static readonly Regex PairRegex =
            new Regex(@"^\s*(.+?)\s*(?:\bvs?\b|/|—|-)\s*(.+?)\s*$", RegexOptions.IgnoreCase);

        private static bool TryParsePair(string name, out string a, out string b)
        {
            a = b = null;
            if (string.IsNullOrWhiteSpace(name)) return false;
            var m = PairRegex.Match(name);
            if (!m.Success) return false;
            a = m.Groups[1].Value.Trim();
            b = m.Groups[2].Value.Trim();
            return a.Length > 0 && b.Length > 0 && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static void AddAxis(List<string> axes, string a)
        {
            if (!axes.Any(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase)))
                axes.Add(a);
        }

        private static int AxisRank(string code)
        {
            return Order.TryGetValue(code, out int r) ? r : 99;
        }

        private static string PairKey(string a, string b)
        {
            // Unordered pair key so "MC v EC" and "EC v MC" collide.
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0
                ? a.ToUpperInvariant() + "|" + b.ToUpperInvariant()
                : b.ToUpperInvariant() + "|" + a.ToUpperInvariant();
        }
    }
}
