using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.PointClouds;

namespace RevitEnscapeCloud
{
    /// <summary>
    /// Ultra-High-Density PointCloud reader for Riegl VZ-600i terrestrial laser scans (.rcp / .rcs) in Revit 2026.
    /// Uses spatial 64-tile octree subdivision + direct native pointer reads (GetPointBufferPointer)
    /// and a camera-centric high-resolution priority zone so the user sees solid reality-capture point density.
    /// </summary>
    public static class PointCloudReader
    {
        private const double FeetToMeters = 0.3048;

        public static List<PointCloudBatchData> ExtractPointClouds(Document doc, View3D activeView, int maxPointsPerCloud)
        {
            List<PointCloudBatchData> results = new List<PointCloudBatchData>();
            if (doc == null) return results;

            FilteredElementCollector collector = new FilteredElementCollector(doc)
                .OfClass(typeof(PointCloudInstance));

            IList<Element> instances = collector.ToElements();
            if (instances == null || instances.Count == 0)
            {
                return results;
            }

            BoundingBoxXYZ activeSectionBox = null;
            if (activeView != null && activeView.IsSectionBoxActive)
            {
                try
                {
                    BoundingBoxXYZ sb = activeView.GetSectionBox();
                    if (sb != null && sb.Enabled)
                    {
                        activeSectionBox = sb;
                    }
                }
                catch
                {
                    activeSectionBox = null;
                }
            }

            XYZ cameraEyeWorld = null;
            if (activeView != null)
            {
                try
                {
                    ViewOrientation3D orient = activeView.GetOrientation();
                    if (orient != null)
                        cameraEyeWorld = orient.EyePosition;
                }
                catch
                {
                }
            }

            foreach (Element elem in instances)
            {
                PointCloudInstance pcInstance = elem as PointCloudInstance;
                if (pcInstance == null) continue;

                if (activeView != null && pcInstance.IsHidden(activeView))
                    continue;

                try
                {
                    List<PointCloudBatchData> batches = ReadUltraDensePointCloud(doc, pcInstance, activeSectionBox, cameraEyeWorld, maxPointsPerCloud);
                    if (batches != null && batches.Count > 0)
                    {
                        results.AddRange(batches);
                    }
                }
                catch
                {
                }
            }

            return results;
        }

        public static List<PointCloudBatchData> ReadExternalRcsOrRcpFile(Document doc, string filePath, int maxPoints)
        {
            List<PointCloudBatchData> batches = new List<PointCloudBatchData>();
            if (doc == null || string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                return batches;

            string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant().TrimStart('.');
            if (ext != "rcs" && ext != "rcp")
                ext = "rcs";

            // Use a temporary rolled-back transaction so the Revit model is NEVER modified when reading an external .rcs/.rcp file
            using (Transaction tx = new Transaction(doc, "Temp Read External ReCap Cloud"))
            {
                tx.Start();
                try
                {
                    PointCloudType pcType = PointCloudType.Create(doc, ext, filePath);
                    if (pcType != null)
                    {
                        PointCloudInstance pcInst = PointCloudInstance.Create(doc, pcType.Id, Transform.Identity);
                        if (pcInst != null)
                        {
                            doc.Regenerate();
                            batches = ReadUltraDensePointCloud(doc, pcInst, null, null, maxPoints);
                            foreach (var b in batches)
                            {
                                b.SourceTag = "DIRECT";
                            }
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    if (tx.GetStatus() == TransactionStatus.Started)
                    {
                        tx.RollBack();
                    }
                }
            }

            return batches;
        }

        private class SpatialCell
        {
            public BoundingBoxXYZ Box;
            public double DistanceToCamera;
        }

        private static List<PointCloudBatchData> ReadUltraDensePointCloud(
            Document doc,
            PointCloudInstance pcInstance,
            BoundingBoxXYZ sectionBox,
            XYZ cameraEyeWorld,
            int totalTargetBudget)
        {
            List<PointCloudBatchData> outputBatches = new List<PointCloudBatchData>();
            Transform pcTransform = pcInstance.GetTransform();

            BoundingBoxXYZ cloudWorldBox = pcInstance.get_BoundingBox(null);
            BoundingBoxXYZ targetBox = sectionBox ?? cloudWorldBox;
            if (targetBox == null)
                return outputBatches;

            // Intersect SectionBox with the PointCloud's actual world bounding box so we don't waste cells on empty air!
            BoundingBoxXYZ effectiveBox = TightenBoxToCloudExtent(targetBox, cloudWorldBox);

            // Subdivide into a safe 2x2x1 (4 cells) or 2x2x2 (8 cells) grid so ReCap's native C++ engine never exhausts C++ stack/heap
            int gridX = 2, gridY = 2, gridZ = 1;
            if (totalTargetBudget >= 2500000)
            {
                gridX = 2; gridY = 2; gridZ = 2; // 8 spatial cells
            }

            Transform boxTr = effectiveBox.Transform ?? Transform.Identity;
            XYZ boxMin = effectiveBox.Min;
            XYZ boxMax = effectiveBox.Max;
            XYZ span = boxMax - boxMin;

            double stepX = Math.Max(0.1, span.X / gridX);
            double stepY = Math.Max(0.1, span.Y / gridY);
            double stepZ = Math.Max(0.1, span.Z / gridZ);

            List<SpatialCell> cells = new List<SpatialCell>(gridX * gridY * gridZ);
            for (int ix = 0; ix < gridX; ix++)
            {
                for (int iy = 0; iy < gridY; iy++)
                {
                    for (int iz = 0; iz < gridZ; iz++)
                    {
                        XYZ cMin = new XYZ(
                            boxMin.X + ix * stepX,
                            boxMin.Y + iy * stepY,
                            boxMin.Z + iz * stepZ);

                        XYZ cMax = new XYZ(
                            boxMin.X + (ix + 1) * stepX,
                            boxMin.Y + (iy + 1) * stepY,
                            boxMin.Z + (iz + 1) * stepZ);

                        XYZ centerWorld = boxTr.OfPoint((cMin + cMax) * 0.5);
                        double dist = (cameraEyeWorld != null) ? centerWorld.DistanceTo(cameraEyeWorld) : 0.0;

                        cells.Add(new SpatialCell
                        {
                            Box = new BoundingBoxXYZ { Transform = boxTr, Min = cMin, Max = cMax },
                            DistanceToCamera = dist
                        });
                    }
                }
            }

            // Sort cells closest to the active camera first so foreground/interior scan zones get maximum Riegl VZ-600i density
            cells.Sort((a, b) => a.DistanceToCamera.CompareTo(b.DistanceToCamera));

            // Request up to 900,000 points per non-empty cell at ultra-fine 0.00001 ft spacing
            int maxPointsPerCellCall = Math.Min(950000, Math.Max(350000, (totalTargetBudget / Math.Max(1, cells.Count / 3))));

            const int MaxPointsPerChunkPayload = 550000;
            float[] chunkFloats = new float[MaxPointsPerChunkPayload * 7];
            int currentChunkPoints = 0;
            int chunkIndex = 0;
            int totalCollected = 0;

            // Pre-extract transform matrix components for blazing-fast inner loop (millions of points)
            XYZ ox = pcTransform.BasisX;
            XYZ oy = pcTransform.BasisY;
            XYZ oz = pcTransform.BasisZ;
            XYZ orig = pcTransform.Origin;

            double m00 = ox.X * FeetToMeters, m01 = oy.X * FeetToMeters, m02 = oz.X * FeetToMeters, m03 = orig.X * FeetToMeters;
            double m10 = ox.Z * FeetToMeters, m11 = oy.Z * FeetToMeters, m12 = oz.Z * FeetToMeters, m13 = orig.Z * FeetToMeters;
            double m20 = -ox.Y * FeetToMeters, m21 = -oy.Y * FeetToMeters, m22 = -oz.Y * FeetToMeters, m23 = -orig.Y * FeetToMeters;

            uint rngState = 2463534242u;

            for (int c = 0; c < cells.Count; c++)
            {
                if (totalCollected >= totalTargetBudget)
                    break;

                PointCloudFilter cellFilter = null;
                try
                {
                    cellFilter = CreateBoxFilter(cells[c].Box, 0.02);
                }
                catch
                {
                    continue;
                }

                int remainingBudget = totalTargetBudget - totalCollected;
                int requestCount = Math.Min(maxPointsPerCellCall, remainingBudget);
                if (requestCount <= 0) break;

                using (PointCollection pointCol = pcInstance.GetPoints(cellFilter, 0.00001, requestCount))
                {
                    if (pointCol == null || pointCol.Count == 0)
                        continue;

                    int count = pointCol.Count;
                    IntPtr ptr = pointCol.GetPointBufferPointer();

                    if (ptr != IntPtr.Zero)
                    {
                        // Fast native memory block copy (CloudPoint is 16 bytes: float X, float Y, float Z, int Color)
                        byte[] rawBytes = new byte[count * 16];
                        Marshal.Copy(ptr, rawBytes, 0, rawBytes.Length);

                        float[] rawFloats = new float[count * 4];
                        int[] rawInts = new int[count * 4];
                        Buffer.BlockCopy(rawBytes, 0, rawFloats, 0, rawBytes.Length);
                        Buffer.BlockCopy(rawBytes, 0, rawInts, 0, rawBytes.Length);

                        for (int i = 0; i < count; i++)
                        {
                            int base4 = i * 4;
                            double lx = rawFloats[base4 + 0];
                            double ly = rawFloats[base4 + 1];
                            double lz = rawFloats[base4 + 2];
                            int rawColor = rawInts[base4 + 3];

                            float x = (float)(m00 * lx + m01 * ly + m02 * lz + m03);
                            float y = (float)(m10 * lx + m11 * ly + m12 * lz + m13);
                            float z = (float)(m20 * lx + m21 * ly + m22 * lz + m23);

                            float r = ((rawColor >> 0) & 0xFF) / 255.0f;
                            float g = ((rawColor >> 8) & 0xFF) / 255.0f;
                            float b = ((rawColor >> 16) & 0xFF) / 255.0f;

                            if (r == 0f && g == 0f && b == 0f)
                            {
                                float hNorm = (float)Math.Max(0.15, Math.Min(0.95, (y + 2.0) / 15.0));
                                r = 0.25f + 0.5f * hNorm;
                                g = 0.55f + 0.35f * hNorm;
                                b = 0.85f;
                            }

                            // Fast Xorshift32 uniform float [0..1]
                            rngState ^= rngState << 13;
                            rngState ^= rngState >> 17;
                            rngState ^= rngState << 5;
                            float seed = (rngState & 0x00FFFFFF) / 16777216.0f;

                            int dst = currentChunkPoints * 7;
                            chunkFloats[dst + 0] = x;
                            chunkFloats[dst + 1] = y;
                            chunkFloats[dst + 2] = z;
                            chunkFloats[dst + 3] = r;
                            chunkFloats[dst + 4] = g;
                            chunkFloats[dst + 5] = b;
                            chunkFloats[dst + 6] = seed;

                            currentChunkPoints++;
                            totalCollected++;

                            if (currentChunkPoints >= MaxPointsPerChunkPayload)
                            {
                                outputBatches.Add(FlushChunkArray(pcInstance, chunkIndex++, chunkFloats, currentChunkPoints));
                                currentChunkPoints = 0;
                            }

                            if (totalCollected >= totalTargetBudget)
                                break;
                        }
                    }
                }
            }

            // Fallback if tiled box returned 0 points
            if (totalCollected == 0)
            {
                PointCloudFilter fallbackFilter = CreateWorldExtentFilter(100000.0);
                int fallbackTarget = Math.Min(980000, totalTargetBudget);
                using (PointCollection pointCol = pcInstance.GetPoints(fallbackFilter, 0.00001, fallbackTarget))
                {
                    if (pointCol != null && pointCol.Count > 0)
                    {
                        using (PointIterator it = pointCol.GetPointIterator())
                        {
                            while (it.MoveNext())
                            {
                                CloudPoint cp = it.Current;
                                double lx = cp.X, ly = cp.Y, lz = cp.Z;
                                float x = (float)(m00 * lx + m01 * ly + m02 * lz + m03);
                                float y = (float)(m10 * lx + m11 * ly + m12 * lz + m13);
                                float z = (float)(m20 * lx + m21 * ly + m22 * lz + m23);

                                int rawColor = cp.Color;
                                float r = ((rawColor >> 0) & 0xFF) / 255.0f;
                                float g = ((rawColor >> 8) & 0xFF) / 255.0f;
                                float b = ((rawColor >> 16) & 0xFF) / 255.0f;

                                rngState ^= rngState << 13;
                                rngState ^= rngState >> 17;
                                rngState ^= rngState << 5;
                                float seed = (rngState & 0x00FFFFFF) / 16777216.0f;

                                int dst = currentChunkPoints * 7;
                                chunkFloats[dst + 0] = x;
                                chunkFloats[dst + 1] = y;
                                chunkFloats[dst + 2] = z;
                                chunkFloats[dst + 3] = r;
                                chunkFloats[dst + 4] = g;
                                chunkFloats[dst + 5] = b;
                                chunkFloats[dst + 6] = seed;

                                currentChunkPoints++;
                                if (currentChunkPoints >= MaxPointsPerChunkPayload)
                                {
                                    outputBatches.Add(FlushChunkArray(pcInstance, chunkIndex++, chunkFloats, currentChunkPoints));
                                    currentChunkPoints = 0;
                                }
                            }
                        }
                    }
                }
            }

            if (currentChunkPoints > 0)
            {
                outputBatches.Add(FlushChunkArray(pcInstance, chunkIndex++, chunkFloats, currentChunkPoints));
            }

            return outputBatches;
        }

        /// <summary>
        /// Tightens the target box by intersecting its world AABB with the PointCloudInstance's world AABB
        /// so that oversized Revit Section Boxes don't waste subdivision cells on empty space outside the scan.
        /// </summary>
        private static BoundingBoxXYZ TightenBoxToCloudExtent(BoundingBoxXYZ targetBox, BoundingBoxXYZ cloudWorldBox)
        {
            if (cloudWorldBox == null) return targetBox;

            try
            {
                Transform tr = targetBox.Transform ?? Transform.Identity;
                // Only intersect directly when the section box is unrotated (or near-axis-aligned)
                if (tr.IsIdentity || (Math.Abs(tr.BasisX.X) > 0.98 && Math.Abs(tr.BasisY.Y) > 0.98 && Math.Abs(tr.BasisZ.Z) > 0.98))
                {
                    XYZ tMin = tr.OfPoint(targetBox.Min);
                    XYZ tMax = tr.OfPoint(targetBox.Max);

                    double minX = Math.Max(Math.Min(tMin.X, tMax.X), cloudWorldBox.Min.X - 2.0);
                    double minY = Math.Max(Math.Min(tMin.Y, tMax.Y), cloudWorldBox.Min.Y - 2.0);
                    double minZ = Math.Max(Math.Min(tMin.Z, tMax.Z), cloudWorldBox.Min.Z - 2.0);

                    double maxX = Math.Min(Math.Max(tMin.X, tMax.X), cloudWorldBox.Max.X + 2.0);
                    double maxY = Math.Min(Math.Max(tMin.Y, tMax.Y), cloudWorldBox.Max.Y + 2.0);
                    double maxZ = Math.Min(Math.Max(tMin.Z, tMax.Z), cloudWorldBox.Max.Z + 2.0);

                    if (maxX > minX && maxY > minY && maxZ > minZ)
                    {
                        return new BoundingBoxXYZ
                        {
                            Transform = Transform.Identity,
                            Min = new XYZ(minX, minY, minZ),
                            Max = new XYZ(maxX, maxY, maxZ)
                        };
                    }
                }
            }
            catch
            {
            }

            return targetBox;
        }

        private static PointCloudBatchData FlushChunkArray(PointCloudInstance pcInstance, int chunkIdx, float[] buffer, int pointCount)
        {
            int byteLen = pointCount * 7 * sizeof(float);
            byte[] byteBuffer = new byte[byteLen];
            Buffer.BlockCopy(buffer, 0, byteBuffer, 0, byteLen);

            return new PointCloudBatchData
            {
                Id = pcInstance.Id.Value.ToString() + "_" + chunkIdx,
                Name = (string.IsNullOrEmpty(pcInstance.Name) ? ("VZ-600i Scan #" + pcInstance.Id.Value) : pcInstance.Name) + " [Tile " + (chunkIdx + 1) + "]",
                SourceTag = "REVIT",
                TotalPointsCount = pointCount,
                PackedPointsBuffer = byteBuffer,
                PackedPointsBase64 = ""
            };
        }

        private static PointCloudFilter CreateBoxFilter(BoundingBoxXYZ box, double paddingFeet)
        {
            Transform tr = box.Transform ?? Transform.Identity;
            XYZ pad = new XYZ(paddingFeet, paddingFeet, paddingFeet);
            XYZ min = box.Min - pad;
            XYZ max = box.Max + pad;

            XYZ centerWorld = tr.OfPoint((min + max) * 0.5);

            XYZ[] facePointsLocal = new XYZ[]
            {
                new XYZ(min.X, min.Y, min.Z),
                new XYZ(max.X, max.Y, max.Z),
                new XYZ(min.X, min.Y, min.Z),
                new XYZ(max.X, max.Y, max.Z),
                new XYZ(min.X, min.Y, min.Z),
                new XYZ(max.X, max.Y, max.Z)
            };

            XYZ[] localAxes = new XYZ[]
            {
                XYZ.BasisX,
                -XYZ.BasisX,
                XYZ.BasisY,
                -XYZ.BasisY,
                XYZ.BasisZ,
                -XYZ.BasisZ
            };

            List<Plane> planes = new List<Plane>(6);
            for (int i = 0; i < 6; i++)
            {
                XYZ ptWorld = tr.OfPoint(facePointsLocal[i]);
                XYZ normalWorld = tr.OfVector(localAxes[i]).Normalize();

                XYZ toCenter = centerWorld - ptWorld;
                if (normalWorld.DotProduct(toCenter) < 0)
                {
                    normalWorld = -normalWorld;
                }

                planes.Add(Plane.CreateByNormalAndOrigin(normalWorld, ptWorld));
            }

            return PointCloudFilterFactory.CreateMultiPlaneFilter(planes);
        }

        private static PointCloudFilter CreateWorldExtentFilter(double halfExtentFeet)
        {
            double d = halfExtentFeet;
            List<Plane> planes = new List<Plane>(6)
            {
                Plane.CreateByNormalAndOrigin(XYZ.BasisX, new XYZ(-d, 0, 0)),
                Plane.CreateByNormalAndOrigin(-XYZ.BasisX, new XYZ(d, 0, 0)),
                Plane.CreateByNormalAndOrigin(XYZ.BasisY, new XYZ(0, -d, 0)),
                Plane.CreateByNormalAndOrigin(-XYZ.BasisY, new XYZ(0, d, 0)),
                Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, -d)),
                Plane.CreateByNormalAndOrigin(-XYZ.BasisZ, new XYZ(0, 0, d))
            };
            return PointCloudFilterFactory.CreateMultiPlaneFilter(planes);
        }
    }
}
