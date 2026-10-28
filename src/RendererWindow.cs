using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace RevitEnscapeCloud
{
    /// <summary>
    /// Modeless Enscape-style Real-Time Renderer Window for Revit 2026.
    /// Hosts a native WPF Control Bar (Point Cloud Density Slider, Point Size, Color Mode, Time of Day)
    /// synchronized instantaneously with the Three.js / WebGL2 GPU Engine inside WebView2.
    /// </summary>
    public class RendererWindow : Window
    {
        private readonly LiveSyncExternalHandler _handler;
        private readonly ExternalEvent _externalEvent;
        private WebView2 _webView;
        private bool _isWebViewReady = false;
        private ScenePayload _pendingPayload;

        // Native WPF Controls for Point Cloud & Lighting
        private Slider _densitySlider;
        private TextBlock _densityValueText;
        private Slider _pointSizeSlider;
        private TextBlock _pointSizeValueText;
        private Slider _timeOfDaySlider;
        private TextBlock _timeOfDayValueText;
        private WpfComboBox _colorModeCombo;
        private CheckBox _liveSyncCheck;
        private WpfComboBox _maxPointsCombo;
        private TextBlock _statusText;
        private TextBlock _statsText;

        private bool _hasSceneCenterRef = false;
        private double _refCenterX = 0.0;
        private double _refCenterY = 0.0;
        private double _refCenterZ = 0.0;
        private double _sharedEast = 1083692.001;
        private double _sharedNorth = 1275923.913;
        private double _sharedElev = 382.670;
        private double _sharedAngleRad = 0.0;

        private static RendererWindow _instance;

        public static void ShowOrActivate(UIApplication uiApp, LiveSyncExternalHandler handler, ExternalEvent extEvent)
        {
            if (_instance == null || !_instance.IsLoaded)
            {
                _instance = new RendererWindow(handler, extEvent);
                _instance.Show();
            }
            else
            {
                if (_instance.WindowState == WindowState.Minimized)
                    _instance.WindowState = WindowState.Normal;
                _instance.Activate();
            }

            // Trigger initial scene & point cloud sync
            handler.RequestFullSync(_instance.GetSelectedMaxPoints());
            extEvent.Raise();
        }

        public static RendererWindow CurrentInstance
        {
            get { return _instance; }
        }

        public bool IsLiveSyncEnabled
        {
            get { return _liveSyncCheck != null && _liveSyncCheck.IsChecked == true; }
        }

        public RendererWindow(LiveSyncExternalHandler handler, ExternalEvent extEvent)
        {
            _handler = handler;
            _externalEvent = extEvent;

            Title = "EnscapeCloud RT — Renderizador en Tiempo Real + Nube de Puntos (Revit 2026)";
            Width = 1420;
            Height = 860;
            MinWidth = 960;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(15, 18, 24));
            AllowDrop = true;
            PreviewDragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effects = DragDropEffects.Copy;
                    e.Handled = true;
                }
            };
            PreviewDragOver += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effects = DragDropEffects.Copy;
                    e.Handled = true;
                }
            };
            PreviewDrop += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Handled = true;
                    string[] droppedFiles = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (droppedFiles != null && droppedFiles.Length > 0)
                    {
                        ProcessSelectedOrDroppedFiles(droppedFiles);
                    }
                }
            };

            BuildLayout();

            if (_handler != null)
            {
                _handler.OnSceneExported += HandleSceneExported;
                _handler.OnDirectCloudLoaded += HandleDirectCloudLoaded;
                _handler.OnRevitCameraChanged += HandleRevitCameraChanged;
                _handler.OnStatusMessage += HandleStatusMessage;
            }

            Loaded += OnWindowLoaded;
            Closed += OnWindowClosed;
        }

        private void BuildLayout()
        {
            Grid rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });

            // ================= TOP TOOLBAR (Native WPF Docked Bar) =================
            Border topBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(22, 27, 36)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 55, 72)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(14, 6, 14, 6)
            };

            DockPanel topDock = new DockPanel { LastChildFill = false };

            // Brand / Title Badge
            Border brandBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(14, 165, 233)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            brandBadge.Child = new TextBlock
            {
                Text = "ENSCAPE CLOUD 2026",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 11.5
            };
            DockPanel.SetDock(brandBadge, Dock.Left);
            topDock.Children.Add(brandBadge);

            // Sync Scene Button
            Button btnSync = CreateStyledButton("⟳ Sincronizar Modelo + RCP", Color.FromRgb(37, 99, 235));
            btnSync.Click += (s, e) =>
            {
                SetStatus("Extrayendo geometría PBR y nubes de puntos de Revit 2026...");
                _handler.RequestFullSync(GetSelectedMaxPoints());
                _externalEvent.Raise();
            };
            DockPanel.SetDock(btnSync, Dock.Left);
            topDock.Children.Add(btnSync);

            // Direct .LAS / ReCap .RCS / Reality Mesh .3SM Loader Button
            Button btnLoadLas = CreateStyledButton("📂 Cargar Nube (.LAS/.RCS) o Malla .3SM", Color.FromRgb(5, 150, 105));
            btnLoadLas.Click += (s, e) => PromptAndLoadPointCloudFile();
            DockPanel.SetDock(btnLoadLas, Dock.Left);
            topDock.Children.Add(btnLoadLas);

            // Live Sync CheckBox (Off by default so Idling never interferes with heavy cloud/point-cloud models unless requested)
            _liveSyncCheck = new CheckBox
            {
                Content = "Live Sync Cámara",
                IsChecked = false,
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 16, 0),
                FontWeight = FontWeights.SemiBold
            };
            DockPanel.SetDock(_liveSyncCheck, Dock.Left);
            topDock.Children.Add(_liveSyncCheck);

            // Separator
            topDock.Children.Add(CreateSeparator());

            // ================= POINT CLOUD DENSITY SLIDER (Main Requested Feature) =================
            StackPanel densityPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 14, 0)
            };

            TextBlock densityLabel = new TextBlock
            {
                Text = "☁ Densidad Nube de Puntos:",
                Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            densityPanel.Children.Add(densityLabel);

            _densitySlider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = 100,
                Width = 165,
                VerticalAlignment = VerticalAlignment.Center,
                IsSnapToTickEnabled = false
            };
            _densitySlider.ValueChanged += OnDensitySliderChanged;
            densityPanel.Children.Add(_densitySlider);

            _densityValueText = new TextBlock
            {
                Text = "100.0%",
                Width = 52,
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            densityPanel.Children.Add(_densityValueText);

            DockPanel.SetDock(densityPanel, Dock.Left);
            topDock.Children.Add(densityPanel);

            // Point Size Slider
            StackPanel sizePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 14, 0)
            };
            sizePanel.Children.Add(new TextBlock
            {
                Text = "Tamaño Punto:",
                Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });

            _pointSizeSlider = new Slider
            {
                Minimum = 0.5,
                Maximum = 25.0,
                Value = 6.5,
                Width = 90,
                VerticalAlignment = VerticalAlignment.Center
            };
            _pointSizeSlider.ValueChanged += OnPointSizeSliderChanged;
            sizePanel.Children.Add(_pointSizeSlider);

            _pointSizeValueText = new TextBlock
            {
                Text = "6.5 px",
                Width = 44,
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            sizePanel.Children.Add(_pointSizeValueText);

            DockPanel.SetDock(sizePanel, Dock.Left);
            topDock.Children.Add(sizePanel);

            // Point Cloud Color Mode
            StackPanel modePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 14, 0)
            };
            modePanel.Children.Add(new TextBlock
            {
                Text = "Color Nube:",
                Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });

            _colorModeCombo = new WpfComboBox
            {
                Width = 145,
                VerticalAlignment = VerticalAlignment.Center,
                SelectedIndex = 0
            };
            _colorModeCombo.Items.Add("True Color ReCap (Brillo)");
            _colorModeCombo.Items.Add("Altura por Sector (Gradiente)");
            _colorModeCombo.Items.Add("Segmentación Árboles vs Suelo");
            _colorModeCombo.Items.Add("Intensidad / Reflectancia");
            _colorModeCombo.SelectionChanged += OnColorModeChanged;
            modePanel.Children.Add(_colorModeCombo);

            DockPanel.SetDock(modePanel, Dock.Left);
            topDock.Children.Add(modePanel);

            // Max Points Budget Selector
            StackPanel budgetPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 14, 0)
            };
            budgetPanel.Children.Add(new TextBlock
            {
                Text = "Límite API:",
                Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            _maxPointsCombo = new WpfComboBox
            {
                Width = 125,
                VerticalAlignment = VerticalAlignment.Center,
                SelectedIndex = 0
            };
            _maxPointsCombo.Items.Add("950,000 pts (Rápido)");
            _maxPointsCombo.Items.Add("2,000,000 pts");
            _maxPointsCombo.Items.Add("4,000,000 pts (VZ-600i)");
            _maxPointsCombo.Items.Add("6,000,000 pts (Máx)");
            budgetPanel.Children.Add(_maxPointsCombo);
            DockPanel.SetDock(budgetPanel, Dock.Left);
            topDock.Children.Add(budgetPanel);

            // Time of Day Slider (Sun & Sky)
            StackPanel sunPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 8, 0)
            };
            sunPanel.Children.Add(new TextBlock
            {
                Text = "☀ Hora:",
                Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });

            _timeOfDaySlider = new Slider
            {
                Minimum = 5.0,
                Maximum = 20.0,
                Value = 13.5,
                Width = 95,
                VerticalAlignment = VerticalAlignment.Center
            };
            _timeOfDaySlider.ValueChanged += OnTimeOfDayChanged;
            sunPanel.Children.Add(_timeOfDaySlider);

            _timeOfDayValueText = new TextBlock
            {
                Text = "13:30",
                Width = 42,
                Foreground = new SolidColorBrush(Color.FromRgb(253, 230, 138)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            sunPanel.Children.Add(_timeOfDayValueText);

            DockPanel.SetDock(sunPanel, Dock.Left);
            topDock.Children.Add(sunPanel);

            topBar.Child = topDock;
            Grid.SetRow(topBar, 0);
            rootGrid.Children.Add(topBar);

            // ================= CENTER VIEWPORT (WebView2 GPU Canvas) =================
            _webView = new WebView2();
            Grid.SetRow(_webView, 1);
            rootGrid.Children.Add(_webView);

            // ================= BOTTOM STATUS BAR =================
            Border statusBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(17, 21, 28)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 55, 72)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(12, 4, 12, 4)
            };
            DockPanel statusDock = new DockPanel();

            _statusText = new TextBlock
            {
                Text = "Inicializando motor gráfico GPU WebGL2 / Three.js...",
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(_statusText, Dock.Left);
            statusDock.Children.Add(_statusText);

            _statsText = new TextBlock
            {
                Text = "Controles: WASD + Click Derecho para volar | Rueda: Velocidad | Barra deslizable: Densidad Nube de Puntos en GPU",
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(_statsText, Dock.Right);
            statusDock.Children.Add(_statsText);

            statusBar.Child = statusDock;
            Grid.SetRow(statusBar, 2);
            rootGrid.Children.Add(statusBar);

            Content = rootGrid;
        }

        private Button CreateStyledButton(string text, Color bg)
        {
            return new Button
            {
                Content = text,
                Background = new SolidColorBrush(bg),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 6, 0),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private Border CreateSeparator()
        {
            Border sep = new Border
            {
                Width = 1,
                Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                Margin = new Thickness(4, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(sep, Dock.Left);
            return sep;
        }

        public int GetSelectedMaxPoints()
        {
            if (_maxPointsCombo == null) return 950000;
            if (_maxPointsCombo.SelectedIndex == 0) return 950000;
            if (_maxPointsCombo.SelectedIndex == 1) return 2000000;
            if (_maxPointsCombo.SelectedIndex == 2) return 4000000;
            return 6000000;
        }

        private async void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RevitEnscapeCloud2026",
                    "WebView2Cache");

                Directory.CreateDirectory(userDataFolder);

                CoreWebView2EnvironmentOptions envOptions = new CoreWebView2EnvironmentOptions(
                    "--max-old-space-size=8192 --js-flags=\"--max-old-space-size=8192\" --disable-features=OutOfBlinkCors --ignore-gpu-blocklist --enable-gpu-rasterization --enable-zero-copy",
                    null, null, false, null);

                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, envOptions);
                await _webView.EnsureCoreWebView2Async(env);

                _webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string htmlPath = Path.Combine(assemblyDir, "WebViewer", "index.html");

                if (File.Exists(htmlPath))
                {
                    // Map virtual HTTPS host so WebView2 has zero local file:// security restrictions and high-speed binary transfer
                    string webViewerFolder = Path.Combine(assemblyDir, "WebViewer");
                    _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "enscapecloud.local",
                        webViewerFolder,
                        CoreWebView2HostResourceAccessKind.Allow);
                    _webView.CoreWebView2.Navigate("https://enscapecloud.local/index.html?v=" + DateTime.UtcNow.Ticks);
                }
                else
                {
                    SetStatus("Error: No se encontró WebViewer/index.html en " + htmlPath);
                }
            }
            catch (Exception ex)
            {
                SetStatus("Error inicializando WebView2: " + ex.Message);
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string msg = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(msg)) return;

                if (msg.StartsWith("READY"))
                {
                    _isWebViewReady = true;
                    SetStatus("Motor en tiempo real listo. Sincronizando escena de Revit 2026...");
                    if (_pendingPayload != null)
                    {
                        SendSceneToWebView(_pendingPayload);
                        _pendingPayload = null;
                    }
                    else
                    {
                        _handler.RequestFullSync(GetSelectedMaxPoints());
                        _externalEvent.Raise();
                    }
                }
                else if (msg.StartsWith("DENSITY:"))
                {
                    // Keep native WPF slider synchronized if the user drags the HUD slider inside the 3D viewport
                    double val;
                    if (double.TryParse(msg.Substring(8), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out val))
                    {
                        _densitySlider.ValueChanged -= OnDensitySliderChanged;
                        _densitySlider.Value = val;
                        _densityValueText.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F1}%", val);
                        _densitySlider.ValueChanged += OnDensitySliderChanged;
                    }
                }
                else if (msg.StartsWith("SYNC_REQUEST"))
                {
                    _handler.RequestFullSync(GetSelectedMaxPoints());
                    _externalEvent.Raise();
                }
                else if (msg.StartsWith("OPEN_LAS_DIALOG"))
                {
                    PromptAndLoadPointCloudFile();
                }
                else if (msg.StartsWith("LOAD_FILE_PATH:") || msg.StartsWith("LOAD_3SM_PATH:"))
                {
                    string p = msg.Substring(msg.IndexOf(':') + 1);
                    ProcessSelectedOrDroppedFiles(new string[] { p });
                }
                else if (msg.StartsWith("SAVE_VIDEO_B64:"))
                {
                    SaveExportedVideoFromBase64(msg.Substring(15));
                }
            }
            catch
            {
            }
        }

        private void OnDensitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_densityValueText != null)
            {
                _densityValueText.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F1}%", e.NewValue);
            }

            if (_isWebViewReady && _webView != null && _webView.CoreWebView2 != null)
            {
                string valStr = (e.NewValue / 100.0).ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                _webView.CoreWebView2.ExecuteScriptAsync("window.setPointCloudDensity && window.setPointCloudDensity(" + valStr + ");");
            }
        }

        private void OnPointSizeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_pointSizeValueText != null)
            {
                _pointSizeValueText.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F1} px", e.NewValue);
            }

            if (_isWebViewReady && _webView != null && _webView.CoreWebView2 != null)
            {
                string valStr = e.NewValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                _webView.CoreWebView2.ExecuteScriptAsync("window.setPointCloudSize && window.setPointCloudSize(" + valStr + ");");
            }
        }

        private void OnColorModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isWebViewReady && _webView != null && _webView.CoreWebView2 != null && _colorModeCombo != null)
            {
                int mode = _colorModeCombo.SelectedIndex;
                _webView.CoreWebView2.ExecuteScriptAsync("window.setPointCloudColorMode && window.setPointCloudColorMode(" + mode + ");");
            }
        }

        private void OnTimeOfDayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int hours = (int)Math.Floor(e.NewValue);
            int mins = (int)Math.Round((e.NewValue - hours) * 60);
            if (mins == 60) { hours++; mins = 0; }

            if (_timeOfDayValueText != null)
            {
                _timeOfDayValueText.Text = string.Format("{0:D2}:{1:D2}", hours, mins);
            }

            if (_isWebViewReady && _webView != null && _webView.CoreWebView2 != null)
            {
                string valStr = e.NewValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                _webView.CoreWebView2.ExecuteScriptAsync("window.setTimeOfDay && window.setTimeOfDay(" + valStr + ");");
            }
        }

        private void HandleSceneExported(ScenePayload payload)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isWebViewReady)
                {
                    _pendingPayload = payload;
                    return;
                }
                SendSceneToWebView(payload);
            }));
        }

        private void SendSceneToWebView(ScenePayload payload)
        {
            try
            {
                int totalTris = 0;
                double sumX = 0, sumY = 0, sumZ = 0;
                int countVerts = 0;

                foreach (var m in payload.Meshes)
                {
                    totalTris += m.Indices.Count / 3;
                    for (int i = 0; i + 2 < m.Positions.Count; i += 30)
                    {
                        sumX += m.Positions[i];
                        sumY += m.Positions[i + 1];
                        sumZ += m.Positions[i + 2];
                        countVerts++;
                    }
                }

                if (countVerts > 0)
                {
                    _refCenterX = sumX / countVerts;
                    _refCenterY = sumY / countVerts;
                    _refCenterZ = sumZ / countVerts;
                    _hasSceneCenterRef = true;
                }
                else if (payload.Camera != null)
                {
                    _refCenterX = payload.Camera.TargetX;
                    _refCenterY = payload.Camera.TargetY;
                    _refCenterZ = payload.Camera.TargetZ;
                    _hasSceneCenterRef = true;
                }

                if (payload.SharedOriginEast != 0 || payload.SharedOriginNorth != 0)
                {
                    _sharedEast = payload.SharedOriginEast;
                    _sharedNorth = payload.SharedOriginNorth;
                    _sharedElev = payload.SharedOriginElev;
                    _sharedAngleRad = payload.SharedAngleRad;
                }

                // Stream PointClouds via fast binary files on virtual host https://enscapecloud.local/
                // so we NEVER choke WebView2's JSON message pipe with multi-megabyte Base64 strings!
                string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string webViewerFolder = Path.Combine(assemblyDir, "WebViewer");

                List<PointCloudBatchData> clouds = new List<PointCloudBatchData>(payload.PointClouds);
                payload.PointClouds.Clear();

                string meshJson = payload.ToJson();
                try
                {
                    string jsonPath = Path.Combine(webViewerFolder, "scene_current.json");
                    File.WriteAllText(jsonPath, meshJson);
                }
                catch { }

                _webView.CoreWebView2.PostWebMessageAsJson(meshJson);

                // Bulletproof fallback: ensure scene loads even if PostWebMessage was dropped
                string verifyScript = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "setTimeout(function() {{ if (!window.__sceneLoaded && window.loadRevitScenePayload) {{ fetch('https://enscapecloud.local/scene_current.json?t={0}').then(function(r){{ return r.json(); }}).then(function(d){{ window.loadRevitScenePayload(d); }}).catch(console.error); }} }}, 350);",
                    DateTime.UtcNow.Ticks);
                _webView.CoreWebView2.ExecuteScriptAsync(verifyScript);

                int totalPts = 0;
                for (int i = 0; i < clouds.Count; i++)
                {
                    if (clouds[i].PackedPointsBuffer == null || clouds[i].TotalPointsCount <= 0)
                        continue;

                    totalPts += clouds[i].TotalPointsCount;
                    string binFileName = string.Format("cloud_tile_{0}.bin", i);
                    string binFullPath = Path.Combine(webViewerFolder, binFileName);
                    File.WriteAllBytes(binFullPath, clouds[i].PackedPointsBuffer);

                    string loadScript = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "window.loadBinaryCloudFromHost && window.loadBinaryCloudFromHost('https://enscapecloud.local/{0}?t={1}', {2}, false, 'REVIT');",
                        binFileName, DateTime.UtcNow.Ticks, clouds[i].TotalPointsCount);
                    _webView.CoreWebView2.ExecuteScriptAsync(loadScript);
                }

                SetStatus(string.Format(
                    "Sincronizado: '{0}' ({1}) — {2:N0} triángulos PBR | {3} bloques de nube ({4:N0} puntos en GPU)",
                    payload.DocumentTitle, payload.ViewName, totalTris, clouds.Count, totalPts));
            }
            catch (Exception ex)
            {
                SetStatus("Error enviando geometría a GPU: " + ex.Message);
            }
        }

        private void HandleDirectCloudLoaded(List<PointCloudBatchData> batches, string fileName)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (batches == null || batches.Count == 0)
                    {
                        SetStatus("No se encontraron puntos válidos en el archivo ReCap: " + fileName);
                        return;
                    }

                    string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    string webViewerFolder = Path.Combine(assemblyDir, "WebViewer");
                    Directory.CreateDirectory(webViewerFolder);

                    try
                    {
                        foreach (string oldFile in Directory.GetFiles(webViewerFolder, "mesh_tile_*.bin"))
                        {
                            try { File.Delete(oldFile); } catch { }
                        }
                        foreach (string oldFile in Directory.GetFiles(webViewerFolder, "mesh_tex_*.jpg"))
                        {
                            try { File.Delete(oldFile); } catch { }
                        }
                    }
                    catch { }
                    int loadedPts = 0;

                    for (int i = 0; i < batches.Count; i++)
                    {
                        if (batches[i].PackedPointsBuffer == null || batches[i].TotalPointsCount <= 0)
                            continue;

                        loadedPts += batches[i].TotalPointsCount;
                        string binFileName = string.Format("direct_rcs_{0}_{1}.bin", DateTime.UtcNow.Ticks, i);
                        string binFullPath = Path.Combine(webViewerFolder, binFileName);
                        File.WriteAllBytes(binFullPath, batches[i].PackedPointsBuffer);

                        string loadScript = string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "window.loadBinaryCloudFromHost && window.loadBinaryCloudFromHost('https://enscapecloud.local/{0}', {1}, {2}, 'DIRECT');",
                            binFileName, batches[i].TotalPointsCount, (i == batches.Count - 1) ? "true" : "false");
                        _webView.CoreWebView2.ExecuteScriptAsync(loadScript);
                    }

                    SetStatus(string.Format(
                        "Nube ReCap (.RCS/.RCP) cargada directamente: '{0}' — {1:N0} puntos en GPU.",
                        fileName, loadedPts));
                }
                catch (Exception ex)
                {
                    SetStatus("Error enviando nube ReCap a GPU: " + ex.Message);
                }
            }));
        }

        private void PromptAndLoadPointCloudFile()
        {
            try
            {
                OpenFileDialog dlg = new OpenFileDialog
                {
                    Title = "Cargar Nube de Puntos (.LAS / .RCS) o Malla Reality .3SM (iTwin)",
                    Filter = "Nubes y Mallas Reality (*.las;*.rcs;*.rcp;*.3sm;*.3mx;*.3mxb;*.obj;*.glb)|*.las;*.rcs;*.rcp;*.3sm;*.3mx;*.3mxb;*.obj;*.glb|Mallas Reality .3SM Bentley (*.3sm;*.3mx;*.3mxb)|*.3sm;*.3mx;*.3mxb|Nubes ReCap (*.rcs;*.rcp)|*.rcs;*.rcp|Nubes LAS (*.las)|*.las|Todos los archivos (*.*)|*.*",
                    Multiselect = true,
                    DereferenceLinks = false,
                    ValidateNames = false,
                    CheckFileExists = false
                };

                if (dlg.ShowDialog(this) == true)
                {
                    ProcessSelectedOrDroppedFiles(dlg.FileNames);
                }
            }
            catch (Exception ex)
            {
                SetStatus("Error al abrir selector de archivos: " + ex.Message);
            }
        }

        private async void ProcessSelectedOrDroppedFiles(string[] files)
        {
            if (files == null || files.Length == 0) return;
            try
            {
                int maxPts = GetSelectedMaxPoints();
                bool hasRef = _hasSceneCenterRef;
                double rx = _refCenterX, ry = _refCenterY, rz = _refCenterZ;

                foreach (string filePath in files)
                {
                    if (string.IsNullOrEmpty(filePath)) continue;

                    // If user dropped a directory (e.g. WebReady folder)
                    if (Directory.Exists(filePath))
                    {
                        string webReadyN0 = Path.Combine(filePath, "n_0.json");
                        if (File.Exists(webReadyN0))
                        {
                            string parentDir = Path.GetDirectoryName(filePath);
                            string[] smFiles = Directory.GetFiles(parentDir, "*.3sm");
                            if (smFiles.Length > 0)
                            {
                                await LoadRealityMeshFileAsync(smFiles[0]);
                                continue;
                            }
                        }
                    }

                    if (!File.Exists(filePath)) continue;

                    string ext = Path.GetExtension(filePath).ToLowerInvariant();
                    string fileName = Path.GetFileName(filePath);

                    if (ext == ".3sm" || ext == ".3mx" || ext == ".3mxb" || ext == ".glb" || ext == ".obj")
                    {
                        await LoadRealityMeshFileAsync(filePath);
                        continue;
                    }

                    if (ext == ".rcp")
                    {
                        SetStatus("Solicitando lectura de proyecto ReCap (.RCP) a Revit 2026: " + fileName + "...");
                        PushProgressToWebView("IN_PROGRESS", 10.0, "Cargando Proyecto ReCap (.RCP): " + fileName, "Extrayendo nubes vinculadas desde motor de Revit 2026...");
                        _handler.RequestLoadDirectRcs(filePath, maxPts);
                        _externalEvent.Raise();
                        continue;
                    }

                    SetStatus("Iniciando lectura de nube de puntos (" + ext.ToUpperInvariant() + "): " + fileName + "...");
                    PushProgressToWebView("IN_PROGRESS", 1.0, "Cargando " + fileName, "Iniciando lectura de archivo binario...");

                    Action<double, string> progressCb = (pct, detail) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            SetStatus(detail);
                            PushProgressToWebView("IN_PROGRESS", pct, "Cargando " + fileName, detail);
                        }));
                    };

                    List<PointCloudBatchData> batches = await Task.Run(() =>
                        (ext == ".rcs")
                            ? LasFileReader.ReadRcsFile(filePath, maxPts, hasRef, rx, ry, rz, true, progressCb)
                            : LasFileReader.ReadLasFile(filePath, maxPts, hasRef, rx, ry, rz, true, progressCb));

                    if (batches == null || batches.Count == 0)
                    {
                        SetStatus("No se encontraron puntos válidos en: " + fileName);
                        PushProgressToWebView("ERROR", 0.0, "Error en " + fileName, "No se encontraron puntos válidos en el archivo seleccionado.");
                        continue;
                    }

                    int loadedPts = 0;
                    string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    string webViewerFolder = Path.Combine(assemblyDir, "WebViewer");
                    Directory.CreateDirectory(webViewerFolder);

                    try
                    {
                        foreach (string oldFile in Directory.GetFiles(webViewerFolder, "mesh_tile_*.bin"))
                        {
                            try { File.Delete(oldFile); } catch { }
                        }
                        foreach (string oldFile in Directory.GetFiles(webViewerFolder, "mesh_tex_*.jpg"))
                        {
                            try { File.Delete(oldFile); } catch { }
                        }
                    }
                    catch { }

                    for (int i = 0; i < batches.Count; i++)
                    {
                        if (batches[i].PackedPointsBuffer == null || batches[i].TotalPointsCount <= 0)
                            continue;

                        loadedPts += batches[i].TotalPointsCount;
                        string binFileName = string.Format("las_tile_{0}_{1}.bin", DateTime.UtcNow.Ticks, i);
                        string binFullPath = Path.Combine(webViewerFolder, binFileName);
                        File.WriteAllBytes(binFullPath, batches[i].PackedPointsBuffer);

                        string loadScript = string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "window.loadBinaryCloudFromHost && window.loadBinaryCloudFromHost('https://enscapecloud.local/{0}', {1}, {2}, 'DIRECT');",
                            binFileName, batches[i].TotalPointsCount, (i == batches.Count - 1) ? "true" : "false");
                        await _webView.CoreWebView2.ExecuteScriptAsync(loadScript);
                    }

                    string doneMsg = string.Format("¡Completado! '{0}' — {1:N0} puntos cargados en GPU.", fileName, loadedPts);
                    SetStatus(doneMsg);
                    PushProgressToWebView("COMPLETED", 100.0, "Carga Completada: " + fileName, doneMsg);
                }
            }
            catch (Exception ex)
            {
                SetStatus("Error al procesar archivo: " + ex.Message);
                PushProgressToWebView("ERROR", 0.0, "Error de Carga", ex.Message);
            }
        }

        private async Task LoadRealityMeshFileAsync(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            string fileName = Path.GetFileName(filePath);

            if (ext == ".3sm")
            {
                SetStatus("Cargando Malla Reality .3SM (iTwin): " + fileName + "...");
                PushProgressToWebView("IN_PROGRESS", 5.0, "Cargando Malla Reality .3SM: " + fileName, "Consultando base de datos SQLite y extrayendo teselas...");
                try
                {
                    RealityMeshModel model = await Task.Run(() => SmFileReader.Read3smFile(filePath, (pct, detail) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            SetStatus(detail);
                            PushProgressToWebView("IN_PROGRESS", pct, "Cargando Malla Reality .3SM: " + fileName, detail);
                        }));
                    }));

                    if (model == null || model.Tiles.Count == 0)
                    {
                        throw new InvalidOperationException("No se encontraron teselas de geometría en el archivo .3sm: " + fileName);
                    }

                    string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    string webViewerFolder = Path.Combine(assemblyDir, "WebViewer");
                    Directory.CreateDirectory(webViewerFolder);

                    try
                    {
                        foreach (string oldFile in Directory.GetFiles(webViewerFolder, "mesh_tile_*.bin"))
                        {
                            try { File.Delete(oldFile); } catch { }
                        }
                        foreach (string oldFile in Directory.GetFiles(webViewerFolder, "mesh_tex_*.jpg"))
                        {
                            try { File.Delete(oldFile); } catch { }
                        }
                    }
                    catch { }

                    for (int i = 0; i < model.Tiles.Count; i++)
                    {
                        var tile = model.Tiles[i];
                        string binFileName = string.Format("mesh_tile_{0}_{1}.bin", DateTime.UtcNow.Ticks, i);
                        string binFullPath = Path.Combine(webViewerFolder, binFileName);
                        File.WriteAllBytes(binFullPath, tile.PackedGeometry);

                        string texFileName = "";
                        if (tile.JpegTexture != null && tile.JpegTexture.Length > 0)
                        {
                            texFileName = string.Format("mesh_tex_{0}_{1}.jpg", DateTime.UtcNow.Ticks, i);
                            string texFullPath = Path.Combine(webViewerFolder, texFileName);
                            File.WriteAllBytes(texFullPath, tile.JpegTexture);
                        }

                        string loadScript = string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "window.loadRealityMeshTileBinary && window.loadRealityMeshTileBinary('{0}', '{1}', {2}, {3}, {4:F4}, {5:F4}, {6:F4}, {7:F4}, {8:F4}, {9:F4}, '{10}', {11});",
                            binFileName, texFileName, i, model.Tiles.Count,
                            tile.MinX, tile.MinY, tile.MinZ, tile.MaxX, tile.MaxY, tile.MaxZ,
                            EscapeJs(fileName), (i == model.Tiles.Count - 1) ? "true" : "false"
                        );
                        await _webView.CoreWebView2.ExecuteScriptAsync(loadScript);
                    }

                    SetStatus(string.Format("Malla Reality '{0}' cargada ({1} teselas en GPU).", fileName, model.Tiles.Count));
                }
                catch (Exception ex3sm)
                {
                    SetStatus("Error al cargar malla reality .3sm: " + ex3sm.Message);
                    PushProgressToWebView("ERROR", 0.0, "Error en " + fileName, ex3sm.Message);
                }
            }
            else
            {
                SetStatus("Cargando Malla Reality (" + ext.ToUpperInvariant() + "): " + fileName + "...");
                PushProgressToWebView("IN_PROGRESS", 5.0, "Cargando Malla Reality: " + fileName, "Leyendo archivo...");
                try
                {
                    string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    string webViewerFolder = Path.Combine(assemblyDir, "WebViewer");
                    Directory.CreateDirectory(webViewerFolder);

                    try
                    {
                        foreach (string oldFile in Directory.GetFiles(webViewerFolder, "mesh_tile_*.bin"))
                        {
                            try { File.Delete(oldFile); } catch { }
                        }
                        foreach (string oldFile in Directory.GetFiles(webViewerFolder, "mesh_tex_*.jpg"))
                        {
                            try { File.Delete(oldFile); } catch { }
                        }
                    }
                    catch { }
                    string binFileName = string.Format("mesh_direct_{0}_{1}", DateTime.UtcNow.Ticks, fileName);
                    string binFullPath = Path.Combine(webViewerFolder, binFileName);
                    await Task.Run(() => File.Copy(filePath, binFullPath, true));
                    await _webView.CoreWebView2.ExecuteScriptAsync(
                        "window.loadRealityMeshFileFromHost && window.loadRealityMeshFileFromHost('" + EscapeJs(binFileName) + "', '" + EscapeJs(fileName) + "');"
                    );
                }
                catch (Exception exMesh)
                {
                    SetStatus("Error al cargar malla reality: " + exMesh.Message);
                    PushProgressToWebView("ERROR", 0.0, "Error en " + fileName, exMesh.Message);
                }
            }
        }

        private string EscapeJs(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", " ");
        }

        private void PushProgressToWebView(string state, double pct, string title, string detail)
        {
            if (!_isWebViewReady || _webView == null || _webView.CoreWebView2 == null) return;
            string safeTitle = (title ?? "").Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", " ");
            string safeDetail = (detail ?? "").Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", " ");
            string script = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "window.updateTaskProgressModal && window.updateTaskProgressModal('{0}', {1:F1}, '{2}', '{3}');",
                state, pct, safeTitle, safeDetail);
            _webView.CoreWebView2.ExecuteScriptAsync(script);
        }

        public void PushCameraUpdate(CameraInfoData cam)
        {
            if (!IsLiveSyncEnabled) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isWebViewReady && _webView != null && _webView.CoreWebView2 != null)
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    string script = string.Format(inv,
                        "window.updateCameraFromRevit && window.updateCameraFromRevit([{0:F4},{1:F4},{2:F4}],[{3:F4},{4:F4},{5:F4}]);",
                        cam.EyeX, cam.EyeY, cam.EyeZ,
                        cam.TargetX, cam.TargetY, cam.TargetZ);
                    _webView.CoreWebView2.ExecuteScriptAsync(script);
                }
            }));
        }

        private void HandleRevitCameraChanged(CameraInfoData cam)
        {
            PushCameraUpdate(cam);
        }

        private void HandleStatusMessage(string message)
        {
            Dispatcher.BeginInvoke(new Action(() => SetStatus(message)));
        }

        private void SetStatus(string text)
        {
            if (_statusText != null)
                _statusText.Text = text;
        }

        private void SaveExportedVideoFromBase64(string payload)
        {
            try
            {
                int sep = payload.IndexOf('|');
                if (sep <= 0) return;

                string ext = payload.Substring(0, sep);
                string b64 = payload.Substring(sep + 1);
                byte[] bytes = Convert.FromBase64String(b64);

                SaveFileDialog sfd = new SaveFileDialog
                {
                    Title = "Guardar Recorrido de Video Exportado (MP4)",
                    Filter = "Video MP4 (*.mp4)|*.mp4|Video WebM (*.webm)|*.webm",
                    FileName = "Recorrido_EnscapeCloud_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".mp4"
                };

                if (sfd.ShowDialog(this) == true)
                {
                    File.WriteAllBytes(sfd.FileName, bytes);
                    SetStatus("Video exportado exitosamente en: " + sfd.FileName + " (" + (bytes.Length / (1024.0 * 1024.0)).ToString("F2") + " MB)");
                }
            }
            catch (Exception ex)
            {
                SetStatus("Error guardando archivo de video: " + ex.Message);
            }
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            if (_handler != null)
            {
                _handler.OnSceneExported -= HandleSceneExported;
                _handler.OnDirectCloudLoaded -= HandleDirectCloudLoaded;
                _handler.OnRevitCameraChanged -= HandleRevitCameraChanged;
                _handler.OnStatusMessage -= HandleStatusMessage;
            }

            if (_webView != null)
            {
                _webView.Dispose();
                _webView = null;
            }
            _instance = null;
        }
    }
}
