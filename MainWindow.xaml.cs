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

        private readonly TaskbarIcon _taskbarIcon;
        private bool _isExiting = false;

        public ObservableCollection<ProviderViewModel> Providers { get; } = new();
        public ObservableCollection<ProviderSettingItem> SettingsItems { get; } = new();

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

            // Force immersive dark mode to avoid light borders
            int useDark = 1;
            DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));

            // Set border color to blend with Mica background (#1D1D20 -> 0x00201D1D)
            int darkBorder = 0x00201D1D;
            DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref darkBorder, sizeof(int));

            PositionWindow();
        }

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
            ProvidersItemsControl.ItemsSource = Providers;

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
        public double Used { get; }
        public double Limit { get; }
        public string Unit { get; }
        public string ResetCountdown { get; }
        public string Status { get; }
        public bool IsMock { get; }
        public string CostInfo { get; }

        public Visibility IsMockVisibility => IsMock ? Visibility.Visible : Visibility.Collapsed;

        public GridLength UsedStarWidth => new GridLength(Math.Max(0.001, Percent), GridUnitType.Star);
        public GridLength RemainingStarWidth => new GridLength(Math.Max(0.001, 1.0 - Percent), GridUnitType.Star);

        public double Percent => Limit > 0 ? Math.Clamp(Used / Limit, 0.0, 1.0) : 0.0;
        public string PercentText => $"{Math.Round(Percent * 100)}%";

        public Brush StatusBrush => Status.ToLower() switch
        {
            "healthy" => new SolidColorBrush(Colors.MediumSpringGreen),
            "degraded" => new SolidColorBrush(Colors.Orange),
            _ => new SolidColorBrush(Colors.Red)
        };

        public Brush ProgressBarBrush
        {
            get
            {
                var gradient = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 0)
                };

                switch (Provider)
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

        public ProviderViewModel(ProviderUsage usage)
        {
            Provider = usage.Provider;
            DisplayName = usage.DisplayName;
            Used = usage.Used;
            Limit = usage.Limit;
            Unit = usage.Unit;
            ResetCountdown = usage.ResetCountdown;
            Status = usage.Status;
            IsMock = usage.IsMock;
            CostInfo = usage.CostInfo;
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
