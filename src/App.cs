using System;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using MediaColor = System.Windows.Media.Color;

namespace RevitEnscapeCloud
{
    /// <summary>
    /// Main Revit 2026 External Application entry point.
    /// Creates the "EnscapeCloud RT" Ribbon Tab with real-time renderer and point cloud controls.
    /// Also hooks DocumentChanged and Idling for Enscape-style Live Sync.
    /// </summary>
    public class App : IExternalApplication
    {
        public static LiveSyncExternalHandler SyncHandler;
        public static ExternalEvent SyncExternalEvent;

        private static bool _geometryDirty = false;
        private static DateTime _lastCameraCheck = DateTime.MinValue;
        private static double _lastEyeX, _lastEyeY, _lastEyeZ;
        private static double _lastTargetX, _lastTargetY, _lastTargetZ;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                SyncHandler = new LiveSyncExternalHandler();
                SyncExternalEvent = ExternalEvent.Create(SyncHandler);

                string tabName = "EnscapeCloud RT";
                try
                {
                    application.CreateRibbonTab(tabName);
                }
                catch
                {
                    // Tab might already exist
                }

                RibbonPanel panel = application.CreateRibbonPanel(tabName, "Renderizador & Nube de Puntos");
                string assemblyPath = Assembly.GetExecutingAssembly().Location;

                // 1. Start / Open Real-Time Renderer Button
                PushButtonData startBtnData = new PushButtonData(
                    "cmdStartEnscapeCloud",
                    "Iniciar\nEnscapeCloud",
                    assemblyPath,
                    "RevitEnscapeCloud.StartRendererCommand");
                startBtnData.ToolTip = "Abre el renderizador en tiempo real estilo Enscape con visualización de Nube de Puntos (.rcp/.rcs) y barra deslizable de densidad GPU.";
                startBtnData.LargeImage = CreateVectorIcon(MediaColor.FromRgb(14, 165, 233), MediaColor.FromRgb(56, 189, 248), true);

                // 2. Force Sync Model & Point Cloud Button
                PushButtonData syncBtnData = new PushButtonData(
                    "cmdSyncEnscapeCloud",
                    "Sincronizar\nEscena + Nube",
                    assemblyPath,
                    "RevitEnscapeCloud.ForceSyncCommand");
                syncBtnData.ToolTip = "Actualiza inmediatamente toda la geometría 3D, materiales PBR, sol y nubes de puntos vinculadas.";
                syncBtnData.LargeImage = CreateVectorIcon(MediaColor.FromRgb(16, 185, 129), MediaColor.FromRgb(52, 211, 153), false);

                panel.AddItem(startBtnData);
                panel.AddItem(syncBtnData);

                // Subscribe to DocumentChanged and Idling for automatic Live Sync
                application.ControlledApplication.DocumentChanged += OnDocumentChanged;
                application.Idling += OnApplicationIdling;

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("EnscapeCloud RT Error", "Error al iniciar el plugin: " + ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
                application.Idling -= OnApplicationIdling;

                if (RendererWindow.CurrentInstance != null)
                {
                    RendererWindow.CurrentInstance.Close();
                }
            }
            catch
            {
            }
            return Result.Succeeded;
        }

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (RendererWindow.CurrentInstance != null && RendererWindow.CurrentInstance.IsLoaded && RendererWindow.CurrentInstance.IsLiveSyncEnabled)
            {
                _geometryDirty = true;
            }
        }

        private void OnApplicationIdling(object sender, IdlingEventArgs e)
        {
            RendererWindow win = RendererWindow.CurrentInstance;
            if (win == null || !win.IsLoaded || !win.IsLiveSyncEnabled)
                return;

            UIApplication uiApp = sender as UIApplication;
            if (uiApp == null || uiApp.ActiveUIDocument == null || uiApp.ActiveUIDocument.Document == null)
                return;

            // If geometry changed in Revit, trigger full sync
            if (_geometryDirty)
            {
                _geometryDirty = false;
                SyncHandler.RequestFullSync(win.GetSelectedMaxPoints());
                SyncExternalEvent.Raise();
                return;
            }

            // Check camera movement every 120ms for smooth Live Sync
            DateTime now = DateTime.UtcNow;
            if ((now - _lastCameraCheck).TotalMilliseconds < 120)
                return;
            _lastCameraCheck = now;

            View3D active3D = uiApp.ActiveUIDocument.ActiveView as View3D;
            if (active3D != null && !active3D.IsTemplate)
            {
                CameraInfoData cam = LiveSyncExternalHandler.ExtractCameraFromView(active3D);
                double diff = Math.Abs(cam.EyeX - _lastEyeX) + Math.Abs(cam.EyeY - _lastEyeY) + Math.Abs(cam.EyeZ - _lastEyeZ)
                            + Math.Abs(cam.TargetX - _lastTargetX) + Math.Abs(cam.TargetY - _lastTargetY) + Math.Abs(cam.TargetZ - _lastTargetZ);

                if (diff > 0.02)
                {
                    _lastEyeX = cam.EyeX;
                    _lastEyeY = cam.EyeY;
                    _lastEyeZ = cam.EyeZ;
                    _lastTargetX = cam.TargetX;
                    _lastTargetY = cam.TargetY;
                    _lastTargetZ = cam.TargetZ;

                    win.PushCameraUpdate(cam);
                }
            }
        }

        private static BitmapSource CreateVectorIcon(MediaColor primary, MediaColor accent, bool isPlayIcon)
        {
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                System.Windows.Rect bgRect = new System.Windows.Rect(2, 2, 28, 28);
                LinearGradientBrush grad = new LinearGradientBrush(primary, accent, 45.0);
                dc.DrawRoundedRectangle(grad, new Pen(Brushes.White, 1.0), bgRect, 6, 6);

                if (isPlayIcon)
                {
                    // Draw 3D cube + point cloud dots + play triangle
                    Brush white = Brushes.White;
                    dc.DrawEllipse(white, null, new System.Windows.Point(9, 9), 1.8, 1.8);
                    dc.DrawEllipse(white, null, new System.Windows.Point(15, 7), 1.5, 1.5);
                    dc.DrawEllipse(white, null, new System.Windows.Point(8, 15), 1.5, 1.5);
                    dc.DrawEllipse(white, null, new System.Windows.Point(22, 9), 1.6, 1.6);

                    StreamGeometry playGeo = new StreamGeometry();
                    using (StreamGeometryContext ctx = playGeo.Open())
                    {
                        ctx.BeginFigure(new System.Windows.Point(13, 12), true, true);
                        ctx.LineTo(new System.Windows.Point(23, 17.5), true, false);
                        ctx.LineTo(new System.Windows.Point(13, 23), true, false);
                    }
                    dc.DrawGeometry(Brushes.White, null, playGeo);
                }
                else
                {
                    // Draw Point Cloud cluster icon
                    Brush white = Brushes.White;
                    dc.DrawEllipse(white, null, new System.Windows.Point(10, 11), 2.2, 2.2);
                    dc.DrawEllipse(white, null, new System.Windows.Point(16, 9), 2.5, 2.5);
                    dc.DrawEllipse(white, null, new System.Windows.Point(22, 12), 2.0, 2.0);
                    dc.DrawEllipse(white, null, new System.Windows.Point(12, 18), 2.4, 2.4);
                    dc.DrawEllipse(white, null, new System.Windows.Point(18, 16), 2.6, 2.6);
                    dc.DrawEllipse(white, null, new System.Windows.Point(22, 20), 2.1, 2.1);
                    dc.DrawEllipse(white, null, new System.Windows.Point(14, 23), 1.9, 1.9);
                }
            }

            RenderTargetBitmap bmp = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class StartRendererCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (App.SyncHandler == null)
                {
                    App.SyncHandler = new LiveSyncExternalHandler();
                    App.SyncExternalEvent = ExternalEvent.Create(App.SyncHandler);
                }

                RendererWindow.ShowOrActivate(commandData.Application, App.SyncHandler, App.SyncExternalEvent);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class ForceSyncCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (App.SyncHandler == null)
                {
                    App.SyncHandler = new LiveSyncExternalHandler();
                    App.SyncExternalEvent = ExternalEvent.Create(App.SyncHandler);
                }

                RendererWindow.ShowOrActivate(commandData.Application, App.SyncHandler, App.SyncExternalEvent);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
