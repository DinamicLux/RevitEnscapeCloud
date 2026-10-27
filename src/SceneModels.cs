using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RevitEnscapeCloud
{
    public class MeshBatchData
    {
        public string MaterialId;
        public string MaterialName;
        public int ColorR = 200;
        public int ColorG = 200;
        public int ColorB = 200;
        public double Transparency = 0.0;
        public double Roughness = 0.5;
        public double Metalness = 0.1;
        public List<float> Positions = new List<float>();
        public List<float> Normals = new List<float>();
        public List<int> Indices = new List<int>();
    }

    public class PointCloudBatchData
    {
        public string Id;
        public string Name;
        public string SourceTag = "REVIT"; // "REVIT" or "DIRECT"
        public int TotalPointsCount;
        // Interleaved float array: x, y, z, r, g, b, heightAboveGround (7 floats = 28 bytes per point)
        public byte[] PackedPointsBuffer;
        public string PackedPointsBase64;
    }

    public class CameraInfoData
    {
        public double EyeX, EyeY, EyeZ;
        public double TargetX, TargetY, TargetZ;
        public double UpX, UpY, UpZ;
    }

    public class ScenePayload
    {
        public string DocumentTitle = "Revit 2026 Model";
        public string ViewName = "3D View";
        public CameraInfoData Camera = new CameraInfoData();
        public double SunAltitude = 45.0;
        public double SunAzimuth = 135.0;
        public double TimeOfDayHours = 13.5;
        public double SharedOriginEast = 1083692.001;
        public double SharedOriginNorth = 1275923.913;
        public double SharedOriginElev = 382.670;
        public double SharedAngleRad = 0.0;
        public List<MeshBatchData> Meshes = new List<MeshBatchData>();
        public List<PointCloudBatchData> PointClouds = new List<PointCloudBatchData>();

        public string ToJson()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder(1024 * 1024);
            sb.Append("{");
            sb.AppendFormat(inv, "\"documentTitle\":\"{0}\",", Escape(DocumentTitle));
            sb.AppendFormat(inv, "\"viewName\":\"{0}\",", Escape(ViewName));
            sb.AppendFormat(inv, "\"sunAltitude\":{0:F3},", SunAltitude);
            sb.AppendFormat(inv, "\"sunAzimuth\":{0:F3},", SunAzimuth);
            sb.AppendFormat(inv, "\"timeOfDayHours\":{0:F2},", TimeOfDayHours);
            sb.AppendFormat(inv, "\"sharedOrigin\":[{0:F4},{1:F4},{2:F4},{3:F6}],", SharedOriginEast, SharedOriginNorth, SharedOriginElev, SharedAngleRad);

            sb.Append("\"camera\":{");
            sb.AppendFormat(inv, "\"eye\":[{0:F4},{1:F4},{2:F4}],", Camera.EyeX, Camera.EyeY, Camera.EyeZ);
            sb.AppendFormat(inv, "\"target\":[{0:F4},{1:F4},{2:F4}],", Camera.TargetX, Camera.TargetY, Camera.TargetZ);
            sb.AppendFormat(inv, "\"up\":[{0:F4},{1:F4},{2:F4}]", Camera.UpX, Camera.UpY, Camera.UpZ);
            sb.Append("},");

            sb.Append("\"meshes\":[");
            for (int i = 0; i < Meshes.Count; i++)
            {
                if (i > 0) sb.Append(",");
                MeshBatchData m = Meshes[i];
                sb.Append("{");
                sb.AppendFormat(inv, "\"materialId\":\"{0}\",", Escape(m.MaterialId));
                sb.AppendFormat(inv, "\"materialName\":\"{0}\",", Escape(m.MaterialName));
                sb.AppendFormat(inv, "\"color\":[{0},{1},{2}],", m.ColorR, m.ColorG, m.ColorB);
                sb.AppendFormat(inv, "\"transparency\":{0:F3},", m.Transparency);
                sb.AppendFormat(inv, "\"roughness\":{0:F3},", m.Roughness);
                sb.AppendFormat(inv, "\"metalness\":{0:F3},", m.Metalness);

                // Pack positions and normals as Base64 Float32Array and indices as Base64 Int32Array for super fast WebView2 decoding
                float[] posArr = m.Positions.ToArray();
                byte[] posBytes = new byte[posArr.Length * 4];
                Buffer.BlockCopy(posArr, 0, posBytes, 0, posBytes.Length);

                float[] normArr = m.Normals.ToArray();
                byte[] normBytes = new byte[normArr.Length * 4];
                Buffer.BlockCopy(normArr, 0, normBytes, 0, normBytes.Length);

                int[] idxArr = m.Indices.ToArray();
                byte[] idxBytes = new byte[idxArr.Length * 4];
                Buffer.BlockCopy(idxArr, 0, idxBytes, 0, idxBytes.Length);

                sb.AppendFormat("\"positionsB64\":\"{0}\",", Convert.ToBase64String(posBytes));
                sb.AppendFormat("\"normalsB64\":\"{0}\",", Convert.ToBase64String(normBytes));
                sb.AppendFormat("\"indicesB64\":\"{0}\"", Convert.ToBase64String(idxBytes));
                sb.Append("}");
            }
            sb.Append("],");

            sb.Append("\"pointClouds\":[");
            for (int i = 0; i < PointClouds.Count; i++)
            {
                if (i > 0) sb.Append(",");
                PointCloudBatchData pc = PointClouds[i];
                sb.Append("{");
                sb.AppendFormat(inv, "\"id\":\"{0}\",", Escape(pc.Id));
                sb.AppendFormat(inv, "\"name\":\"{0}\",", Escape(pc.Name));
                sb.AppendFormat(inv, "\"count\":{0},", pc.TotalPointsCount);
                string b64 = pc.PackedPointsBase64;
                if (string.IsNullOrEmpty(b64) && pc.PackedPointsBuffer != null)
                {
                    b64 = Convert.ToBase64String(pc.PackedPointsBuffer);
                }
                sb.AppendFormat("\"bufferB64\":\"{0}\"", b64 ?? "");
                sb.Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
        }
    }
}
