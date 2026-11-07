using System;
using System.Collections.Generic;
using DSPRE.Avalonia.Gl;
using DSPRE.ROMFiles;

namespace DSPRE.Avalonia.MapShots
{
    /// <summary>One header's stitched map scene, ready for a screenshot.</summary>
    public sealed class MapShotScene
    {
        public int HeaderId;
        public string Name = "";
        public string InternalName = "";
        public int MatrixId;
        public int MatrixWidth, MatrixHeight;
        public int MapCount;
        public NsbmdRenderModel Scene;

        /// <summary>Whether the area uses HeartGold's interior building set, as the map view decides.</summary>
        public bool Indoor;

        /// <summary>The clock part the map view uses when it picks a building's time-of-day animation.</summary>
        public FieldTimeZone TimeOfDay = FieldTimeOfDay.Now;

        /// <summary>The map cells this header owns, in matrix order.</summary>
        public List<(int CellX, int CellY, int MapIndex)> Cells = new List<(int, int, int)>();

        /// <summary>The union of the cells the header owns, in raw scene space.</summary>
        public float CellMinX, CellMaxX, CellMinZ, CellMaxZ;
        public bool HasCellArea;

        public bool HasContent => Scene != null && Scene.Parts != null && Scene.Parts.Count > 0;

        public float WidthTiles => (CellMaxX - CellMinX) * 4f;
        public float HeightTiles => (CellMaxZ - CellMinZ) * 4f;
    }

    /// <summary>
    /// Builds the same scenes the map editor's header view draws, without any Avalonia or OpenGL state.
    /// </summary>
    public static class MapShotScenes
    {
        /// <summary>Unpacks every archive a scene reads.</summary>
        public static void EnsureArchives()
        {
            MatrixSceneBuilder.EnsureUnpacked();
            List<RomInfo.DirNames> extra = new List<RomInfo.DirNames>();
            Add(extra, RomInfo.DirNames.FieldTextureAnimations);
            Add(extra, RomInfo.DirNames.GroundAnimations);
            Add(extra, RomInfo.DirNames.BuildingAnimations);
            Add(extra, RomInfo.DirNames.DynamicHeaders);
            if (extra.Count > 0) DSUtils.TryUnpackNarcs(extra);
        }

        private static void Add(List<RomInfo.DirNames> list, RomInfo.DirNames dir)
        {
            if (RomInfo.GameDirs != null && RomInfo.GameDirs.ContainsKey(dir)) list.Add(dir);
        }

        /// <summary>Header names as the app lists them, falling back to the ROM's internal names.</summary>
        public static List<string> HeaderNames()
        {
            List<string> names = new List<string>();
            try
            {
                IReadOnlyList<string> friendly = HeaderLabels.Friendly();
                for (int i = 0; i < friendly.Count; i++) names.Add(friendly[i]);
            }
            catch (Exception ex) { AppLogger.WarnOnce("MapShotScenes.HeaderNames", ex); }
            if (names.Count > 0) return names;

            try
            {
                List<string> internalNames = HeaderLists.GetInternalNames();
                for (int i = 0; i < internalNames.Count; i++) names.Add(i.ToString("D3") + " " + internalNames[i].Trim());
            }
            catch (Exception ex) { AppLogger.WarnOnce("MapShotScenes.HeaderNames", ex); }
            return names;
        }

        private static List<string> _internalNames;
        private static int _internalNamesFor = -1;

        /// <summary>The ROM's own short name for a header, such as C03GYM0101, or an empty string.</summary>
        public static string InternalName(int headerId, int headerCount)
        {
            if (_internalNames == null || _internalNamesFor != headerCount)
            {
                try { _internalNames = HeaderLists.GetInternalNames(); _internalNamesFor = headerCount; }
                catch (Exception ex) { AppLogger.WarnOnce("MapShotScenes.InternalName", ex); _internalNames = new List<string>(); }
            }
            return headerId >= 0 && headerId < _internalNames.Count ? _internalNames[headerId].Trim() : "";
        }

        public static int HeaderCount()
        {
            try { return MapHeader.GetHeaderCount(); }
            catch (Exception ex) { AppLogger.Error("Map shot: header count: " + ex.Message); return 0; }
        }

        /// <summary>Builds one header's stitched scene, or null when the header has no maps.</summary>
        /// <param name="onlyCells">Matrix cells to draw, or null for every cell the header owns.</param>
        public static MapShotScene Build(int headerId, IReadOnlyCollection<(int x, int y)> onlyCells = null)
        {
            MapHeader header = MapHeader.GetMapHeader((ushort)headerId);
            if (header == null) return null;
            HashSet<(int x, int y)> cellFilter = onlyCells == null ? null : new HashSet<(int x, int y)>(onlyCells);
            if (headerId == MapHeader.Everywhere) return null;

            GameMatrix matrix = new GameMatrix(header.MatrixID);
            MapShotScene result = new MapShotScene
            {
                HeaderId = headerId,
                MatrixId = header.MatrixID,
                MatrixWidth = matrix.Width,
                MatrixHeight = matrix.Height,
            };
            try
            {
                result.Indoor = RomInfo.GameFamily == RomInfo.GameFamilies.HGSS
                    && new AreaData(header.AreaDataID).AreaType == AreaData.TYPE_INDOOR;
            }
            catch (Exception ex) { AppLogger.WarnOnce("MapShotScenes.Indoor", ex); }

            List<(int cellX, int cellY, MapFile map, byte areaId, float altitudeY)> loaded =
                new List<(int, int, MapFile, byte, float)>();
            for (int y = 0; y < matrix.Height; y++)
                for (int x = 0; x < matrix.Width; x++)
                {
                    int mapIndex = matrix.Maps[y, x];
                    if (mapIndex == GameMatrix.EMPTY) continue;
                    if (matrix.HasHeadersSection && matrix.Headers[y, x] != headerId) continue;
                    if (cellFilter != null && !cellFilter.Contains((x, y))) continue;

                    byte areaId = header.AreaDataID;
                    if (matrix.HasHeadersSection)
                    {
                        try
                        {
                            MapHeader cellHeader = MapHeader.GetMapHeader(matrix.Headers[y, x]);
                            if (cellHeader != null) areaId = cellHeader.AreaDataID;
                        }
                        catch (Exception ex) { AppLogger.WarnOnce("MapShotScenes.Build", ex); }
                    }

                    float altitude = matrix.HasHeightsSection ? matrix.Altitudes[y, x] * (NsbmdGeometry.TileSize / 2f) : 0f;
                    MapFile map = new MapFile(mapIndex, RomInfo.GameFamily, discardMoveperms: false, showMessages: false);
                    loaded.Add((x, y, map, areaId, altitude));
                    result.Cells.Add((x, y, mapIndex));
                }

            if (loaded.Count == 0) return null;
            result.MapCount = loaded.Count;
            result.Scene = MatrixSceneBuilder.BuildFromLoaded(RomInfo.GameFamily, loaded,
                NsbmdGeometry.MatrixStitchMode.Grid);
            if (result.Scene == null) return null;

            foreach ((int cx, int cy, int mapIndex) in result.Cells)
            {
                if (!result.Scene.TryCellPlacement(cx, cy, out NsbmdRenderModel.CellPlacement placement)) continue;
                if (!result.HasCellArea)
                {
                    result.CellMinX = placement.OriginX;
                    result.CellMaxX = placement.OriginX + placement.Width;
                    result.CellMinZ = placement.OriginZ;
                    result.CellMaxZ = placement.OriginZ + placement.Height;
                    result.HasCellArea = true;
                }
                else
                {
                    result.CellMinX = Math.Min(result.CellMinX, placement.OriginX);
                    result.CellMaxX = Math.Max(result.CellMaxX, placement.OriginX + placement.Width);
                    result.CellMinZ = Math.Min(result.CellMinZ, placement.OriginZ);
                    result.CellMaxZ = Math.Max(result.CellMaxZ, placement.OriginZ + placement.Height);
                }
            }

            return result;
        }
    }
}
