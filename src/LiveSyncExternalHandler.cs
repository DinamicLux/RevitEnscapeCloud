using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitEnscapeCloud
{
    public enum ExternalRequestType
    {
        None = 0,
        SyncFullScene = 1,
        SyncPointCloudsOnly = 2,
        UpdateRevitCameraFromViewer = 3,
        LoadDirectRcsFile = 4
    }

    /// <summary>
    /// Thread-safe IExternalEventHandler that bridges the modeless WPF/WebView2 renderer window
    /// with the Revit 2026 API context.
    /// </summary>
    public class LiveSyncExternalHandler : IExternalEventHandler
    {
        private readonly object _lock = new object();
        private ExternalRequestType _pendingRequest = ExternalRequestType.None;
        private int _requestedMaxPointsPerCloud = 950000;
        private CameraInfoData _incomingViewerCamera;
        private string _pendingRcsFilePath;

        public event Action<ScenePayload> OnSceneExported;
        public event Action<List<PointCloudBatchData>, string> OnDirectCloudLoaded;
        public event Action<CameraInfoData> OnRevitCameraChanged;
        public event Action<string> OnStatusMessage;

        private const double FeetToMeters = 0.3048;

        public void RequestFullSync(int maxPointsPerCloud)
        {
            lock (_lock)
            {
                _requestedMaxPointsPerCloud = maxPointsPerCloud;
                _pendingRequest = ExternalRequestType.SyncFullScene;
            }
        }

        public void RequestPointCloudsRefresh(int maxPointsPerCloud)
        {
            lock (_lock)
            {
                _requestedMaxPointsPerCloud = maxPointsPerCloud;
                _pendingRequest = ExternalRequestType.SyncPointCloudsOnly;
            }
        }

        public void RequestPushCameraToRevit(CameraInfoData cam)
        {
            lock (_lock)
            {
                _incomingViewerCamera = cam;
                _pendingRequest = ExternalRequestType.UpdateRevitCameraFromViewer;
            }
        }

        public void RequestLoadDirectRcs(string rcsFilePath, int maxPoints)
        {
            lock (_lock)
            {
                _pendingRcsFilePath = rcsFilePath;
                _requestedMaxPointsPerCloud = maxPoints;
                _pendingRequest = ExternalRequestType.LoadDirectRcsFile;
            }
        }

        public void Execute(UIApplication app)
        {
            ExternalRequestType req;
            int maxPts;
            CameraInfoData viewerCam;
            string rcsPath;

            lock (_lock)
            {
                req = _pendingRequest;
                maxPts = _requestedMaxPointsPerCloud;
                viewerCam = _incomingViewerCamera;
                rcsPath = _pendingRcsFilePath;
                _pendingRequest = ExternalRequestType.None;
            }

            if (req == ExternalRequestType.None || app == null || app.ActiveUIDocument == null)
                return;

            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc.Document;
            if (doc == null) return;

            try
            {
                View3D view3d = FindOrActivate3DView(uidoc);
                if (view3d == null)
                {
                    if (OnStatusMessage != null)
                        OnStatusMessage("Abre o crea una Vista 3D en Revit para sincronizar con el renderizador.");
                    return;
                }

                if (req == ExternalRequestType.SyncFullScene)
                {
                    if (OnStatusMessage != null)
                        OnStatusMessage("Exportando geometría 3D y nube de puntos de la vista '" + view3d.Name + "'...");

                    ScenePayload payload = ExportFullScene(doc, view3d, maxPts, OnStatusMessage);
                    if (OnSceneExported != null)
                        OnSceneExported(payload);
                }
                else if (req == ExternalRequestType.SyncPointCloudsOnly)
                {
                    ScenePayload payload = new ScenePayload
                    {
                        DocumentTitle = doc.Title,
                        ViewName = view3d.Name,
                        Camera = ExtractCameraFromView(view3d),
                        PointClouds = PointCloudReader.ExtractPointClouds(doc, view3d, maxPts)
                    };
                    if (OnSceneExported != null)
                        OnSceneExported(payload);
                }
                else if (req == ExternalRequestType.UpdateRevitCameraFromViewer && viewerCam != null)
                {
                    ApplyCameraToRevitView(uidoc, view3d, viewerCam);
                }
                else if (req == ExternalRequestType.LoadDirectRcsFile && !string.IsNullOrEmpty(rcsPath))
                {
                    if (OnStatusMessage != null)
                        OnStatusMessage("Leyendo archivo ReCap (" + System.IO.Path.GetFileName(rcsPath) + ") con motor nativo de Revit 2026...");

                    List<PointCloudBatchData> batches = PointCloudReader.ReadExternalRcsOrRcpFile(doc, rcsPath, maxPts);
                    if (OnDirectCloudLoaded != null)
                        OnDirectCloudLoaded(batches, System.IO.Path.GetFileName(rcsPath));
                }
            }
            catch (Exception ex)
            {
                if (OnStatusMessage != null)
                    OnStatusMessage("Error en sincronización Revit 2026: " + ex.Message);
            }
        }

        public static ScenePayload ExportFullScene(Document doc, View3D view3d, int maxPointsPerCloud, Action<string> logStatus = null)
        {
            ScenePayload payload = new ScenePayload
            {
                DocumentTitle = doc.Title ?? "Proyecto Revit 2026",
                ViewName = view3d.Name ?? "{3D}"
            };

            // 1. Extract Camera Orientation & Revit Shared Coordinates Origin (Project Base Point / Survey Point)
            payload.Camera = ExtractCameraFromView(view3d);
            try
            {
                if (doc.ActiveProjectLocation != null)
                {
                    ProjectPosition pp = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
                    if (pp != null)
                    {
                        payload.SharedOriginEast = pp.EastWest * FeetToMeters;
                        payload.SharedOriginNorth = pp.NorthSouth * FeetToMeters;
                        payload.SharedOriginElev = pp.Elevation * FeetToMeters;
                        payload.SharedAngleRad = pp.Angle;
                    }
                }
            }
            catch
            {
            }

            // 2. Extract Sun & Shadow Settings
            try
            {
                SunAndShadowSettings sun = view3d.SunAndShadowSettings;
                if (sun != null)
                {
                    payload.SunAltitude = sun.Altitude * (180.0 / Math.PI);
                    payload.SunAzimuth = sun.Azimuth * (180.0 / Math.PI);
                    DateTime dt = sun.ActiveFrameTime;
                    payload.TimeOfDayHours = dt.Hour + (dt.Minute / 60.0);
                }
            }
            catch
            {
            }

            // 3. Export 3D Geometry via CustomExporter (with automatic fallback to Direct Element Geometry if CustomExporter fails or is blocked)
            try
            {
                SceneExportContext context = new SceneExportContext(doc);
                using (CustomExporter exporter = new CustomExporter(doc, context))
                {
                    exporter.IncludeGeometricObjects = false;
                    exporter.ShouldStopOnError = false;
                    exporter.Export(view3d);
                }
                payload.Meshes = context.GetMeshBatches();
            }
            catch (Exception ex)
            {
                if (logStatus != null)
                    logStatus("CustomExporter aviso: " + ex.Message + " -> Usando extractor geométrico directo.");
            }

            // Fallback: If CustomExporter returned 0 meshes (e.g., view in Wireframe mode or rendering unsupported), extract Element Geometry directly!
            if (payload.Meshes == null || payload.Meshes.Count == 0)
            {
                try
                {
                    payload.Meshes = ExtractGeometryFallback(doc, view3d);
                }
                catch
                {
                }
            }

            // 4. Extract Linked Point Clouds (.rcp / .rcs)
            try
            {
                payload.PointClouds = PointCloudReader.ExtractPointClouds(doc, view3d, maxPointsPerCloud);
            }
            catch (Exception ex)
            {
                if (logStatus != null)
                    logStatus("Error leyendo PointCloudInstance: " + ex.Message);
            }

            return payload;
        }

        private static List<MeshBatchData> ExtractGeometryFallback(Document doc, View3D view3d)
        {
            Dictionary<string, MeshBatchData> batches = new Dictionary<string, MeshBatchData>();
            Options geomOpts = new Options
            {
                ComputeReferences = false,
                DetailLevel = ViewDetailLevel.Fine,
                IncludeNonVisibleObjects = false
            };

            FilteredElementCollector collector = new FilteredElementCollector(doc, view3d.Id)
                .WhereElementIsNotElementType();

            foreach (Element elem in collector)
            {
                if (elem is PointCloudInstance || elem is View || elem is ElementType)
                    continue;
                if (elem.Category == null)
                    continue;

                try
                {
                    GeometryElement geoElem = elem.get_Geometry(geomOpts);
                    if (geoElem == null) continue;
                    AppendGeometryElement(doc, geoElem, Transform.Identity, batches, elem.Category);
                }
                catch
                {
                }
            }

            return batches.Values.Where(b => b.Indices.Count > 0).ToList();
        }

        private static void AppendGeometryElement(
            Document doc,
            GeometryElement geoElem,
            Transform currentTr,
            Dictionary<string, MeshBatchData> batches,
            Category fallbackCategory)
        {
            foreach (GeometryObject obj in geoElem)
            {
                Solid solid = obj as Solid;
                if (solid != null && solid.Faces.Size > 0)
                {
                    foreach (Face face in solid.Faces)
                    {
                        try
                        {
                            Mesh mesh = face.Triangulate();
                            if (mesh == null || mesh.NumTriangles == 0) continue;

                            ElementId matId = face.MaterialElementId;
                            MeshBatchData batch = GetOrCreateFallbackBatch(doc, batches, matId, fallbackCategory);
                            AppendRevitMesh(batch, mesh, currentTr);
                        }
                        catch
                        {
                        }
                    }
                    continue;
                }

                Mesh directMesh = obj as Mesh;
                if (directMesh != null && directMesh.NumTriangles > 0)
                {
                    MeshBatchData batch = GetOrCreateFallbackBatch(doc, batches, directMesh.MaterialElementId, fallbackCategory);
                    AppendRevitMesh(batch, directMesh, currentTr);
                    continue;
                }

                GeometryInstance inst = obj as GeometryInstance;
                if (inst != null)
                {
                    Transform nextTr = currentTr.Multiply(inst.Transform);
                    GeometryElement symGeo = inst.GetSymbolGeometry();
                    if (symGeo != null)
                    {
                        AppendGeometryElement(doc, symGeo, nextTr, batches, fallbackCategory);
                    }
                }
            }
        }

        private static MeshBatchData GetOrCreateFallbackBatch(
            Document doc,
            Dictionary<string, MeshBatchData> batches,
            ElementId matId,
            Category fallbackCategory)
        {
            string key = (matId != null && matId != ElementId.InvalidElementId)
                ? matId.Value.ToString()
                : (fallbackCategory != null ? "cat_" + fallbackCategory.Id.Value : "default");

            MeshBatchData batch;
            if (!batches.TryGetValue(key, out batch))
            {
                batch = new MeshBatchData
                {
                    MaterialId = key,
                    MaterialName = "Material BIM",
                    ColorR = 205,
                    ColorG = 208,
                    ColorB = 212,
                    Roughness = 0.65,
                    Metalness = 0.08
                };

                if (matId != null && matId != ElementId.InvalidElementId)
                {
                    Material mat = doc.GetElement(matId) as Material;
                    if (mat != null)
                    {
                        batch.MaterialName = mat.Name ?? "Material";
                        if (mat.Color != null && mat.Color.IsValid)
                        {
                            batch.ColorR = mat.Color.Red;
                            batch.ColorG = mat.Color.Green;
                            batch.ColorB = mat.Color.Blue;
                        }
                        batch.Transparency = mat.Transparency / 100.0;
                    }
                }
                else if (fallbackCategory != null && fallbackCategory.LineColor != null && fallbackCategory.LineColor.IsValid)
                {
                    batch.MaterialName = fallbackCategory.Name ?? "Categoría";
                    batch.ColorR = fallbackCategory.LineColor.Red;
                    batch.ColorG = fallbackCategory.LineColor.Green;
                    batch.ColorB = fallbackCategory.LineColor.Blue;
                }

                batches[key] = batch;
            }
            return batch;
        }

        private static void AppendRevitMesh(MeshBatchData batch, Mesh mesh, Transform tr)
        {
            int baseVertex = batch.Positions.Count / 3;
            IList<XYZ> verts = mesh.Vertices;
            int numVerts = verts.Count;

            for (int i = 0; i < numVerts; i++)
            {
                XYZ p = tr.OfPoint(verts[i]);
                batch.Positions.Add((float)(p.X * FeetToMeters));
                batch.Positions.Add((float)(p.Z * FeetToMeters));
                batch.Positions.Add((float)(-p.Y * FeetToMeters));

                batch.Normals.Add(0f);
                batch.Normals.Add(1f);
                batch.Normals.Add(0f);
            }

            int numTris = mesh.NumTriangles;
            for (int i = 0; i < numTris; i++)
            {
                MeshTriangle tri = mesh.get_Triangle(i);
                batch.Indices.Add(baseVertex + (int)tri.get_Index(0));
                batch.Indices.Add(baseVertex + (int)tri.get_Index(1));
                batch.Indices.Add(baseVertex + (int)tri.get_Index(2));
            }
        }

        public static CameraInfoData ExtractCameraFromView(View3D view3d)
        {
            CameraInfoData cam = new CameraInfoData
            {
                EyeX = 12, EyeY = 8, EyeZ = 15,
                TargetX = 0, TargetY = 1.5, TargetZ = 0,
                UpX = 0, UpY = 1, UpZ = 0
            };

            try
            {
                ViewOrientation3D orient = view3d.GetOrientation();
                if (orient != null)
                {
                    XYZ eye = orient.EyePosition;
                    XYZ fwd = orient.ForwardDirection.Normalize();
                    XYZ up = orient.UpDirection.Normalize();

                    // Convert Revit (X, Y, Z in feet) -> Three.js (X, Z, -Y in meters)
                    cam.EyeX = eye.X * FeetToMeters;
                    cam.EyeY = eye.Z * FeetToMeters;
                    cam.EyeZ = -eye.Y * FeetToMeters;

                    double targetDistMeters = 10.0;
                    cam.TargetX = cam.EyeX + fwd.X * targetDistMeters;
                    cam.TargetY = cam.EyeY + fwd.Z * targetDistMeters;
                    cam.TargetZ = cam.EyeZ + (-fwd.Y) * targetDistMeters;

                    cam.UpX = up.X;
                    cam.UpY = up.Z;
                    cam.UpZ = -up.Y;
                }
            }
            catch
            {
            }

            return cam;
        }

        private void ApplyCameraToRevitView(UIDocument uidoc, View3D view3d, CameraInfoData cam)
        {
            if (view3d.IsLocked) return;

            try
            {
                // Convert Three.js (X, Y, Z in meters) -> Revit (X, -Z, Y in feet)
                XYZ eye = new XYZ(
                    cam.EyeX / FeetToMeters,
                    -cam.EyeZ / FeetToMeters,
                    cam.EyeY / FeetToMeters);

                XYZ target = new XYZ(
                    cam.TargetX / FeetToMeters,
                    -cam.TargetZ / FeetToMeters,
                    cam.TargetY / FeetToMeters);

                XYZ forward = (target - eye);
                if (forward.GetLength() < 1e-4)
                    forward = new XYZ(0, 1, 0);
                else
                    forward = forward.Normalize();

                XYZ worldUp = XYZ.BasisZ;
                XYZ right = forward.CrossProduct(worldUp);
                if (right.GetLength() < 1e-4)
                    right = XYZ.BasisX;
                else
                    right = right.Normalize();

                XYZ orthoUp = right.CrossProduct(forward).Normalize();

                using (Transaction tx = new Transaction(uidoc.Document, "EnscapeCloud Sync Camera"))
                {
                    tx.Start();
                    ViewOrientation3D newOrient = new ViewOrientation3D(eye, orthoUp, forward);
                    view3d.SetOrientation(newOrient);
                    tx.Commit();
                }

                uidoc.RefreshActiveView();
            }
            catch
            {
            }
        }

        public static View3D FindOrActivate3DView(UIDocument uidoc)
        {
            if (uidoc == null || uidoc.Document == null) return null;
            View3D active3D = uidoc.ActiveView as View3D;
            if (active3D != null && !active3D.IsTemplate)
                return active3D;

            FilteredElementCollector col = new FilteredElementCollector(uidoc.Document)
                .OfClass(typeof(View3D));

            foreach (Element el in col)
            {
                View3D v = el as View3D;
                if (v != null && !v.IsTemplate)
                {
                    return v;
                }
            }
            return null;
        }

        public string GetName()
        {
            return "RevitEnscapeCloud LiveSync Handler";
        }
    }
}
