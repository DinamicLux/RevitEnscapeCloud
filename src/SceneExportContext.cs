using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitEnscapeCloud
{
    /// <summary>
    /// CustomExporter context for Revit 2026 that extracts all 3D tessellated geometry
    /// and PBR material attributes into Three.js coordinate system (Y-up, meters).
    /// </summary>
    public class SceneExportContext : IExportContext
    {
        private readonly Document _rootDoc;
        private readonly Stack<Document> _docStack = new Stack<Document>();
        private readonly Stack<Transform> _transformStack = new Stack<Transform>();
        private readonly Dictionary<string, MeshBatchData> _batchesByMaterial = new Dictionary<string, MeshBatchData>();
        private MeshBatchData _currentBatch;

        private const double FeetToMeters = 0.3048;

        public SceneExportContext(Document doc)
        {
            _rootDoc = doc;
            _docStack.Push(doc);
            _transformStack.Push(Transform.Identity);
        }

        public List<MeshBatchData> GetMeshBatches()
        {
            List<MeshBatchData> result = new List<MeshBatchData>();
            foreach (KeyValuePair<string, MeshBatchData> kvp in _batchesByMaterial)
            {
                if (kvp.Value.Indices.Count > 0)
                {
                    result.Add(kvp.Value);
                }
            }
            return result;
        }

        public bool Start()
        {
            EnsureDefaultBatch();
            return true;
        }

        public void Finish()
        {
        }

        public bool IsCanceled()
        {
            return false;
        }

        public RenderNodeAction OnViewBegin(ViewNode node)
        {
            // Medium-high detail level (8 out of 15) for fast real-time rendering with smooth curves
            node.LevelOfDetail = 8;
            return RenderNodeAction.Proceed;
        }

        public void OnViewEnd(ElementId elementId)
        {
        }

        public RenderNodeAction OnElementBegin(ElementId elementId)
        {
            return RenderNodeAction.Proceed;
        }

        public void OnElementEnd(ElementId elementId)
        {
        }

        public RenderNodeAction OnInstanceBegin(InstanceNode node)
        {
            Transform current = _transformStack.Peek();
            _transformStack.Push(current.Multiply(node.GetTransform()));
            return RenderNodeAction.Proceed;
        }

        public void OnInstanceEnd(InstanceNode node)
        {
            if (_transformStack.Count > 1)
                _transformStack.Pop();
        }

        public RenderNodeAction OnLinkBegin(LinkNode node)
        {
            Transform current = _transformStack.Peek();
            _transformStack.Push(current.Multiply(node.GetTransform()));
            Document linkDoc = node.GetDocument();
            _docStack.Push(linkDoc ?? _rootDoc);
            return RenderNodeAction.Proceed;
        }

        public void OnLinkEnd(LinkNode node)
        {
            if (_transformStack.Count > 1)
                _transformStack.Pop();
            if (_docStack.Count > 1)
                _docStack.Pop();
        }

        public RenderNodeAction OnFaceBegin(FaceNode node)
        {
            return RenderNodeAction.Proceed;
        }

        public void OnFaceEnd(FaceNode node)
        {
        }

        public void OnRPC(RPCNode node)
        {
        }

        public void OnLight(LightNode node)
        {
        }

        public void OnMaterial(MaterialNode node)
        {
            string key = "default";
            if (node.MaterialId != null && node.MaterialId != ElementId.InvalidElementId)
            {
                key = node.MaterialId.Value.ToString();
            }
            else if (node.Color != null && node.Color.IsValid)
            {
                key = string.Format("clr_{0}_{1}_{2}_{3:F2}", node.Color.Red, node.Color.Green, node.Color.Blue, node.Transparency);
            }

            MeshBatchData batch;
            if (!_batchesByMaterial.TryGetValue(key, out batch))
            {
                batch = new MeshBatchData();
                batch.MaterialId = key;
                batch.MaterialName = "Default Material";

                if (node.Color != null && node.Color.IsValid)
                {
                    batch.ColorR = node.Color.Red;
                    batch.ColorG = node.Color.Green;
                    batch.ColorB = node.Color.Blue;
                }

                batch.Transparency = Math.Max(0.0, Math.Min(1.0, node.Transparency));
                // Map Revit Smoothness/Glossiness to PBR Roughness & Metalness
                double smoothness = Math.Max(0, Math.Min(100, node.Smoothness)) / 100.0;
                batch.Roughness = Math.Max(0.05, 1.0 - smoothness * 0.85);
                batch.Metalness = (node.Glossiness > 70) ? 0.6 : 0.08;

                try
                {
                    Document curDoc = _docStack.Count > 0 ? _docStack.Peek() : _rootDoc;
                    if (curDoc != null && node.MaterialId != null && node.MaterialId != ElementId.InvalidElementId)
                    {
                        Material mat = curDoc.GetElement(node.MaterialId) as Material;
                        if (mat != null)
                        {
                            batch.MaterialName = mat.Name ?? "Material";
                            string lowerName = batch.MaterialName.ToLowerInvariant();
                            if (lowerName.Contains("glass") || lowerName.Contains("vidrio") || lowerName.Contains("cristal"))
                            {
                                if (batch.Transparency < 0.5) batch.Transparency = 0.65;
                                batch.Roughness = 0.05;
                                batch.Metalness = 0.1;
                            }
                            else if (lowerName.Contains("metal") || lowerName.Contains("steel") || lowerName.Contains("acero") || lowerName.Contains("alumin"))
                            {
                                batch.Metalness = 0.85;
                                batch.Roughness = 0.25;
                            }
                            else if (lowerName.Contains("water") || lowerName.Contains("agua"))
                            {
                                batch.Transparency = 0.4;
                                batch.Roughness = 0.08;
                            }
                        }
                    }
                }
                catch
                {
                }

                _batchesByMaterial[key] = batch;
            }

            _currentBatch = batch;
        }

        public void OnPolymesh(PolymeshTopology node)
        {
            if (_currentBatch == null)
            {
                EnsureDefaultBatch();
            }

            Transform tr = _transformStack.Peek();
            int baseVertexIndex = _currentBatch.Positions.Count / 3;

            IList<XYZ> pts = node.GetPoints();
            int numPts = pts.Count;

            for (int i = 0; i < numPts; i++)
            {
                XYZ p = tr.OfPoint(pts[i]);
                // Convert Revit (X right, Y north, Z up in feet) -> Three.js (X right, Y up, Z south in meters)
                float x = (float)(p.X * FeetToMeters);
                float y = (float)(p.Z * FeetToMeters);
                float z = (float)(-p.Y * FeetToMeters);

                _currentBatch.Positions.Add(x);
                _currentBatch.Positions.Add(y);
                _currentBatch.Positions.Add(z);
            }

            DistributionOfNormals dist = node.DistributionOfNormals;
            IList<XYZ> norms = node.GetNormals();

            if (dist == DistributionOfNormals.AtEachPoint && norms.Count == numPts)
            {
                for (int i = 0; i < numPts; i++)
                {
                    XYZ n = tr.OfVector(norms[i]).Normalize();
                    _currentBatch.Normals.Add((float)n.X);
                    _currentBatch.Normals.Add((float)n.Z);
                    _currentBatch.Normals.Add((float)(-n.Y));
                }
            }
            else
            {
                XYZ defaultNorm = new XYZ(0, 0, 1);
                if (norms.Count > 0)
                {
                    defaultNorm = tr.OfVector(norms[0]).Normalize();
                }
                float nx = (float)defaultNorm.X;
                float ny = (float)defaultNorm.Z;
                float nz = (float)(-defaultNorm.Y);

                for (int i = 0; i < numPts; i++)
                {
                    _currentBatch.Normals.Add(nx);
                    _currentBatch.Normals.Add(ny);
                    _currentBatch.Normals.Add(nz);
                }
            }

            IList<PolymeshFacet> facets = node.GetFacets();
            int numFacets = facets.Count;
            for (int i = 0; i < numFacets; i++)
            {
                PolymeshFacet f = facets[i];
                _currentBatch.Indices.Add(baseVertexIndex + f.V1);
                _currentBatch.Indices.Add(baseVertexIndex + f.V2);
                _currentBatch.Indices.Add(baseVertexIndex + f.V3);
            }
        }

        private void EnsureDefaultBatch()
        {
            MeshBatchData batch;
            if (!_batchesByMaterial.TryGetValue("default", out batch))
            {
                batch = new MeshBatchData
                {
                    MaterialId = "default",
                    MaterialName = "Default Architecture",
                    ColorR = 215,
                    ColorG = 215,
                    ColorB = 210,
                    Roughness = 0.65,
                    Metalness = 0.05
                };
                _batchesByMaterial["default"] = batch;
            }
            _currentBatch = batch;
        }
    }
}
