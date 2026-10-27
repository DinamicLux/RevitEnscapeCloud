using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;

namespace RevitEnscapeCloud
{
    /// <summary>
    /// High-performance native binary LAS (1.0 - 1.4) and Autodesk ReCap .RCS (ADOCT) point cloud reader.
    /// Reads points directly from disk with live progress percentage callbacks (0% - 100%) and automatic coordinate alignment.
    /// </summary>
    public static class LasFileReader
    {
        public static List<PointCloudBatchData> ReadLasFile(
            string filePath,
            int maxPointsToLoad,
            bool hasRevitCloudRef,
            double refCenterX,
            double refCenterY,
            double refCenterZ,
            bool alignToReferenceIfFar,
            Action<double, string> onProgress = null)
        {
            List<PointCloudBatchData> batches = new List<PointCloudBatchData>();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return batches;

            string shortName = Path.GetFileName(filePath);
            if (onProgress != null)
                onProgress(2.0, "Abriendo cabecera binaria .LAS: " + shortName);

            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
            using (BinaryReader br = new BinaryReader(fs))
            {
                if (fs.Length < 227)
                    throw new InvalidDataException("El archivo es demasiado pequeño para ser un archivo .LAS válido.");

                byte[] sig = br.ReadBytes(5);
                string magic5 = System.Text.Encoding.ASCII.GetString(sig);
                if (magic5.StartsWith("ADOCT"))
                {
                    return ReadRcsStream(fs, br, shortName, maxPointsToLoad, hasRevitCloudRef, refCenterX, refCenterY, refCenterZ, alignToReferenceIfFar, onProgress);
                }
                if (!magic5.StartsWith("LASF"))
                {
                    throw new InvalidDataException("El archivo seleccionado no tiene la firma binaria 'LASF' ni 'ADOCT' (.RCS).");
                }

                fs.Seek(24, SeekOrigin.Begin);
                byte verMajor = br.ReadByte();
                byte verMinor = br.ReadByte();

                fs.Seek(96, SeekOrigin.Begin);
                uint offsetToPointData = br.ReadUInt32();
                uint numVlrs = br.ReadUInt32();
                byte pointFormatByte = br.ReadByte();
                ushort pointRecordLength = br.ReadUInt16();
                uint legacyNumPoints = br.ReadUInt32();

                int pointFormat = pointFormatByte & 0x3F;
                bool isCompressedLaz = (pointFormatByte & 0x80) != 0 || (pointFormatByte & 0x40) != 0;
                if (isCompressedLaz)
                {
                    throw new InvalidDataException("Este archivo está comprimido con LASzip (.LAZ). Por favor expórtalo como .LAS o .RCS.");
                }

                fs.Seek(131, SeekOrigin.Begin);
                double scaleX = br.ReadDouble();
                double scaleY = br.ReadDouble();
                double scaleZ = br.ReadDouble();
                double offsetX = br.ReadDouble();
                double offsetY = br.ReadDouble();
                double offsetZ = br.ReadDouble();

                double maxX = br.ReadDouble();
                double minX = br.ReadDouble();
                double maxY = br.ReadDouble();
                double minY = br.ReadDouble();
                double maxZ = br.ReadDouble();
                double minZ = br.ReadDouble();

                long totalPointsInFile = legacyNumPoints;
                if (verMajor == 1 && verMinor >= 4 && fs.Length >= 255)
                {
                    fs.Seek(247, SeekOrigin.Begin);
                    ulong extNumPoints = br.ReadUInt64();
                    if (extNumPoints > 0)
                        totalPointsInFile = (long)extNumPoints;
                }

                if (totalPointsInFile <= 0 && pointRecordLength > 0 && fs.Length > offsetToPointData)
                {
                    totalPointsInFile = (fs.Length - offsetToPointData) / pointRecordLength;
                }

                if (totalPointsInFile <= 0 || pointRecordLength < 12)
                    return batches;

                int rgbByteOffset = -1;
                if (pointFormat == 2) rgbByteOffset = 20;
                else if (pointFormat == 3 || pointFormat == 5) rgbByteOffset = 28;
                else if (pointFormat == 7 || pointFormat == 8 || pointFormat == 10) rgbByteOffset = 30;

                // Sample first valid point in case header min/max are unpopulated
                fs.Seek(offsetToPointData, SeekOrigin.Begin);
                byte[] firstRec = br.ReadBytes(pointRecordLength);
                double firstWX = BitConverter.ToInt32(firstRec, 0) * scaleX + offsetX;
                double firstWY = BitConverter.ToInt32(firstRec, 4) * scaleY + offsetY;
                double firstWZ = BitConverter.ToInt32(firstRec, 8) * scaleZ + offsetZ;

                bool headerBoxValid = (maxX > minX) && Math.Abs(maxX - minX) < 1e7 && Math.Abs(firstWX - minX) < 1e6;
                double lasCenterThreeX = headerBoxValid ? (minX + maxX) * 0.5 : firstWX;
                double lasCenterThreeY = headerBoxValid ? (minZ + maxZ) * 0.5 : firstWZ;
                double lasCenterThreeZ = headerBoxValid ? -(minY + maxY) * 0.5 : -firstWY;

                double shiftX = (hasRevitCloudRef ? refCenterX : 0.0) - lasCenterThreeX;
                double shiftY = (hasRevitCloudRef ? refCenterY : 0.0) - lasCenterThreeY;
                double shiftZ = (hasRevitCloudRef ? refCenterZ : 0.0) - lasCenterThreeZ;

                bool isRgb16Bit = false;
                if (rgbByteOffset > 0 && rgbByteOffset + 6 <= pointRecordLength)
                {
                    fs.Seek(offsetToPointData, SeekOrigin.Begin);
                    int sampleCheck = (int)Math.Min(200L, totalPointsInFile);
                    byte[] sampleBuf = new byte[pointRecordLength];
                    for (int s = 0; s < sampleCheck; s++)
                    {
                        int read = fs.Read(sampleBuf, 0, pointRecordLength);
                        if (read < pointRecordLength) break;
                        ushort r16 = BitConverter.ToUInt16(sampleBuf, rgbByteOffset);
                        ushort g16 = BitConverter.ToUInt16(sampleBuf, rgbByteOffset + 2);
                        ushort b16 = BitConverter.ToUInt16(sampleBuf, rgbByteOffset + 4);
                        if (r16 > 255 || g16 > 255 || b16 > 255)
                        {
                            isRgb16Bit = true;
                            break;
                        }
                    }
                }

                double rgbDivisor = isRgb16Bit ? 65535.0 : 255.0;
                int targetPts = (int)Math.Max(100000L, Math.Min(totalPointsInFile, (long)maxPointsToLoad));

                const int MaxPointsPerChunkPayload = 550000;
                float[] chunkFloats = new float[MaxPointsPerChunkPayload * 7];
                int currentChunkPoints = 0;
                int chunkIndex = 0;
                uint rngState = 987654321u;

                // Read 160 large windows across 100% of the file and stride uniformly INSIDE each window so dense 2mm scans never look patchy!
                int numSlices = 160;
                int ptsPerSlice = Math.Max(1, (int)Math.Ceiling((double)targetPts / numSlices));
                long totalWindowRecs = Math.Max(1L, totalPointsInFile / numSlices);
                int maxBufferRecs = (int)Math.Min(totalWindowRecs, 180000L);
                byte[] sliceBuf = new byte[Math.Max(1, maxBufferRecs) * pointRecordLength];
                int loadedTotal = 0;

                for (int s = 0; s < numSlices && loadedTotal < targetPts; s++)
                {
                    long sliceStartRecord = (long)((double)s / numSlices * totalPointsInFile);
                    int recsToRead = (int)Math.Min((long)maxBufferRecs, totalPointsInFile - sliceStartRecord);
                    if (recsToRead <= 0) break;

                    long fileOffset = (long)offsetToPointData + sliceStartRecord * (long)pointRecordLength;
                    if (fileOffset + (long)recsToRead * pointRecordLength > fs.Length)
                    {
                        recsToRead = (int)Math.Max(0L, (fs.Length - fileOffset) / pointRecordLength);
                    }
                    if (recsToRead <= 0) break;

                    fs.Seek(fileOffset, SeekOrigin.Begin);
                    int bytesNeeded = recsToRead * pointRecordLength;
                    int bytesRead = 0;
                    while (bytesRead < bytesNeeded)
                    {
                        int n = fs.Read(sliceBuf, bytesRead, bytesNeeded - bytesRead);
                        if (n <= 0) break;
                        bytesRead += n;
                    }

                    int actualRecs = bytesRead / pointRecordLength;
                    if (actualRecs <= 0) continue;

                    double innerStride = Math.Max(1.0, (double)actualRecs / ptsPerSlice);
                    for (double cursor = 0.0; (int)cursor < actualRecs && loadedTotal < targetPts; cursor += innerStride)
                    {
                        int i = (int)cursor;
                        int recOffset = i * pointRecordLength;
                        int xi = BitConverter.ToInt32(sliceBuf, recOffset + 0);
                        int yi = BitConverter.ToInt32(sliceBuf, recOffset + 4);
                        int zi = BitConverter.ToInt32(sliceBuf, recOffset + 8);
                        ushort intensity = BitConverter.ToUInt16(sliceBuf, recOffset + 12);

                        double worldX = xi * scaleX + offsetX;
                        double worldY = yi * scaleY + offsetY;
                        double worldZ = zi * scaleZ + offsetZ;

                        float x = (float)(worldX + shiftX);
                        float y = (float)(worldZ + shiftY);
                        float z = (float)(-worldY + shiftZ);

                        float r = 0f, g = 0f, b = 0f;
                        if (rgbByteOffset > 0 && rgbByteOffset + 6 <= pointRecordLength)
                        {
                            r = (float)(BitConverter.ToUInt16(sliceBuf, recOffset + rgbByteOffset) / rgbDivisor);
                            g = (float)(BitConverter.ToUInt16(sliceBuf, recOffset + rgbByteOffset + 2) / rgbDivisor);
                            b = (float)(BitConverter.ToUInt16(sliceBuf, recOffset + rgbByteOffset + 4) / rgbDivisor);
                        }

                        if (r == 0f && g == 0f && b == 0f)
                        {
                            if (intensity > 0)
                            {
                                float normI = (float)Math.Min(1.0, intensity > 255 ? (intensity / 4096.0) : (intensity / 255.0));
                                normI = Math.Max(0.22f, normI);
                                r = normI * 0.90f;
                                g = normI * 0.95f;
                                b = Math.Min(1.0f, normI * 1.05f);
                            }
                            else
                            {
                                r = 0.35f; g = 0.78f; b = 0.95f;
                            }
                        }

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
                        loadedTotal++;

                        if (currentChunkPoints >= MaxPointsPerChunkPayload)
                        {
                            batches.Add(FlushLasChunk(shortName, chunkIndex++, chunkFloats, currentChunkPoints));
                            currentChunkPoints = 0;
                        }
                    }

                    if (onProgress != null && (s % 4 == 0 || s == numSlices - 1))
                    {
                        double pct = 5.0 + 88.0 * ((double)(s + 1) / numSlices);
                        onProgress(pct, string.Format("Leyendo .LAS ({0}): {1:N0} / {2:N0} puntos...", shortName, loadedTotal, targetPts));
                    }
                }

                if (currentChunkPoints > 0)
                {
                    batches.Add(FlushLasChunk(shortName, chunkIndex++, chunkFloats, currentChunkPoints));
                }
            }

            if (onProgress != null)
                onProgress(96.0, "Transfiriendo nube .LAS a GPU WebGL2...");

            return batches;
        }

        public static List<PointCloudBatchData> ReadRcsFile(
            string filePath,
            int maxPointsToLoad,
            bool hasRevitCloudRef,
            double refCenterX,
            double refCenterY,
            double refCenterZ,
            bool alignToReferenceIfFar,
            Action<double, string> onProgress = null)
        {
            List<PointCloudBatchData> batches = new List<PointCloudBatchData>();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return batches;

            string shortName = Path.GetFileName(filePath);
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
            using (BinaryReader br = new BinaryReader(fs))
            {
                return ReadRcsStream(fs, br, shortName, maxPointsToLoad, hasRevitCloudRef, refCenterX, refCenterY, refCenterZ, alignToReferenceIfFar, onProgress);
            }
        }

        private static List<PointCloudBatchData> ReadRcsStream(
            FileStream fs,
            BinaryReader br,
            string shortName,
            int maxPointsToLoad,
            bool hasRevitCloudRef,
            double refCenterX,
            double refCenterY,
            double refCenterZ,
            bool alignToReferenceIfFar,
            Action<double, string> onProgress)
        {
            List<PointCloudBatchData> batches = new List<PointCloudBatchData>();
            if (fs.Length < 512)
                throw new InvalidDataException("El archivo .RCS es demasiado pequeño.");

            if (onProgress != null)
                onProgress(3.0, "Analizando estructura Octree Autodesk ReCap (.RCS): " + shortName);

            fs.Seek(0, SeekOrigin.Begin);
            byte[] hdr = br.ReadBytes(512);
            string sig = System.Text.Encoding.ASCII.GetString(hdr, 0, 5);
            if (sig != "ADOCT")
                throw new InvalidDataException("El archivo .RCS no tiene cabecera ADOCT de Autodesk ReCap.");

            double hdrMinX = BitConverter.ToDouble(hdr, 0xA0);
            double hdrMinY = BitConverter.ToDouble(hdr, 0xA8);
            double hdrMinZ = BitConverter.ToDouble(hdr, 0xB0);
            double hdrMaxX = BitConverter.ToDouble(hdr, 0xB8);
            double hdrMaxY = BitConverter.ToDouble(hdr, 0xC0);
            double hdrMaxZ = BitConverter.ToDouble(hdr, 0xC8);

            uint numSec = BitConverter.ToUInt32(hdr, 0x129);
            long sec2Off = 0, sec2Size = 0, sec1Off = 0, sec1Size = 0;
            for (int i = 0; i < numSec && (0x12D + i * 24 + 24) <= hdr.Length; i++)
            {
                int pos = 0x12D + i * 24;
                ulong t = BitConverter.ToUInt64(hdr, pos);
                long o = (long)BitConverter.ToUInt64(hdr, pos + 8);
                long s = (long)BitConverter.ToUInt64(hdr, pos + 16);
                if (t == 2) { sec2Off = o; sec2Size = s; }
                else if (t == 1) { sec1Off = o; sec1Size = s; }
            }

            if (sec2Off <= 0 || sec1Off <= 0)
                throw new InvalidDataException("No se encontraron las tablas de nodos Octree en el archivo .RCS.");

            fs.Seek(sec2Off, SeekOrigin.Begin);
            int leafCount = (int)br.ReadUInt64();
            if (leafCount <= 0) return batches;

            double[] cMinX = new double[leafCount];
            double[] cMinY = new double[leafCount];
            double[] cMinZ = new double[leafCount];
            double[] tMinX = new double[leafCount];
            double[] tMinY = new double[leafCount];
            double[] tMinZ = new double[leafCount];
            double[] tMaxX = new double[leafCount];
            double[] tMaxY = new double[leafCount];
            double[] tMaxZ = new double[leafCount];
            int[] ptCounts = new int[leafCount];
            long[] ptOffsets = new long[leafCount];

            long streamCursor = sec1Off;
            long totalPointsInRcs = 0;

            for (int n = 0; n < leafCount; n++)
            {
                byte[] nd = br.ReadBytes(368);
                if (nd.Length < 368) break;
                cMinX[n] = BitConverter.ToDouble(nd, 0);
                cMinY[n] = BitConverter.ToDouble(nd, 8);
                cMinZ[n] = BitConverter.ToDouble(nd, 16);
                tMinX[n] = BitConverter.ToDouble(nd, 48);
                tMinY[n] = BitConverter.ToDouble(nd, 56);
                tMinZ[n] = BitConverter.ToDouble(nd, 64);
                tMaxX[n] = BitConverter.ToDouble(nd, 72);
                tMaxY[n] = BitConverter.ToDouble(nd, 80);
                tMaxZ[n] = BitConverter.ToDouble(nd, 88);

                int pCount = (int)BitConverter.ToUInt32(nd, 100);
                uint bSize = BitConverter.ToUInt32(nd, 104);
                ptCounts[n] = pCount;
                ptOffsets[n] = streamCursor + (long)bSize - ((long)pCount * 16L);
                streamCursor += bSize;
                totalPointsInRcs += pCount;
            }

            if (totalPointsInRcs <= 0) return batches;

            double centerThreeX = (hdrMinX + hdrMaxX) * 0.5;
            double centerThreeY = (hdrMinZ + hdrMaxZ) * 0.5;
            double centerThreeZ = -(hdrMinY + hdrMaxY) * 0.5;

            double shiftX = (hasRevitCloudRef ? refCenterX : 0.0) - centerThreeX;
            double shiftY = (hasRevitCloudRef ? refCenterY : 0.0) - centerThreeY;
            double shiftZ = (hasRevitCloudRef ? refCenterZ : 0.0) - centerThreeZ;

            int targetPts = (int)Math.Max(100000L, Math.Min(totalPointsInRcs, (long)maxPointsToLoad));
            double stride = Math.Max(1.0, (double)totalPointsInRcs / targetPts);

            const int MaxPointsPerChunkPayload = 550000;
            float[] chunkFloats = new float[MaxPointsPerChunkPayload * 7];
            int currentChunkPoints = 0;
            int chunkIndex = 0;
            uint rngState = 123456789u;
            int loadedTotal = 0;

            for (int n = 0; n < leafCount && loadedTotal < targetPts; n++)
            {
                int pCount = ptCounts[n];
                if (pCount <= 0 || ptOffsets[n] < 0 || ptOffsets[n] + (long)pCount * 16L > fs.Length)
                    continue;

                fs.Seek(ptOffsets[n], SeekOrigin.Begin);
                byte[] nodeBytes = br.ReadBytes(pCount * 16);
                if (nodeBytes.Length < pCount * 16) continue;

                // Determine exact quantization bounds in this leaf node
                int minQx = int.MaxValue, maxQx = -1;
                int minQy = int.MaxValue, maxQy = -1;
                int minQz = int.MaxValue, maxQz = -1;
                for (int p = 0; p < pCount; p++)
                {
                    ulong u64 = BitConverter.ToUInt64(nodeBytes, p * 16);
                    int qx = (int)(u64 & 0x3FFFFUL);
                    int qy = (int)((u64 >> 18) & 0x3FFFFUL);
                    int qz = (int)((u64 >> 36) & 0x3FFFFUL);
                    if (qx < minQx) minQx = qx; if (qx > maxQx) maxQx = qx;
                    if (qy < minQy) minQy = qy; if (qy > maxQy) maxQy = qy;
                    if (qz < minQz) minQz = qz; if (qz > maxQz) maxQz = qz;
                }

                double scaleX = (maxQx > minQx && tMaxX[n] > tMinX[n]) ? (tMaxX[n] - tMinX[n]) / (maxQx - minQx) : 0.001;
                double scaleY = (maxQy > minQy && tMaxY[n] > tMinY[n]) ? (tMaxY[n] - tMinY[n]) / (maxQy - minQy) : 0.001;
                double scaleZ = (maxQz > minQz && tMaxZ[n] > tMinZ[n]) ? (tMaxZ[n] - tMinZ[n]) / (maxQz - minQz) : 0.001;

                for (double pCursor = 0; (int)pCursor < pCount && loadedTotal < targetPts; pCursor += stride)
                {
                    int p = (int)pCursor;
                    int bOff = p * 16;
                    ulong u64 = BitConverter.ToUInt64(nodeBytes, bOff);
                    int qx = (int)(u64 & 0x3FFFFUL);
                    int qy = (int)((u64 >> 18) & 0x3FFFFUL);
                    int qz = (int)((u64 >> 36) & 0x3FFFFUL);

                    double wx = (maxQx > minQx) ? (tMinX[n] + (qx - minQx) * scaleX) : (cMinX[n] + qx * 0.001);
                    double wy = (maxQy > minQy) ? (tMinY[n] + (qy - minQy) * scaleY) : (cMinY[n] + qy * 0.001);
                    double wz = (maxQz > minQz) ? (tMinZ[n] + (qz - minQz) * scaleZ) : (cMinZ[n] + qz * 0.001);

                    float x = (float)(wx + shiftX);
                    float y = (float)(wz + shiftY);
                    float z = (float)(-wy + shiftZ);

                    byte intensity = nodeBytes[bOff + 8];
                    byte bByte = nodeBytes[bOff + 9];
                    byte gByte = nodeBytes[bOff + 10];
                    byte rByte = nodeBytes[bOff + 11];

                    float r = rByte / 255.0f;
                    float g = gByte / 255.0f;
                    float b = bByte / 255.0f;

                    if (r == 0f && g == 0f && b == 0f)
                    {
                        float normI = Math.Max(0.25f, intensity / 255.0f);
                        r = normI; g = normI; b = normI;
                    }

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
                    loadedTotal++;

                    if (currentChunkPoints >= MaxPointsPerChunkPayload)
                    {
                        batches.Add(FlushLasChunk(shortName, chunkIndex++, chunkFloats, currentChunkPoints));
                        currentChunkPoints = 0;
                    }
                }

                if (onProgress != null && (n % Math.Max(1, leafCount / 25) == 0 || n == leafCount - 1))
                {
                    double pct = 6.0 + 88.0 * ((double)(n + 1) / leafCount);
                    onProgress(pct, string.Format("Decodificando Octree ReCap .RCS ({0}): nodo {1}/{2} ({3:N0} pts)...", shortName, n + 1, leafCount, loadedTotal));
                }
            }

            if (currentChunkPoints > 0)
            {
                batches.Add(FlushLasChunk(shortName, chunkIndex++, chunkFloats, currentChunkPoints));
            }

            if (onProgress != null)
                onProgress(96.0, "Subiendo nube ReCap .RCS a GPU WebGL2...");

            return batches;
        }

        private static PointCloudBatchData FlushLasChunk(string fileName, int chunkIdx, float[] buffer, int pointCount)
        {
            int byteLen = pointCount * 7 * sizeof(float);
            byte[] byteBuffer = new byte[byteLen];
            Buffer.BlockCopy(buffer, 0, byteBuffer, 0, byteLen);

            return new PointCloudBatchData
            {
                Id = "LAS_" + fileName + "_" + chunkIdx,
                Name = fileName + " [Tile " + (chunkIdx + 1) + "]",
                SourceTag = "DIRECT",
                TotalPointsCount = pointCount,
                PackedPointsBuffer = byteBuffer,
                PackedPointsBase64 = ""
            };
        }
    }
}
