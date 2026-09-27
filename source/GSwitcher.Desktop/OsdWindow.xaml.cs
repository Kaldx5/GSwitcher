using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GSwitcher
{
    public partial class OsdWindow : Window
    {
        private readonly string[] _modes = { "ultra", "daily", "battery" };
        private int _selectedIndex;

        public OsdWindow(string currentMode)
        {
            InitializeComponent();
            _selectedIndex = IndexForMode(currentMode);
            UpdateSelection();
        }

        public event Action<string> ModeAccepted;

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Activate();
            Focus();
            Keyboard.Focus(this);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Left)
            {
                MoveSelection(-1);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Right)
            {
                MoveSelection(1);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                var handler = ModeAccepted;
                if (handler != null)
                {
                    handler(_modes[_selectedIndex]);
                }

                Close();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        private void MoveSelection(int delta)
        {
            _selectedIndex += delta;
            if (_selectedIndex < 0)
            {
                _selectedIndex = _modes.Length - 1;
            }
            else if (_selectedIndex >= _modes.Length)
            {
                _selectedIndex = 0;
            }

            UpdateSelection();
        }

        private void UpdateSelection()
        {
            SetCard(UltraCard, UltraTitle, _selectedIndex == 0, Color.FromRgb(242, 53, 98));
            SetCard(DailyCard, DailyTitle, _selectedIndex == 1, Color.FromRgb(24, 216, 233));
            SetCard(BatteryCard, BatteryTitle, _selectedIndex == 2, Color.FromRgb(241, 184, 74));
        }

        private static void SetCard(Border card, TextBlock title, bool selected, Color accent)
        {
            var accentBrush = new SolidColorBrush(accent);
            var background = selected
                ? new SolidColorBrush(Color.FromArgb(72, accent.R, accent.G, accent.B))
                : new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));

            card.Background = background;
            card.BorderBrush = selected ? accentBrush : new SolidColorBrush(Color.FromArgb(38, 255, 255, 255));
            card.Effect = selected
                ? new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = accent,
                    ShadowDepth = 0,
                    BlurRadius = 24,
                    Opacity = 0.55
                }
                : null;

            title.Foreground = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(150, 157, 180));
        }

        private static int IndexForMode(string mode)
        {
            mode = (mode ?? string.Empty).Trim().ToLowerInvariant();
            if (mode == "ultra" || mode == "tested-ultra")
            {
                return 0;
            }

            if (mode == "battery" || mode == "eco" || mode == "tested-eco")
            {
                return 2;
            }

            return 1;
        }
    }
}
