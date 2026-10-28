using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace RevitEnscapeCloud
{
    public class SmMeshTile
    {
        public int NodeId;
        public int Resolution;
        public int VertexCount;
        public int TriangleCount;
        public byte[] PackedGeometry; // [X:float, Y:float, Z:float, U:float, V:float] * VertexCount (20 bytes/vertex)
        public byte[] JpegTexture;
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
    }

    public class RealityMeshModel
    {
        public string Name;
        public string Gcs;
        public List<SmMeshTile> Tiles = new List<SmMeshTile>();
    }

    public static class SmFileReader
    {
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open16", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open16([MarshalAs(UnmanagedType.LPWStr)] string filename, out IntPtr ppDb);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr db);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare16_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare16_v2(IntPtr db, [MarshalAs(UnmanagedType.LPWStr)] string zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr pStmt);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr pStmt);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr pStmt, int iCol);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr pStmt, int iCol);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_blob", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_blob(IntPtr pStmt, int iCol);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text16", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text16(IntPtr pStmt, int iCol);

        private static byte[] DecompressZlib(byte[] data)
        {
            if (data == null || data.Length < 2) return new byte[0];
            try
            {
                using (MemoryStream ms = new MemoryStream(data, 2, data.Length - 2))
                using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (MemoryStream outMs = new MemoryStream())
                {
                    ds.CopyTo(outMs);
                    return outMs.ToArray();
                }
            }
            catch
            {
                return new byte[0];
            }
        }

        public static RealityMeshModel Read3smFile(string filePath, Action<double, string> progressCallback)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("No se encontró el archivo .3sm en la ruta especificada.", filePath);
            }

            RealityMeshModel model = new RealityMeshModel
            {
                Name = Path.GetFileName(filePath),
                Gcs = ""
            };

            IntPtr db;
            int rc = sqlite3_open16(filePath, out db);
            if (rc != 0)
            {
                throw new InvalidOperationException("No se pudo abrir el contenedor SQLite del archivo .3sm (código " + rc + ").");
            }

            try
            {
                if (progressCallback != null)
                    progressCallback(5.0, "Consultando metadatos y sistema de coordenadas (SMMasterHeader)...");

                // 1. Read SMMasterHeader
                IntPtr stmtHeader;
                if (sqlite3_prepare16_v2(db, "SELECT GCS FROM SMMasterHeader LIMIT 1;", -1, out stmtHeader, IntPtr.Zero) == 0)
                {
                    if (sqlite3_step(stmtHeader) == 100) // SQLITE_ROW
                    {
                        IntPtr textPtr = sqlite3_column_text16(stmtHeader, 0);
                        if (textPtr != IntPtr.Zero)
                            model.Gcs = Marshal.PtrToStringUni(textPtr);
                    }
                    sqlite3_finalize(stmtHeader);
                }

                // 2. Determine target resolution level (Prefer Resolution 1 quadrants for high-definition, fallback to 0)
                int targetResolution = 1;
                IntPtr stmtCount;
                if (sqlite3_prepare16_v2(db, "SELECT COUNT(*) FROM SMNodeHeader WHERE Resolution = 1;", -1, out stmtCount, IntPtr.Zero) == 0)
                {
                    if (sqlite3_step(stmtCount) == 100)
                    {
                        long countRes1 = sqlite3_column_int64(stmtCount, 0);
                        if (countRes1 == 0) targetResolution = 0;
                    }
                    sqlite3_finalize(stmtCount);
                }

                // 3. Query Nodes at target resolution
                string querySql =
                    "SELECT h.NodeId, h.Resolution, h.Extent, p.PointData, p.IndexData, u.UVData, u.UVIndexData, " +
                    "       COALESCE((SELECT t1.TexData FROM SMTexture t1 WHERE t1.NodeId = h.TexID), " +
                    "                (SELECT t2.TexData FROM SMTexture t2 WHERE t2.NodeId = h.NodeId)) AS TexData " +
                    "FROM SMNodeHeader h " +
                    "JOIN SMPoint p ON p.NodeId = h.NodeId " +
                    "LEFT JOIN SMUVs u ON u.NodeId = h.NodeId " +
                    "WHERE h.Resolution = " + targetResolution + " ORDER BY h.NodeId;";

                IntPtr stmtNodes;
                if (sqlite3_prepare16_v2(db, querySql, -1, out stmtNodes, IntPtr.Zero) != 0)
                {
                    throw new InvalidOperationException("Error al consultar las teselas de malla en SMNodeHeader.");
                }

                List<int> nodeIds = new List<int>();
                int currentTile = 0;
                double basePct = 10.0;

                while (sqlite3_step(stmtNodes) == 100)
                {
                    currentTile++;
                    int nodeId = (int)sqlite3_column_int64(stmtNodes, 0);
                    int res = (int)sqlite3_column_int64(stmtNodes, 1);

                    if (progressCallback != null)
                    {
                        double pct = Math.Min(90.0, basePct + currentTile * 18.0);
                        progressCallback(pct, string.Format("Extrayendo tesela {0} (Nodo {1}, Res {2})...", currentTile, nodeId, res));
                    }

                    // Extent (6 doubles: minX, minY, minZ, maxX, maxY, maxZ)
                    double minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
                    int extLen = sqlite3_column_bytes(stmtNodes, 2);
                    if (extLen >= 48)
                    {
                        byte[] extBytes = new byte[48];
                        Marshal.Copy(sqlite3_column_blob(stmtNodes, 2), extBytes, 0, 48);
                        minX = BitConverter.ToDouble(extBytes, 0);
                        minY = BitConverter.ToDouble(extBytes, 8);
                        minZ = BitConverter.ToDouble(extBytes, 16);
                        maxX = BitConverter.ToDouble(extBytes, 24);
                        maxY = BitConverter.ToDouble(extBytes, 32);
                        maxZ = BitConverter.ToDouble(extBytes, 40);
                    }

                    // PointData
                    int pLen = sqlite3_column_bytes(stmtNodes, 3);
                    byte[] pData = new byte[pLen];
                    Marshal.Copy(sqlite3_column_blob(stmtNodes, 3), pData, 0, pLen);
                    byte[] rawPts = DecompressZlib(pData);

                    // IndexData
                    int iLen = sqlite3_column_bytes(stmtNodes, 4);
                    byte[] iData = new byte[iLen];
                    Marshal.Copy(sqlite3_column_blob(stmtNodes, 4), iData, 0, iLen);
                    byte[] rawIdx = DecompressZlib(iData);

                    // UVData
                    int uLen = sqlite3_column_bytes(stmtNodes, 5);
                    byte[] uData = new byte[uLen];
                    if (uLen > 0)
                        Marshal.Copy(sqlite3_column_blob(stmtNodes, 5), uData, 0, uLen);
                    byte[] rawUvs = (uLen > 0) ? DecompressZlib(uData) : new byte[0];

                    // UVIndexData
                    int uiLen = sqlite3_column_bytes(stmtNodes, 6);
                    byte[] uiData = new byte[uiLen];
                    if (uiLen > 0)
                        Marshal.Copy(sqlite3_column_blob(stmtNodes, 6), uiData, 0, uiLen);
                    byte[] rawUvIdx = (uiLen > 0) ? DecompressZlib(uiData) : new byte[0];

                    // TexData
                    int tLen = sqlite3_column_bytes(stmtNodes, 7);
                    byte[] tData = new byte[tLen];
                    if (tLen > 0)
                        Marshal.Copy(sqlite3_column_blob(stmtNodes, 7), tData, 0, tLen);

                    // Search for JPEG header inside TexData
                    byte[] jpg = null;
                    for (int k = 0; k < tData.Length - 4; k++)
                    {
                        if (tData[k] == 0xFF && tData[k + 1] == 0xD8 && tData[k + 2] == 0xFF)
                        {
                            jpg = new byte[tData.Length - k];
                            Array.Copy(tData, k, jpg, 0, jpg.Length);
                            break;
                        }
                    }

                    int totalPts = rawPts.Length / 24;
                    int totalFaceVertices = rawIdx.Length / 4;
                    int totalUvs = rawUvs.Length / 16;

                    if (totalFaceVertices == 0 || totalPts == 0) continue;

                    // Pack into continuous Float32: [X, Y, Z, U, V] = 5 floats = 20 bytes/vertex
                    byte[] packed = new byte[totalFaceVertices * 20];
                    int packOffset = 0;

                    for (int f = 0; f < totalFaceVertices; f++)
                    {
                        uint rawPtIdx = BitConverter.ToUInt32(rawIdx, f * 4);
                        // Bentley 3sm indices are 1-based
                        int pI = (int)rawPtIdx - 1;
                        if (pI < 0) pI = 0;
                        if (pI >= totalPts) pI = totalPts - 1;

                        double px = BitConverter.ToDouble(rawPts, pI * 24);
                        double py = BitConverter.ToDouble(rawPts, pI * 24 + 8);
                        double pz = BitConverter.ToDouble(rawPts, pI * 24 + 16);

                        uint rawUvI = (rawUvIdx.Length >= (f + 1) * 4) ? BitConverter.ToUInt32(rawUvIdx, f * 4) : 1;
                        int uI = (int)rawUvI - 1;
                        if (uI < 0) uI = 0;
                        if (uI >= totalUvs) uI = (totalUvs > 0) ? (totalUvs - 1) : 0;

                        double u = (totalUvs > 0) ? BitConverter.ToDouble(rawUvs, uI * 16) : 0.0;
                        double v = (totalUvs > 0) ? BitConverter.ToDouble(rawUvs, uI * 16 + 8) : 0.0;

                        byte[] bx = BitConverter.GetBytes((float)px);
                        byte[] by = BitConverter.GetBytes((float)py);
                        byte[] bz = BitConverter.GetBytes((float)pz);
                        byte[] bu = BitConverter.GetBytes((float)u);
                        byte[] bv = BitConverter.GetBytes((float)(1.0 - v)); // Flip V for WebGL

                        Buffer.BlockCopy(bx, 0, packed, packOffset, 4);
                        Buffer.BlockCopy(by, 0, packed, packOffset + 4, 4);
                        Buffer.BlockCopy(bz, 0, packed, packOffset + 8, 4);
                        Buffer.BlockCopy(bu, 0, packed, packOffset + 12, 4);
                        Buffer.BlockCopy(bv, 0, packed, packOffset + 16, 4);
                        packOffset += 20;
                    }

                    SmMeshTile tile = new SmMeshTile
                    {
                        NodeId = nodeId,
                        Resolution = res,
                        VertexCount = totalFaceVertices,
                        TriangleCount = totalFaceVertices / 3,
                        PackedGeometry = packed,
                        JpegTexture = jpg,
                        MinX = minX,
                        MinY = minY,
                        MinZ = minZ,
                        MaxX = maxX,
                        MaxY = maxY,
                        MaxZ = maxZ
                    };
                    model.Tiles.Add(tile);
                }

                sqlite3_finalize(stmtNodes);
            }
            finally
            {
                sqlite3_close(db);
            }

            return model;
        }
    }
}
