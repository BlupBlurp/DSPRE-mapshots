using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using DSPRE.Avalonia.Gl;

namespace DSPRE.Avalonia.MapShots
{
    /// <summary>One header's screenshot settings. The defaults block uses the same shape as each map entry.</summary>
    public sealed class MapShotSettings
    {
        public int? Id { get; set; }
        public string Name { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public float? PixelsPerTile { get; set; }
        public float? Pitch { get; set; }
        public float? Yaw { get; set; }
        public float? OffsetXTiles { get; set; }
        public float? OffsetZTiles { get; set; }

        /// <summary>"cell" frames the header's whole matrix cells; "content" frames only its geometry.</summary>
        public string Frame { get; set; }

        /// <summary>"transparent" (default) or a #RRGGBB colour.</summary>
        public string Background { get; set; }
        public bool? Enabled { get; set; }
        public string File { get; set; }

        /// <summary>Other headers to render and stack under this one, for a multi-room header image.</summary>
        public int[] Stack { get; set; }
        public int? Gap { get; set; }

        /// <summary>Extra material names to leave out, on top of the void layer every frame hides.</summary>
        public string[] HideMaterials { get; set; }

        /// <summary>Draw the baked shadow and prop overlay. On by default, as the game and rips show it.</summary>
        public bool? Shadows { get; set; }

        /// <summary>Matrix cells to draw, as "x,y" pairs. Null means every cell the header owns.</summary>
        public string[] Cells { get; set; }

        /// <summary>Apply vertex colours where the model marks shading off. Some maps bake shadows that way.</summary>
        public bool? Shade { get; set; }

        /// <summary>Apply directional face lighting to opaque textured geometry. On by default.</summary>
        public bool? Lighting { get; set; }
    }

    public sealed class MapShotConfig
    {
        public MapShotSettings Defaults { get; set; } = new MapShotSettings();
        public List<MapShotSettings> Maps { get; set; } = new List<MapShotSettings>();
    }

    /// <summary>
    /// Command line batch renderer for map screenshots:
    /// <c>DSPRE.Avalonia --map-shots --rom &lt;project folder&gt; [--out dir] [--maps ids]</c>.
    /// Runs before Avalonia starts, so it needs no window, GL context or interaction.
    /// </summary>
    public static class MapShotBatch
    {
        public const string ModeSwitch = "--map-shots";

        private sealed class Options
        {
            public string Rom;
            public string Game;
            public string Out;
            public string Config;
            public string Maps;
            public string Filter;
            public bool List;
            public bool Info;
            public bool Materials;
            public bool FlipCulling;
            public string Hide;
            public bool NoShade;
            public bool ForceShade;
            public bool? Lighting;
            public float? LightX, LightY, LightZ;
            public float? LightAmbient, LightDiffuse;
            public bool? ShowShadows;
            public string Cells;
            public bool NoTexMatrix;
            public bool NoTextures;
            public int PickX = -1, PickY = -1;
            public bool Force;
            public float? Scale;
            public int? Width;
            public int? Height;
            public float? Pitch;
        }

        public static bool WantsBatch(string[] args)
        {
            foreach (string a in args)
                if (string.Equals(a, ModeSwitch, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static int Run(string[] args)
        {
            Options options;
            try { options = Parse(args); }
            catch (Exception ex) { Console.Error.WriteLine("map shots: " + ex.Message); return Usage(); }

            if (options.List) return ListMaps(options);

            string rom;
            try { rom = ResolveRom(options.Rom); }
            catch (Exception ex) { Console.Error.WriteLine("map shots: " + ex.Message); return 2; }

            string game = string.IsNullOrWhiteSpace(options.Game) ? ResolveGameCode(rom) : options.Game.Trim();
            if (string.IsNullOrWhiteSpace(game))
            {
                Console.Error.WriteLine("map shots: couldn't tell which game this project is. Pass --game CPUE (or CPEU, CPUP, CPJP).");
                return 2;
            }

            try
            {
                new RomInfo(game, rom);
                if (RomInfo.GameFamily == RomInfo.GameFamilies.NULL)
                {
                    Console.Error.WriteLine($"map shots: '{game}' isn't a supported game code.");
                    return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("map shots: opening the project failed: " + ex.Message);
                return 2;
            }

            Console.WriteLine($"Opening {RomInfo.GameVersion} project at {rom}");
            try { MapShotScenes.EnsureArchives(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("map shots: unpacking map archives failed: " + ex.Message);
                return 2;
            }

            MapShotConfig config;
            try { config = LoadConfig(options.Config); }
            catch (Exception ex) { Console.Error.WriteLine("map shots: reading the config failed: " + ex.Message); return 2; }

            string outDir = options.Out;
            if (string.IsNullOrWhiteSpace(outDir)) outDir = Path.Combine(Directory.GetCurrentDirectory(), "mapshots");
            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex) { Console.Error.WriteLine("map shots: couldn't create the output folder: " + ex.Message); return 2; }

            HashSet<int> wanted = ParseIds(options.Maps);
            List<string> names = MapShotScenes.HeaderNames();
            int headerCount = MapShotScenes.HeaderCount();
            if (headerCount <= 1)
            {
                Console.Error.WriteLine("map shots: this project has no map headers.");
                return 2;
            }

            int total = 0, written = 0, skipped = 0, failed = 0, empty = 0;
            for (int id = 1; id < headerCount; id++)
            {
                if (wanted.Count > 0 && !wanted.Contains(id)) continue;
                string name = id < names.Count ? names[id] : id.ToString("D3");
                if (!string.IsNullOrWhiteSpace(options.Filter)
                    && name.IndexOf(options.Filter, StringComparison.OrdinalIgnoreCase) < 0
                    && !id.ToString("D3").Equals(options.Filter, StringComparison.OrdinalIgnoreCase)) continue;

                MapShotSettings settings = MergeSettings(config.Defaults, FindSettings(config, id, name));
                if (settings?.Enabled == false) continue;
                total++;

                try
                {
                    if (options.Info)
                    {
                        PrintInfo(id, name, settings, options);
                        continue;
                    }

                    string file = string.IsNullOrWhiteSpace(settings?.File)
                        ? SafeFileName(string.IsNullOrWhiteSpace(name) ? id.ToString("D3") : name) + ".png"
                        : settings.File;
                    string path = Path.Combine(outDir, file);
                    if (File.Exists(path) && new FileInfo(path).Length > 0 && !options.Force)
                    {
                        skipped++;
                        continue;
                    }

                    if (settings?.Stack != null && settings.Stack.Length > 0)
                    {
                        RawImage stacked = RenderStack(id, settings, options, out string stackNote);
                        if (stacked == null) { empty++; Console.WriteLine($"  {id:D3} {name}: {stackNote}"); continue; }
                        Save(stacked, path);
                    }
                    else
                    {
                        MapShotScene scene = MapShotScenes.Build(id, CellsOf(settings, options));
                        if (scene == null || !scene.HasContent)
                        {
                            empty++;
                            Console.WriteLine($"  {id:D3} {name}: no renderable map");
                            continue;
                        }

                        MapShotCamera camera = MakeCamera(scene, settings, options);
                        RawImage image = MapShotRenderer.Render(scene.Scene, camera, out MapShotStats stats);
                        Save(image, path);
                        Console.WriteLine($"  {id:D3} {name}: {camera.Width}x{camera.Height}, {stats.DrawnTriangles} triangles");
                        Pick(scene.Scene, stats, options);
                    }
                    written++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.Error.WriteLine($"  {id:D3} {name}: FAILED: {ex.Message}");
                }
            }

            if (options.Info) return 0;
            Console.WriteLine($"Done. {written} written, {skipped} up to date, {empty} without maps, {failed} failed ({total} headers checked).");
            Console.WriteLine("Output: " + outDir);
            return failed > 0 ? 1 : 0;
        }

        private static Options Parse(string[] args)
        {
            Options o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"missing value for {a}");
                switch (a)
                {
                    case ModeSwitch: break;
                    case "--rom": o.Rom = Next(); break;
                    case "--game": o.Game = Next(); break;
                    case "--out": o.Out = Next(); break;
                    case "--config": o.Config = Next(); break;
                    case "--maps": o.Maps = Next(); break;
                    case "--filter": o.Filter = Next(); break;
                    case "--list": o.List = true; break;
                    case "--info": o.Info = true; break;
                    case "--materials": o.Materials = true; break;
                    case "--flip-cull": o.FlipCulling = true; break;
                    case "--hide": o.Hide = Next(); break;
                    case "--no-shade": o.NoShade = true; break;
                    case "--shade": o.ForceShade = true; break;
                    case "--light": o.Lighting = true; break;
                    case "--no-light": o.Lighting = false; break;
                    case "--light-dir":
                        {
                            string[] parts = Next().Split(',');
                            if (parts.Length != 3) throw new ArgumentException("--light-dir needs x,y,z");
                            o.LightX = float.Parse(parts[0], CultureInfo.InvariantCulture);
                            o.LightY = float.Parse(parts[1], CultureInfo.InvariantCulture);
                            o.LightZ = float.Parse(parts[2], CultureInfo.InvariantCulture);
                            break;
                        }
                    case "--light-ambient": o.LightAmbient = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--light-diffuse": o.LightDiffuse = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--shadows": o.ShowShadows = true; break;
                    case "--no-shadows": o.ShowShadows = false; break;
                    case "--cells": o.Cells = Next(); break;
                    case "--no-tex-matrix": o.NoTexMatrix = true; break;
                    case "--no-textures": o.NoTextures = true; break;
                    case "--pick":
                        {
                            string[] parts = Next().Split(',');
                            if (parts.Length == 2)
                            {
                                o.PickX = int.Parse(parts[0], CultureInfo.InvariantCulture);
                                o.PickY = int.Parse(parts[1], CultureInfo.InvariantCulture);
                            }
                            break;
                        }
                    case "--force": o.Force = true; break;
                    case "--scale": o.Scale = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--width": o.Width = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--height": o.Height = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--pitch": o.Pitch = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                    default: throw new ArgumentException("unknown option " + a);
                }
            }
            if (string.IsNullOrWhiteSpace(o.Config))
            {
                string beside = Path.Combine(Directory.GetCurrentDirectory(), "mapshots.json");
                if (File.Exists(beside)) o.Config = beside;
            }
            return o;
        }

        private static int Usage()
        {
            Console.Error.WriteLine("Usage: DSPRE.Avalonia --map-shots [--rom <project folder>] [--game CPUE] [--out <dir>] [--config <file>]");
            Console.Error.WriteLine("          [--maps 5,35] [--filter name] [--list] [--info] [--force] [--scale 16] [--width 512] [--height 512] [--pitch 50]");
            Console.Error.WriteLine("          [--light] [--no-light] [--light-dir x,y,z] [--light-ambient n] [--light-diffuse n]");
            return 2;
        }

        private static int ListMaps(Options options)
        {
            string rom;
            try { rom = ResolveRom(options.Rom); }
            catch (Exception ex) { Console.Error.WriteLine("map shots: " + ex.Message); return 2; }
            string game = string.IsNullOrWhiteSpace(options.Game) ? ResolveGameCode(rom) : options.Game.Trim();
            if (string.IsNullOrWhiteSpace(game))
            {
                Console.Error.WriteLine("map shots: couldn't tell which game this project is. Pass --game CPUE (or CPEU, CPUP, CPJP).");
                return 2;
            }
            try { new RomInfo(game, rom); }
            catch (Exception ex) { Console.Error.WriteLine("map shots: opening the project failed: " + ex.Message); return 2; }

            List<string> names = MapShotScenes.HeaderNames();
            int count = MapShotScenes.HeaderCount();
            for (int i = 1; i < count; i++) Console.WriteLine($"{i:D3}\t{(i < names.Count ? names[i] : "")}");
            return 0;
        }

        /// <summary>A project folder, or the ROM's already extracted sibling folder.</summary>
        private static string ResolveRom(string given)
        {
            if (!string.IsNullOrWhiteSpace(given))
            {
                if (File.Exists(given) && given.EndsWith(".nds", StringComparison.OrdinalIgnoreCase))
                {
                    string sibling = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(given)) ?? "",
                        Path.GetFileNameWithoutExtension(given) + "_DSPRE_contents");
                    if (Directory.Exists(sibling)) return sibling;
                    throw new FileNotFoundException($"DSPRE reads extracted projects. Open '{given}' in DSPRE once to extract it, then pass the '{Path.GetFileName(sibling)}' folder.");
                }
                if (Directory.Exists(given)) return Path.GetFullPath(given);
                throw new DirectoryNotFoundException("no project folder at " + given);
            }

            string cwd = Directory.GetCurrentDirectory();
            string[] candidates = Directory.GetDirectories(cwd, "*_DSPRE_contents");
            if (candidates.Length == 1) return candidates[0];
            if (candidates.Length > 1)
            {
                foreach (string candidate in candidates)
                    if (candidate.Contains("Platinum", StringComparison.OrdinalIgnoreCase)) return candidate;
                return candidates[0];
            }
            throw new DirectoryNotFoundException("no project folder found. Pass --rom <folder>.");
        }

        /// <summary>The project's game code, from config.yaml or its folder name.</summary>
        private static string ResolveGameCode(string project)
        {
            string code = ReadGameCode(project);
            if (!string.IsNullOrWhiteSpace(code)) return code;
            string name = Path.GetFileName(project) ?? "";
            if (name.Contains("Platinum", StringComparison.OrdinalIgnoreCase)) return "CPUE";
            if (name.Contains("HeartGold", StringComparison.OrdinalIgnoreCase)) return "IPKE";
            if (name.Contains("SoulSilver", StringComparison.OrdinalIgnoreCase)) return "IPGE";
            if (name.Contains("Diamond", StringComparison.OrdinalIgnoreCase)) return "ADAE";
            if (name.Contains("Pearl", StringComparison.OrdinalIgnoreCase)) return "APAE";
            return null;
        }

        /// <summary>Reads game_code from a ds-rom project's config.yaml, so the game need not be named.</summary>
        private static string ReadGameCode(string project)
        {
            try
            {
                string config = Path.Combine(project, "config.yaml");
                if (!File.Exists(config)) return null;
                foreach (string line in File.ReadLines(config))
                {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("game_code:", StringComparison.OrdinalIgnoreCase)) continue;
                    string code = trimmed.Substring("game_code:".Length).Trim().Trim('"', '\'');
                    if (code.Length >= 4) return code.Substring(0, 4).ToUpperInvariant();
                }
            }
            catch (Exception ex) { AppLogger.WarnOnce("MapShotBatch.ReadGameCode", ex); }
            return null;
        }

        private static MapShotConfig LoadConfig(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new MapShotConfig();
            JsonSerializerOptions json = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            MapShotConfig config = JsonSerializer.Deserialize<MapShotConfig>(File.ReadAllText(path), json);
            return config ?? new MapShotConfig();
        }

        private static MapShotSettings FindSettings(MapShotConfig config, int id, string name)
        {
            if (config?.Maps == null) return null;
            foreach (MapShotSettings entry in config.Maps)
            {
                if (entry == null) continue;
                if (entry.Id.HasValue && entry.Id.Value == id) return entry;
                if (!string.IsNullOrWhiteSpace(entry.Name) && name != null
                    && name.IndexOf(entry.Name, StringComparison.OrdinalIgnoreCase) >= 0) return entry;
            }
            return null;
        }

        /// <summary>Applies the defaults block under one map entry; the entry wins field by field.</summary>
        private static MapShotSettings MergeSettings(MapShotSettings defaults, MapShotSettings entry)
        {
            if (defaults == null) return entry;
            if (entry == null) return defaults;
            return new MapShotSettings
            {
                Id = entry.Id ?? defaults.Id,
                Name = entry.Name ?? defaults.Name,
                Width = entry.Width ?? defaults.Width,
                Height = entry.Height ?? defaults.Height,
                PixelsPerTile = entry.PixelsPerTile ?? defaults.PixelsPerTile,
                Pitch = entry.Pitch ?? defaults.Pitch,
                Yaw = entry.Yaw ?? defaults.Yaw,
                OffsetXTiles = entry.OffsetXTiles ?? defaults.OffsetXTiles,
                OffsetZTiles = entry.OffsetZTiles ?? defaults.OffsetZTiles,
                Frame = entry.Frame ?? defaults.Frame,
                Background = entry.Background ?? defaults.Background,
                Enabled = entry.Enabled ?? defaults.Enabled,
                File = entry.File ?? defaults.File,
                Stack = entry.Stack ?? defaults.Stack,
                Gap = entry.Gap ?? defaults.Gap,
                HideMaterials = entry.HideMaterials ?? defaults.HideMaterials,
                Shadows = entry.Shadows ?? defaults.Shadows,
                Cells = entry.Cells ?? defaults.Cells,
                Shade = entry.Shade ?? defaults.Shade,
                Lighting = entry.Lighting ?? defaults.Lighting,
            };
        }

        private static HashSet<int> ParseIds(string text)
        {
            HashSet<int> ids = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(text)) return ids;
            foreach (string part in text.Split(',', ';'))
            {
                string trimmed = part.Trim();
                if (trimmed.Length == 0) continue;
                if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) ids.Add(id);
            }
            return ids;
        }

        private static List<(int x, int y)> CellsOf(MapShotSettings settings, Options options)
        {
            string text = options.Cells;
            if (string.IsNullOrWhiteSpace(text) && settings?.Cells != null && settings.Cells.Length > 0)
                text = string.Join(";", settings.Cells);
            if (string.IsNullOrWhiteSpace(text)) return null;
            List<(int x, int y)> cells = new List<(int, int)>();
            foreach (string part in text.Split(';', '|'))
            {
                string[] pair = part.Split(',');
                if (pair.Length != 2) continue;
                if (int.TryParse(pair[0].Trim(), out int x) && int.TryParse(pair[1].Trim(), out int y)) cells.Add((x, y));
            }
            return cells.Count > 0 ? cells : null;
        }

        private static bool ShadowsEnabled(MapShotSettings settings, Options options)
            => options.ShowShadows ?? settings?.Shadows ?? true;

        private static MapShotCamera MakeCamera(MapShotScene scene, MapShotSettings settings, Options options)
        {
            float ppt = options.Scale ?? settings?.PixelsPerTile ?? 16f;
            bool shadows = ShadowsEnabled(settings, options);
            HashSet<int> hidden = HiddenMaterials(scene.Scene, MergeHides(settings?.HideMaterials, options.Hide), shadows);
            // Frame from room geometry only, so the shadow/prop overlay can't shift or enlarge the composition.
            HashSet<int> framingHidden = HiddenMaterials(scene.Scene, MergeHides(settings?.HideMaterials, options.Hide), false);
            ContentBounds bounds = Content(scene.Scene, framingHidden);
            bool content = !string.Equals(settings?.Frame, "cell", StringComparison.OrdinalIgnoreCase);
            float centerX, centerZ;
            if (content || !bounds.Any || !scene.HasCellArea)
            {
                centerX = (bounds.MinX + bounds.MaxX) / 2f;
                centerZ = (bounds.MinZ + bounds.MaxZ) / 2f;
            }
            else
            {
                centerX = (scene.CellMinX + scene.CellMaxX) / 2f;
                centerZ = (scene.CellMinZ + scene.CellMaxZ) / 2f;
            }
            centerX += (settings?.OffsetXTiles ?? 0f) * 0.25f;
            centerZ += (settings?.OffsetZTiles ?? 0f) * 0.25f;

            int width = options.Width ?? settings?.Width ?? 0;
            int height = options.Height ?? settings?.Height ?? 0;
            if (width <= 0 || height <= 0)
            {
                float contentWidthTiles = bounds.Any ? (bounds.MaxX - bounds.MinX) * 4f : 1f;
                float contentHeightTiles = bounds.Any ? (bounds.MaxZ - bounds.MinZ) * 4f : 1f;
                float pitch = (options.Pitch ?? settings?.Pitch ?? 50f) * (float)Math.PI / 180f;
                float projectedHeight = contentHeightTiles * (float)Math.Sin(pitch)
                    + (bounds.MaxY - bounds.MinY) * 4f * (float)Math.Cos(pitch);
                if (width <= 0) width = RoundCanvas(contentWidthTiles * ppt);
                if (height <= 0) height = RoundCanvas(projectedHeight * ppt);
            }

            MapShotCamera camera = new MapShotCamera
            {
                Width = width,
                Height = height,
                PixelsPerTile = ppt,
                PitchDegrees = options.Pitch ?? settings?.Pitch ?? 50f,
                YawDegrees = settings?.Yaw ?? 0f,
                CenterX = centerX,
                CenterZ = centerZ,
                Transparent = true,
                HiddenMaterials = hidden,
                ShowShadows = shadows,
                FlipCulling = options.FlipCulling,
                NoShading = options.NoShade,
                ForceShading = options.ForceShade || (settings?.Shade ?? false),
                Lighting = options.Lighting ?? settings?.Lighting ?? true,
                IgnoreTextureMatrices = options.NoTexMatrix,
                NoTextures = options.NoTextures,
            };
            if (options.LightX.HasValue) camera.LightX = options.LightX.Value;
            if (options.LightY.HasValue) camera.LightY = options.LightY.Value;
            if (options.LightZ.HasValue) camera.LightZ = options.LightZ.Value;
            if (options.LightAmbient.HasValue) camera.LightAmbient = options.LightAmbient.Value;
            if (options.LightDiffuse.HasValue) camera.LightDiffuse = options.LightDiffuse.Value;
            ApplyBackground(camera, settings?.Background);
            return camera;
        }

        /// <summary>Canvas sides become 512, or the next 512 above that, which is the shape of a map rip.</summary>
        private static int RoundCanvas(float pixels)
        {
            int needed = Math.Max(1, (int)Math.Ceiling(pixels));
            if (needed <= 512) return 512;
            return (needed + 511) / 512 * 512;
        }

        private sealed class ContentBounds
        {
            public bool Any;
            public float MinX, MaxX, MinY, MaxY, MinZ, MaxZ;
        }

        /// <summary>Visible geometry bounds in raw scene units, with shadows and hidden materials left out.</summary>
        private static ContentBounds Content(NsbmdRenderModel scene, HashSet<int> hidden)
        {
            ContentBounds bounds = new ContentBounds();
            float scale = scene.Scale > 0f ? scene.Scale : 1f;
            foreach (NsbmdMeshPart part in scene.Parts)
            {
                if (part == null || part.VertexCount == 0) continue;
                if (hidden.Contains(part.MaterialIndex)) continue;
                float[] v = part.Vertices;
                for (int i = 0; i + 7 < v.Length; i += 8)
                {
                    float x = v[i] / scale + scene.Cx;
                    float y = v[i + 1] / scale + scene.Cy;
                    float z = v[i + 2] / scale + scene.Cz;
                    if (!bounds.Any)
                    {
                        bounds.MinX = bounds.MaxX = x;
                        bounds.MinY = bounds.MaxY = y;
                        bounds.MinZ = bounds.MaxZ = z;
                        bounds.Any = true;
                    }
                    else
                    {
                        bounds.MinX = Math.Min(bounds.MinX, x); bounds.MaxX = Math.Max(bounds.MaxX, x);
                        bounds.MinY = Math.Min(bounds.MinY, y); bounds.MaxY = Math.Max(bounds.MaxY, y);
                        bounds.MinZ = Math.Min(bounds.MinZ, z); bounds.MaxZ = Math.Max(bounds.MaxZ, z);
                    }
                }
            }
            return bounds;
        }

        private static string[] MergeHides(string[] configured, string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return configured;
            List<string> all = new List<string>();
            if (configured != null) all.AddRange(configured);
            all.AddRange(commandLine.Split(',', ';'));
            return all.ToArray();
        }

        private static HashSet<int> HiddenMaterials(NsbmdRenderModel scene, string[] extra, bool showShadows)
        {
            HashSet<int> hidden = new HashSet<int>();
            foreach (KeyValuePair<int, string> kv in scene.MaterialNameByKey)
            {
                string materialName = kv.Value ?? "";
                if (!showShadows && (materialName.IndexOf("kage", StringComparison.OrdinalIgnoreCase) >= 0
                    || materialName.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0)) hidden.Add(kv.Key);
                // The game's own black backdrop hides the void outside a room; a map rip has nothing there.
                if (string.Equals(materialName, "lambert1", StringComparison.OrdinalIgnoreCase)) hidden.Add(kv.Key);
                if (extra != null)
                    foreach (string wanted in extra)
                        if (!string.IsNullOrWhiteSpace(wanted)
                            && materialName.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) hidden.Add(kv.Key);
            }
            return hidden;
        }

        private static void ApplyBackground(MapShotCamera camera, string background)
        {
            if (string.IsNullOrWhiteSpace(background) || background.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return;
            string text = background.TrimStart('#');
            if (text.Length != 6) return;
            if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) return;
            camera.Transparent = false;
            camera.BackgroundR = (byte)((rgb >> 16) & 0xff);
            camera.BackgroundG = (byte)((rgb >> 8) & 0xff);
            camera.BackgroundB = (byte)(rgb & 0xff);
        }

        private static RawImage RenderStack(int headerId, MapShotSettings settings, Options options, out string note)
        {
            note = "";
            List<int> ids = new List<int> { headerId };
            foreach (int id in settings.Stack) if (id != headerId) ids.Add(id);

            List<RawImage> pieces = new List<RawImage>();
            foreach (int id in ids)
            {
                MapShotScene scene = MapShotScenes.Build(id, CellsOf(settings, options));
                if (scene == null || !scene.HasContent) { note = $"header {id} has no renderable map"; return null; }
                MapShotCamera camera = MakeCamera(scene, settings, options);
                RawImage image = MapShotRenderer.Render(scene.Scene, camera, out MapShotStats stats);
                pieces.Add(CropToContent(image, stats));
            }

            int gap = settings.Gap ?? 10;
            int contentWidth = 0, contentHeight = 0;
            foreach (RawImage piece in pieces)
            {
                contentWidth = Math.Max(contentWidth, piece.Width);
                contentHeight += piece.Height;
            }
            contentHeight += gap * Math.Max(0, pieces.Count - 1);

            int width = options.Width ?? settings.Width ?? 0;
            int height = options.Height ?? settings.Height ?? 0;
            if (width <= 0) width = RoundCanvas(contentWidth);
            if (height <= 0) height = RoundCanvas(contentHeight);

            RawImage result = new RawImage(width, height);
            int y = Math.Max(0, (height - contentHeight) / 2);
            foreach (RawImage piece in pieces)
            {
                int x = (width - piece.Width) / 2;
                Blit(result, piece, x, y);
                y += piece.Height + gap;
            }
            return result;
        }

        private static RawImage CropToContent(RawImage image, MapShotStats stats)
        {
            if (stats.OpaquePixels == 0) return image;
            int x0 = (int)Math.Floor(stats.ContentX0), y0 = (int)Math.Floor(stats.ContentY0);
            int x1 = (int)Math.Ceiling(stats.ContentX1), y1 = (int)Math.Ceiling(stats.ContentY1);
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
            x1 = Math.Min(image.Width - 1, x1); y1 = Math.Min(image.Height - 1, y1);
            int w = x1 - x0 + 1, h = y1 - y0 + 1;
            RawImage crop = new RawImage(w, h);
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(image.Bgra, ((y0 + y) * image.Width + x0) * 4, crop.Bgra, y * w * 4, w * 4);
            return crop;
        }

        private static void Blit(RawImage target, RawImage source, int x, int y)
        {
            for (int row = 0; row < source.Height; row++)
            {
                int ty = y + row;
                if (ty < 0 || ty >= target.Height) continue;
                Buffer.BlockCopy(source.Bgra, row * source.Width * 4, target.Bgra,
                    (ty * target.Width + x) * 4, source.Width * 4);
            }
        }

        private static void Pick(NsbmdRenderModel scene, MapShotStats stats, Options options)
        {
            if (options.PickX < 0 || options.PickY < 0 || stats.MaterialMap == null) return;
            if (options.PickX >= stats.Width || options.PickY >= stats.Height)
            {
                Console.WriteLine($"  pick ({options.PickX},{options.PickY}) is outside the {stats.Width}x{stats.Height} image");
                return;
            }
            int key = stats.MaterialMap[options.PickY * stats.Width + options.PickX];
            string materialName = scene.MaterialNameByKey.TryGetValue(key, out string found) ? found : "";
            scene.Textures.TryGetValue(key, out NsbmdTextureData tex);
            string texture = tex == null ? "no texture" : $"{tex.Width}x{tex.Height} avg {AverageColour(tex)}";
            Console.WriteLine($"  pick ({options.PickX},{options.PickY}) = material {key} '{materialName}' {texture}");
        }

        private static void Save(RawImage image, string path)
        {
            PlainFiles.WriteBytes(path, GameBanner.EncodePng(image));
        }

        private static string SafeFileName(string name)
        {
            StringBuilder text = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
                    text.Append('_');
                else
                    text.Append(c);
            }
            string result = text.ToString().Trim().Trim('.');
            return result.Length == 0 ? "map" : result;
        }

        private static void PrintInfo(int id, string name, MapShotSettings settings, Options options)
        {
            MapShotScene scene = MapShotScenes.Build(id, CellsOf(settings, options));
            if (scene == null) { Console.WriteLine($"{id:D3} {name}: no scene"); return; }
            MapShotCamera camera = MakeCamera(scene, settings, options);
            ContentBounds bounds = Content(scene.Scene, HiddenMaterials(scene.Scene, settings?.HideMaterials, false));
            float tileMinX = bounds.MinX * 4f, tileMaxX = bounds.MaxX * 4f;
            float tileMinZ = bounds.MinZ * 4f, tileMaxZ = bounds.MaxZ * 4f;
            string internalName = MapShotScenes.InternalName(id, MapShotScenes.HeaderCount());
            Console.WriteLine($"{id:D3} {name} [{internalName}]");
            Console.WriteLine($"  matrix {scene.MatrixId} {scene.MatrixWidth}x{scene.MatrixHeight}, {scene.MapCount} map(s)");
            Console.WriteLine($"  cells x {scene.CellMinX:F2}..{scene.CellMaxX:F2} z {scene.CellMinZ:F2}..{scene.CellMaxZ:F2} raw");
            Console.WriteLine($"  content tiles x {tileMinX:F2}..{tileMaxX:F2} z {tileMinZ:F2}..{tileMaxZ:F2}, height {bounds.MinY:F2}..{bounds.MaxY:F2}");
            Console.WriteLine($"  triangles {scene.Scene.TotalVertices / 3}, parts {scene.Scene.Parts.Count}");
            Console.WriteLine($"  canvas {camera.Width}x{camera.Height} at {camera.PixelsPerTile}px/tile, pitch {camera.PitchDegrees}, centre ({camera.CenterX:F3}, {camera.CenterZ:F3})");

            if (!options.Materials) return;
            foreach (NsbmdMeshPart part in scene.Scene.Parts)
            {
                string materialName = scene.Scene.MaterialNameByKey.TryGetValue(part.MaterialIndex, out string found) ? found : "";
                scene.Scene.Textures.TryGetValue(part.MaterialIndex, out NsbmdTextureData tex);
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                for (int i = 0; i + 7 < part.Vertices.Length; i += 8)
                {
                    minX = Math.Min(minX, part.Vertices[i]); maxX = Math.Max(maxX, part.Vertices[i]);
                    minZ = Math.Min(minZ, part.Vertices[i + 2]); maxZ = Math.Max(maxZ, part.Vertices[i + 2]);
                }
                string texture = tex == null ? "no texture" : $"{tex.Width}x{tex.Height} wrap {tex.WrapS}/{tex.WrapT} avg {AverageColour(tex)}";
                float scale = scene.Scene.Scale > 0f ? scene.Scene.Scale : 1f;
                float rawMinX = (minX / scale + scene.Scene.Cx) * 4f, rawMaxX = (maxX / scale + scene.Scene.Cx) * 4f;
                float rawMinZ = (minZ / scale + scene.Scene.Cz) * 4f, rawMaxZ = (maxZ / scale + scene.Scene.Cz) * 4f;
                string light = $"lights {part.LightMask} dif {part.DiffuseR:F2}/{part.DiffuseG:F2}/{part.DiffuseB:F2} amb {part.AmbientR:F2}/{part.AmbientG:F2}/{part.AmbientB:F2}";
                Console.WriteLine($"    mat {part.MaterialIndex,3} {materialName,-20} alpha {part.Alpha:F2} cull {part.CullMode} shade {(part.Shade ? 1 : 0)} {light} verts {part.VertexCount,6} x {rawMinX:F1}..{rawMaxX:F1} z {rawMinZ:F1}..{rawMaxZ:F1} {texture}");
            }
        }

        private static string AverageColour(NsbmdTextureData tex)
        {
            long r = 0, g = 0, b = 0, n = 0;
            for (int i = 0; i + 3 < tex.Rgba.Length; i += 4)
            {
                if (tex.Rgba[i + 3] < 128) continue;
                r += tex.Rgba[i]; g += tex.Rgba[i + 1]; b += tex.Rgba[i + 2]; n++;
            }
            if (n == 0) return "transparent";
            return $"#{r / n:X2}{g / n:X2}{b / n:X2}";
        }
    }
}
