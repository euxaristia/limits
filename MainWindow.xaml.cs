using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.UI;
using H.NotifyIcon;
using CodexBarWin.Core;

namespace CodexBarWin
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;

        // DWM's COLORREF is 0x00BBGGRR, not ARGB. Helper avoids the encoding confusion.
        private static int ColorRef(byte r, byte g, byte b) =>
            (b << 16) | (g << 8) | r;

        private readonly TaskbarIcon _taskbarIcon;
        private bool _isExiting = false;

        public ObservableCollection<ProviderViewModel> Providers { get; } = new();
        public ObservableCollection<ProviderTabItem> Tabs { get; } = new();
        public ObservableCollection<ProviderSettingItem> SettingsItems { get; } = new();

        // Tab/body state. Defaults to Overview so the first time the popup
        // opens, the user sees the at-a-glance summary across all providers.
        private bool _overviewMode = true;
        public bool OverviewMode
        {
            get => _overviewMode;
            set
            {
                if (_overviewMode != value)
                {
                    _overviewMode = value;
                    BindBody();
                }
            }
        }

        private ProviderViewModel? _selectedProvider;
        public ProviderViewModel? SelectedProvider
        {
            get => _selectedProvider;
            set
            {
                if (_selectedProvider != value)
                {
                    _selectedProvider = value;
                    BindBody();
                }
            }
        }

        public MainWindow()
        {
            this.InitializeComponent();

            // Set up borderless style and dimensions
            ConfigureWindow();

            // Setup programmatic System Tray Icon
            _taskbarIcon = new TaskbarIcon
            {
                IconSource = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico")),
                ToolTipText = "CodexBar"
            };
            _taskbarIcon.LeftClickCommand = new RelayCommand(() => ToggleWindow());
            _taskbarIcon.RightClickCommand = new RelayCommand(() => ToggleWindow());
            _taskbarIcon.ForceCreate();

            // Custom deactivation behavior (hide when user clicks away)
            this.Activated += MainWindow_Activated;

            // Handle close button click (close to tray)
            this.Closed += MainWindow_Closed;

            // Initial body binding (Overview is selected by default).
            BindBody();

            // Load configurations and fetch usage data
            LoadConfigAndRefresh();
        }

        private void ConfigureWindow()
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            // Hide from Taskbar/Alt+Tab
            appWindow.IsShownInSwitchers = false;

            // Make borderless
            if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }

            // DWM attributes must be applied immediately after the HWND exists,
            // before the window is shown or restyled - otherwise the calls are
            // silently ignored and we see the default light frame.
            ApplyDarkFrame(hWnd);

            PositionWindow();
        }

        private void ApplyDarkFrame(IntPtr hWnd)
        {
            // #1F1F1F - neutral near-black that matches the popup's flat
            // background (#C018181A in MainWindow.xaml). DWM wants 0x00BBGGRR.
            int darkBorder = ColorRef(0x1F, 0x1F, 0x1F);

            int hr1 = DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref _useDark, sizeof(int));
            int hr2 = DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref darkBorder, sizeof(int));

            if (hr1 != 0 || hr2 != 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CodexBarWin] DwmSetWindowAttribute failed: immersiveDark={hr1}, borderColor={hr2}");
            }
        }

        private static int _useDark = 1;

        private void PositionWindow()
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            RectInt32 bounds = displayArea.WorkArea;

            int width = 420;
            int height = 620;

            // Position at bottom-right corner (just above the system tray)
            int x = bounds.X + bounds.Width - width - 12;
            int y = bounds.Y + bounds.Height - height - 12;

            appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                this.AppWindow.Hide();
            }
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            if (!_isExiting)
            {
                args.Handled = true;
                this.AppWindow.Hide();
            }
        }

        private void ToggleWindow()
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var appWindow = this.AppWindow;

            if (appWindow.IsVisible)
            {
                appWindow.Hide();
            }
            else
            {
                PositionWindow();
                appWindow.Show();
                SetForegroundWindow(hWnd);
            }
        }

        private async void LoadConfigAndRefresh()
        {
            Providers.Clear();
            BuildTabs();

            // 1. Load config from F# Core
            var config = ConfigStore.load();

            // 2. Map enabled providers to viewmodels
            var activeConfigs = config.providers.Where(p => p.enabled.HasValue && p.enabled.Value).ToList();

            // Create view models
            var tasks = activeConfigs.Select(async providerConfig =>
            {
                var usage = await UsageFetcher.fetch(providerConfig);
                return new ProviderViewModel(usage);
            });

            var viewModels = await Task.WhenAll(tasks);
            foreach (var vm in viewModels)
            {
                Providers.Add(vm);
            }
        }

        /// <summary>
        /// Rebuilds the tabs strip from the current Providers list. The first tab
        /// is always "Overview"; the rest are the providers in arrival order.
        /// </summary>
        private void BuildTabs()
        {
            Tabs.Clear();
            Tabs.Add(new ProviderTabItem(UsageProvider.Unknown, "Overview", "\uE9D9", isOverview: true));
            // Add a placeholder tab for each known provider so the strip is stable
            // across fetches. They'll get bound to real data once LoadConfigAndRefresh
            // populates Providers.
            foreach (UsageProvider provider in Enum.GetValues(typeof(UsageProvider)))
            {
                if (provider == UsageProvider.Unknown) continue;
                string id = ProviderMapping.toString(provider);
                string displayName = ProviderMapping.getDisplayName(provider);
                // Use the same glyph the per-provider header used (EC7A was a sample).
                // For the tab we use a generic bullet glyph; provider-specific icons
                // can be added later.
                Tabs.Add(new ProviderTabItem(provider, displayName, "\uE91F", isOverview: false));
            }
        }

        private void Tab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.DataContext is not ProviderTabItem tab) return;

            foreach (var t in Tabs) t.IsSelected = false;
            tab.IsSelected = true;

            if (tab.IsOverview)
            {
                OverviewMode = true;
                SelectedProvider = null;
            }
            else
            {
                OverviewMode = false;
                SelectedProvider = Providers.FirstOrDefault(p => p.Provider == tab.Provider);
            }
            BindBody();
        }

        private void BindBody()
        {
            // Wire the body ItemsControl to the right source.
            OverviewBody.Visibility = OverviewMode ? Visibility.Visible : Visibility.Collapsed;
            ProviderBody.Visibility = OverviewMode ? Visibility.Collapsed : Visibility.Visible;

            if (OverviewMode)
            {
                OverviewList.ItemsSource = Providers;
            }
            else if (SelectedProvider != null)
            {
                ProviderList.ItemsSource = SelectedProvider.Windows;
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadConfigAndRefresh();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // Populate Settings Items
            SettingsItems.Clear();
            var config = ConfigStore.load();

            foreach (UsageProvider provider in Enum.GetValues(typeof(UsageProvider)))
            {
                if (provider == UsageProvider.Unknown) continue;

                string id = ProviderMapping.toString(provider);
                string displayName = ProviderMapping.getDisplayName(provider);

                ProviderConfig? existing = config.providers.FirstOrDefault(p => p.id == id);
                bool isEnabled = existing != null && existing.enabled.HasValue && existing.enabled.Value;
                string apiKey = existing?.apiKey ?? "";

                SettingsItems.Add(new ProviderSettingItem(id, displayName, isEnabled, apiKey));
            }

            SettingsItemsControl.ItemsSource = SettingsItems;

            DashboardPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Visible;
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            var configList = new List<ProviderConfig>();
            foreach (var item in SettingsItems)
            {
                configList.Add(new ProviderConfig(
                    id: item.Id,
                    enabled: item.IsEnabled,
                    apiKey: item.ApiKey,
                    cookieHeader: "",
                    region: ""
                ));
            }

            var newConfig = new CodexBarConfig(version: 1, providers: Microsoft.FSharp.Collections.ListModule.OfSeq(configList));
            ConfigStore.save(newConfig);

            DashboardPanel.Visibility = Visibility.Visible;
            SettingsPanel.Visibility = Visibility.Collapsed;

            LoadConfigAndRefresh();
        }

        private void CancelSettings_Click(object sender, RoutedEventArgs e)
        {
            DashboardPanel.Visibility = Visibility.Visible;
            SettingsPanel.Visibility = Visibility.Collapsed;
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            _isExiting = true;
            _taskbarIcon.Dispose();
            this.Close();
            Microsoft.UI.Xaml.Application.Current.Exit();
        }

        private void ConfigLink_Click(object sender, RoutedEventArgs e)
        {
            string configPath = ConfigStore.getDefaultConfigPath();
            string? folderPath = Path.GetDirectoryName(configPath);
            if (folderPath != null && Directory.Exists(folderPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", folderPath);
            }
        }
    }

    public class ProviderViewModel
    {
        public UsageProvider Provider { get; }
        public string DisplayName { get; }
        public string Status { get; }
        public bool IsMock { get; }
        public string Footer { get; }

        /// <summary>
        /// Per-window rows for this provider. Single-bucket providers have
        /// exactly one entry; multi-bucket providers (Claude, future Codex
        /// OAuth) have two or more.
        /// </summary>
        public ObservableCollection<WindowViewModel> Windows { get; } = new();

        public Visibility IsMockVisibility => IsMock ? Visibility.Visible : Visibility.Collapsed;

        public Brush StatusBrush => Status.ToLower() switch
        {
            "healthy" => new SolidColorBrush(Colors.MediumSpringGreen),
            "degraded" => new SolidColorBrush(Colors.Orange),
            _ => new SolidColorBrush(Colors.Red)
        };

        public Brush ProgressBarBrush => MakeProviderGradient(Provider);

        public ProviderViewModel(ProviderUsage usage)
        {
            Provider = usage.Provider;
            DisplayName = usage.DisplayName;
            Status = usage.Status;
            IsMock = usage.IsMock;
            Footer = usage.Footer;
            foreach (var w in usage.Windows)
            {
                Windows.Add(new WindowViewModel(w, Provider));
            }
        }

        public static Brush MakeProviderGradient(UsageProvider provider)
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 0)
            };
            switch (provider)
            {
                case UsageProvider.OpenAI:
                    gradient.GradientStops.Add(new GradientStop { Color = ColorHelper.ToColor("FF10A37F"), Offset = 0.0 });
                    gradient.GradientStops.Add(new GradientStop { Color = ColorHelper.ToColor("FF00C9FF"), Offset = 1.0 });
                    break;
                case UsageProvider.Claude:
                    gradient.GradientStops.Add(new GradientStop { Color = ColorHelper.ToColor("FFD97706"), Offset = 0.0 });
                    gradient.GradientStops.Add(new GradientStop { Color = ColorHelper.ToColor("FFF59E0B"), Offset = 1.0 });
                    break;
                case UsageProvider.Gemini:
                    gradient.GradientStops.Add(new GradientStop { Color = ColorHelper.ToColor("FF8B5CF6"), Offset = 0.0 });
                    gradient.GradientStops.Add(new GradientStop { Color = ColorHelper.ToColor("FFEC4899"), Offset = 1.0 });
                    break;
                case UsageProvider.DeepSeek:
                    gradient.GradientStops.Add(new GradientStop { Color = ColorHelper.ToColor("FF2563EB"), Offset = 0.0 });
                    gradient.GradientStops.Add(new GradientStop { Color = ColorHelper.ToColor("FF1D4ED8"), Offset = 1.0 });
                    break;
                case UsageProvider.Cursor:
                    gradient.GradientStops.Add(new GradientStop { Color = ColorHelper.ToColor("FF0D9488"), Offset = 0.0 });
                    gradient.GradientStops.Add(new GradientStop { Color = ColorHelper.ToColor("FF14B8A6"), Offset = 1.0 });
                    break;
                default:
                    gradient.GradientStops.Add(new GradientStop { Color = ColorHelper.ToColor("FF06B6D4"), Offset = 0.0 });
                    gradient.GradientStops.Add(new GradientStop { Color = ColorHelper.ToColor("FF3B82F6"), Offset = 1.0 });
                    break;
            }
            return gradient;
        }
    }

    public class WindowViewModel
    {
        public string Label { get; }
        public double UsedPercent { get; }
        public string ResetCountdown { get; }
        public Brush ProgressBarBrush { get; }

        public double PercentFraction => Math.Clamp(UsedPercent / 100.0, 0.0, 1.0);
        public GridLength UsedStarWidth => new GridLength(Math.Max(0.001, PercentFraction), GridUnitType.Star);
        public GridLength RemainingStarWidth => new GridLength(Math.Max(0.001, 1.0 - PercentFraction), GridUnitType.Star);
        public string PercentText => $"{Math.Round(UsedPercent)}%";
        public bool HasLabel => !string.IsNullOrEmpty(Label) && Label != "Quota";
        public Visibility LabelVisibility => HasLabel ? Visibility.Visible : Visibility.Collapsed;

        public WindowViewModel(UsageWindow window, UsageProvider provider)
        {
            Label = window.Label;
            UsedPercent = window.UsedPercent;
            ResetCountdown = window.ResetCountdown;
            ProgressBarBrush = ProviderViewModel.MakeProviderGradient(provider);
        }
    }

    public class ProviderTabItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public UsageProvider Provider { get; }
        public string DisplayName { get; }
        public string IconGlyph { get; }
        public bool IsOverview { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TabBackground)));
                }
            }
        }

        /// <summary>
        /// Background brush for the tab button. Computed from IsSelected so
        /// the XAML doesn't need a value converter (which fails to bind
        /// inside a Window's DataTemplate in WinUI 3).
        /// </summary>
        public Brush TabBackground => new SolidColorBrush(
            _isSelected
                ? ColorHelper.ToColor("FF2D2D30")
                : Colors.Transparent);

        public ProviderTabItem(UsageProvider provider, string displayName, string iconGlyph, bool isOverview)
        {
            Provider = provider;
            DisplayName = displayName;
            IconGlyph = iconGlyph;
            IsOverview = isOverview;
            // Overview is selected by default.
            _isSelected = isOverview;
        }
    }

    public class ProviderSettingItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; }
        public string DisplayName { get; }

        private bool _isEnabled;
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
                }
            }
        }

        private string _apiKey;
        public string ApiKey
        {
            get => _apiKey;
            set
            {
                if (_apiKey != value)
                {
                    _apiKey = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ApiKey)));
                }
            }
        }

        public ProviderSettingItem(string id, string displayName, bool isEnabled, string apiKey)
        {
            Id = id;
            DisplayName = displayName;
            IsEnabled = isEnabled;
            _apiKey = apiKey;
        }
    }

    public static class ColorHelper
    {
        public static Color ToColor(string hex)
        {
            if (hex.StartsWith("#")) hex = hex.Substring(1);
            if (hex.Length == 8)
            {
                byte a = Convert.ToByte(hex.Substring(0, 2), 16);
                byte r = Convert.ToByte(hex.Substring(2, 2), 16);
                byte g = Convert.ToByte(hex.Substring(4, 2), 16);
                byte b = Convert.ToByte(hex.Substring(6, 2), 16);
                return Color.FromArgb(a, r, g, b);
            }
            else if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                return Color.FromArgb(255, r, g, b);
            }
            return Colors.White;
        }
    }

    public class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
