using System;
using System.Collections.Generic;
using DSPRE.Avalonia.Gl;
using DSPRE.ROMFiles;

namespace DSPRE.Avalonia.MapShots
{
    /// <summary>Camera and canvas settings for a headless map screenshot.</summary>
    public sealed class MapShotCamera
    {
        public int Width = 512;
        public int Height = 512;

        /// <summary>Horizontal pixels for one 16×16 map tile. One tile is a quarter of a scene unit.</summary>
        public float PixelsPerTile = 16f;

        /// <summary>90 looks straight down. Lower values tilt the camera towards the south, as the field camera does.</summary>
        public float PitchDegrees = 90f;

        public float YawDegrees;

        /// <summary>The raw scene point the middle of the canvas looks at.</summary>
        public float CenterX;
        public float CenterZ;

        public bool Transparent = true;
        public byte BackgroundR, BackgroundG, BackgroundB;

        /// <summary>Material keys not to draw, for cast shadows and other layers a map rip leaves out.</summary>
        public HashSet<int> HiddenMaterials = new HashSet<int>();

        /// <summary>For diagnosing a model whose winding is opposite the DS engine's.</summary>
        public bool FlipCulling;

        /// <summary>Draw the baked cast-shadow meshes instead of leaving them out.</summary>
        public bool ShowShadows;

        /// <summary>Draw textures as they are, without the baked per-vertex colour. Map rips are flat lit.</summary>
        public bool NoShading;

        /// <summary>Apply vertex colours even where the model disables shading, for baked map shadows.</summary>
        public bool ForceShading;

        /// <summary>Apply directional face lighting to opaque textured parts. Map rips use the field light.</summary>
        public bool Lighting;

        /// <summary>Direction towards the light, in scene space. It is normalized when rendering.</summary>
        public float LightX = -0.45f;
        public float LightY = 0.80f;
        public float LightZ = 0.50f;
        public float LightAmbient = 0.55f;
        public float LightDiffuse = 0.75f;

        /// <summary>For diagnosing texture matrices: draw raw texture coordinates.</summary>
        public bool IgnoreTextureMatrices;

        /// <summary>For diagnosing placement: draw untextured vertex colours.</summary>
        public bool NoTextures;
    }

    public sealed class MapShotStats
    {
        public int Triangles, DrawnTriangles;
        public int OpaquePixels;
        public float ContentX0, ContentY0, ContentX1, ContentY1;

        /// <summary>The material that last drew each pixel, for the --pick probe.</summary>
        public int[] MaterialMap;
        public int Width, Height;
    }

    /// <summary>
    /// Draws an <see cref="NsbmdRenderModel"/> without OpenGL, so maps can be captured on a machine with no
    /// window. It follows the shader and fixed-function state the editors use: an orthographic camera,
    /// nearest-neighbour DS textures with their wrap modes, the material's texture matrix, per-vertex
    /// modulate colour, alpha test at the same thresholds, the same two opaque/see-through passes, and the
    /// same face culling. Optional face lighting, no fog, tint or 2D layers are applied, which is what
    /// a map rip shows. Lighting is a directional approximation of the field light, not the full NDS
    /// four-light setup.
    /// </summary>
    public static class MapShotRenderer
    {
        /// <summary>A triangle whose vertices run counter-clockwise in NDC is front-facing, as in OpenGL.</summary>
        private static bool IsFront(float area) => area < 0f;

        private readonly struct MapShotLight
        {
            public readonly bool Enabled;
            public readonly float X, Y, Z, Ambient, Diffuse;

            public MapShotLight(MapShotCamera cam)
            {
                Enabled = cam.Lighting;
                float length = (float)Math.Sqrt(cam.LightX * cam.LightX + cam.LightY * cam.LightY + cam.LightZ * cam.LightZ);
                if (length < 0.0001f)
                {
                    X = 0f; Y = 1f; Z = 0f;
                }
                else
                {
                    X = cam.LightX / length; Y = cam.LightY / length; Z = cam.LightZ / length;
                }
                Ambient = cam.LightAmbient;
                Diffuse = cam.LightDiffuse;
            }
        }

        public static RawImage Render(NsbmdRenderModel scene, MapShotCamera cam, out MapShotStats stats)
        {
            stats = new MapShotStats();
            RawImage image = new RawImage(cam.Width, cam.Height);
            byte[] pixels = image.Bgra;
            if (cam.Transparent)
            {
                Array.Clear(pixels);
            }
            else
            {
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i] = cam.BackgroundB;
                    pixels[i + 1] = cam.BackgroundG;
                    pixels[i + 2] = cam.BackgroundR;
                    pixels[i + 3] = 255;
                }
            }

            if (scene == null || scene.Parts == null || scene.Parts.Count == 0) return image;

            float sceneScale = scene.Scale > 0f ? scene.Scale : 1f;
            float pixelsPerUnit = cam.PixelsPerTile * 4f / sceneScale;
            float halfHeight = cam.Height / (2f * pixelsPerUnit);
            float aspect = (float)cam.Width / cam.Height;

            float[] proj = Mat4.Ortho(halfHeight, aspect, -4000f, 4000f);
            float[] view = Mat4.Multiply(
                Mat4.OrbitView(Math.Max(4f, halfHeight * 4f), cam.YawDegrees, cam.PitchDegrees),
                Mat4.Translate(-(cam.CenterX - scene.Cx) * sceneScale, 0f, -(cam.CenterZ - scene.Cz) * sceneScale));
            float[] mvp = Mat4.Multiply(proj, view);

            int width = cam.Width, height = cam.Height;
            int count = width * height;
            float[] depth = new float[count];
            for (int i = 0; i < count; i++) depth[i] = float.MaxValue;

            MapShotLight light = new MapShotLight(cam);
            bool[] written = new bool[count];
            int[] materialMap = new int[count];
            for (int i = 0; i < count; i++) materialMap[i] = -1;
            stats.MaterialMap = materialMap;
            stats.Width = width;
            stats.Height = height;

            // Opaque geometry first, then the see-through textures, the order the GL view draws in.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int pi = 0; pi < scene.Parts.Count; pi++)
                {
                    NsbmdMeshPart part = scene.Parts[pi];
                    if (part == null || part.VertexCount < 3 || part.CullMode == NsbmdCull.Nothing) continue;
                    if (cam.HiddenMaterials != null && cam.HiddenMaterials.Contains(part.MaterialIndex)) continue;

                    NsbmdTextureData tex = null;
                    if (scene.Textures != null) scene.Textures.TryGetValue(part.MaterialIndex, out tex);
                    bool hasTexture = !cam.NoTextures && tex != null && tex.Rgba != null && tex.Width > 0 && tex.Height > 0;
                    // Only a material alpha below solid needs blending. A cut-out texture stays in the opaque
                    // pass and writes depth, or a later overlay passes the depth test through a tree canopy.
                    bool translucent = part.Alpha < 0.999f;
                    if (translucent != (pass == 1)) continue;

                    float[] m = part.TexMatrix;
                    bool hasMatrix = !cam.IgnoreTextureMatrices && m != null && m.Length >= 9;
                    float alpha = part.Alpha;
                    if (alpha <= 0.001f) continue;

                    int n = part.VertexCount;
                    float[] sx = new float[n], sy = new float[n], sz = new float[n], su = new float[n], sv = new float[n];
                    float[] sr = new float[n], sg = new float[n], sb = new float[n];
                    float[] v = part.Vertices;
                    for (int i = 0; i < n; i++)
                    {
                        int o = i * 8;
                        float x = v[o], y = v[o + 1], z = v[o + 2];
                        float cx = mvp[0] * x + mvp[4] * y + mvp[8] * z + mvp[12];
                        float cy = mvp[1] * x + mvp[5] * y + mvp[9] * z + mvp[13];
                        float cz = mvp[2] * x + mvp[6] * y + mvp[10] * z + mvp[14];
                        sx[i] = (cx * 0.5f + 0.5f) * width;
                        // Image rows run downwards, the reverse of the GL window's Y.
                        sy[i] = (0.5f - cy * 0.5f) * height;
                        sz[i] = cz;
                        float u = v[o + 3], w = v[o + 4];
                        if (hasMatrix)
                        {
                            su[i] = m[0] * u + m[3] * w + m[6];
                            sv[i] = m[1] * u + m[4] * w + m[7];
                        }
                        else { su[i] = u; sv[i] = w; }
                        sr[i] = v[o + 5]; sg[i] = v[o + 6]; sb[i] = v[o + 7];
                    }

                    DrawPart(part, tex, hasTexture, alpha, cam.FlipCulling, cam.NoShading, cam.ForceShading, light,
                        sx, sy, sz, su, sv, sr, sg, sb, pixels, depth, written, materialMap, width, height, stats);
                }
            }

            return image;
        }

        private static void DrawPart(NsbmdMeshPart part, NsbmdTextureData tex, bool textured,
            float alpha, bool flipCulling, bool noShading, bool forceShading, MapShotLight light, float[] sx, float[] sy, float[] sz, float[] su, float[] sv,
            float[] sr, float[] sg, float[] sb, byte[] pixels, float[] depth, bool[] written, int[] materialMap,
            int width, int height, MapShotStats stats)
        {
            bool blend = alpha < 0.999f;
            // A translucent texture lies on the ground it covers; the GL view offsets it towards the camera and
            // leaves the depth buffer alone so it does not hide the tiles drawn after it.
            bool bias = blend && textured;
            // The depth range spans 8000 scene units, so this is a few hundredths of a unit. A larger NDC
            // bias moves a ground shadow several units forward and draws it over the wall behind it.
            float depthBias = bias ? 0.000002f : 0f;
            int n = part.VertexCount;
            float[] raw = part.Vertices;
            byte[] texels = textured ? tex.Rgba : null;
            int tw = textured ? tex.Width : 0, th = textured ? tex.Height : 0;
            int wrapS = textured ? tex.WrapS : 0, wrapT = textured ? tex.WrapT : 0;

            for (int t = 0; t + 2 < n; t += 3)
            {
                stats.Triangles++;
                float x0 = sx[t], y0 = sy[t], z0 = sz[t];
                float x1 = sx[t + 1], y1 = sy[t + 1], z1 = sz[t + 1];
                float x2 = sx[t + 2], y2 = sy[t + 2], z2 = sz[t + 2];
                float area = (x1 - x0) * (y2 - y0) - (y1 - y0) * (x2 - x0);
                if (area == 0f || float.IsNaN(area)) continue;

                bool front = IsFront(area) != flipCulling;
                if (part.CullMode == NsbmdCull.Front && front) continue;
                if (part.CullMode == NsbmdCull.Back && !front) continue;

                float minX = Math.Min(x0, Math.Min(x1, x2));
                float maxX = Math.Max(x0, Math.Max(x1, x2));
                float minY = Math.Min(y0, Math.Min(y1, y2));
                float maxY = Math.Max(y0, Math.Max(y1, y2));
                int xa = Math.Max(0, (int)Math.Floor(minX));
                int xb = Math.Min(width - 1, (int)Math.Ceiling(maxX));
                int ya = Math.Max(0, (int)Math.Floor(minY));
                int yb = Math.Min(height - 1, (int)Math.Ceiling(maxY));
                if (xa > xb || ya > yb) continue;
                stats.DrawnTriangles++;

                float litR = 1f, litG = 1f, litB = 1f;
                if (light.Enabled && textured && !blend && part.LightMask != 0)
                {
                    int q0 = t * 8, q1 = (t + 1) * 8, q2 = (t + 2) * 8;
                    float ax = raw[q1] - raw[q0], ay = raw[q1 + 1] - raw[q0 + 1], az = raw[q1 + 2] - raw[q0 + 2];
                    float bx = raw[q2] - raw[q0], by = raw[q2 + 1] - raw[q0 + 1], bz = raw[q2 + 2] - raw[q0 + 2];
                    float nx = ay * bz - az * by;
                    float ny = az * bx - ax * bz;
                    float nz = ax * by - ay * bx;
                    float length = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (length > 0.0001f)
                    {
                        nx /= length; ny /= length; nz /= length;
                        float facing = Math.Max(0f, nx * light.X + ny * light.Y + nz * light.Z);
                        litR = part.EmissionR + part.AmbientR * light.Ambient + part.DiffuseR * light.Diffuse * facing;
                        litG = part.EmissionG + part.AmbientG * light.Ambient + part.DiffuseG * light.Diffuse * facing;
                        litB = part.EmissionB + part.AmbientB * light.Ambient + part.DiffuseB * light.Diffuse * facing;
                        litR = Math.Clamp(litR, 0f, 1.25f);
                        litG = Math.Clamp(litG, 0f, 1.25f);
                        litB = Math.Clamp(litB, 0f, 1.25f);
                    }
                }

                float invArea = 1f / area;
                float u0 = su[t], v0 = sv[t], u1 = su[t + 1], v1 = sv[t + 1], u2 = su[t + 2], v2 = sv[t + 2];
                float r0 = sr[t], g0 = sg[t], b0 = sb[t];
                float r1 = sr[t + 1], g1 = sg[t + 1], b1 = sb[t + 1];
                float r2 = sr[t + 2], g2 = sg[t + 2], b2 = sb[t + 2];

                for (int py = ya; py <= yb; py++)
                {
                    float fy = py + 0.5f;
                    int row = py * width;
                    for (int px = xa; px <= xb; px++)
                    {
                        float fx = px + 0.5f;
                        float e0 = (x1 - x0) * (fy - y0) - (y1 - y0) * (fx - x0);
                        float e1 = (x2 - x1) * (fy - y1) - (y2 - y1) * (fx - x1);
                        float e2 = (x0 - x2) * (fy - y2) - (y0 - y2) * (fx - x2);
                        if (area > 0f)
                        {
                            if (e0 < 0f || e1 < 0f || e2 < 0f) continue;
                        }
                        else
                        {
                            if (e0 > 0f || e1 > 0f || e2 > 0f) continue;
                        }

                        // e1 is opposite p0, e2 opposite p1 and e0 opposite p2, so the weights rotate.
                        float w0 = e1 * invArea, w1 = e2 * invArea, w2 = e0 * invArea;
                        float z = z0 * w0 + z1 * w1 + z2 * w2;
                        float zz = z - depthBias;
                        int idx = row + px;
                        if (zz >= depth[idx]) continue;

                        float u = u0 * w0 + u1 * w1 + u2 * w2;
                        float vv = v0 * w0 + v1 * w1 + v2 * w2;
                        float cr = r0 * w0 + r1 * w1 + r2 * w2;
                        float cg = g0 * w0 + g1 * w1 + g2 * w2;
                        float cb = b0 * w0 + b1 * w1 + b2 * w2;

                        float pr = cr, pg = cg, pb = cb, pa = alpha;
                        if (textured)
                        {
                            int tx = SampleIndex(u, tw, wrapS);
                            int ty = SampleIndex(vv, th, wrapT);
                            int ti = (ty * tw + tx) * 4;
                            float tr = texels[ti], tg = texels[ti + 1], tb = texels[ti + 2];
                            int ta = texels[ti + 3];
                            if (blend)
                            {
                                if (ta < 6) continue;
                                pa = alpha * (ta / 255f);
                            }
                            else
                            {
                                if (ta < 128) continue;
                                pa = 1f;
                            }
                            pr = tr / 255f; pg = tg / 255f; pb = tb / 255f;
                            if ((part.Shade || forceShading) && !noShading)
                            {
                                pr *= cr; pg *= cg; pb *= cb;
                            }
                            if (light.Enabled && !blend)
                            {
                                pr *= litR; pg *= litG; pb *= litB;
                            }
                        }

                        int o = idx * 4;
                        if (blend)
                        {
                            float a = Math.Clamp(pa, 0f, 1f);
                            float inv = 1f - a;
                            float dr = pixels[o + 2] / 255f, dg = pixels[o + 1] / 255f, db = pixels[o] / 255f;
                            float da = pixels[o + 3] / 255f;
                            float or = pr * a + dr * inv;
                            float og = pg * a + dg * inv;
                            float ob = pb * a + db * inv;
                            float oa = a + da * inv;
                            pixels[o] = ToByte(ob);
                            pixels[o + 1] = ToByte(og);
                            pixels[o + 2] = ToByte(or);
                            pixels[o + 3] = ToByte(oa);
                        }
                        else
                        {
                            pixels[o] = ToByte(pb);
                            pixels[o + 1] = ToByte(pg);
                            pixels[o + 2] = ToByte(pr);
                            pixels[o + 3] = 255;
                        }

                        if (!written[idx])
                        {
                            if (stats.OpaquePixels == 0)
                            {
                                stats.ContentX0 = stats.ContentX1 = px;
                                stats.ContentY0 = stats.ContentY1 = py;
                            }
                            else
                            {
                                if (px < stats.ContentX0) stats.ContentX0 = px;
                                if (px > stats.ContentX1) stats.ContentX1 = px;
                                if (py < stats.ContentY0) stats.ContentY0 = py;
                                if (py > stats.ContentY1) stats.ContentY1 = py;
                            }
                            stats.OpaquePixels++;
                        }
                        written[idx] = true;
                        materialMap[idx] = part.MaterialIndex;
                        if (!bias) depth[idx] = zz;
                    }
                }
            }
        }

        private static byte ToByte(float value)
        {
            int v = (int)(Math.Clamp(value, 0f, 1f) * 255f + 0.5f);
            return (byte)v;
        }

        private static int SampleIndex(float u, int size, int wrap)
        {
            if (wrap == 1)
            {
                float wrapped = u - (float)Math.Floor(u);
                int i = (int)(wrapped * size);
                return i < 0 ? 0 : (i >= size ? size - 1 : i);
            }
            if (wrap == 2)
            {
                float period = u - (float)Math.Floor(u * 0.5f) * 2f;
                if (period > 1f) period = 2f - period;
                int i = (int)(period * size);
                return i < 0 ? 0 : (i >= size ? size - 1 : i);
            }
            int at = (int)Math.Floor(u * size);
            return at < 0 ? 0 : (at >= size ? size - 1 : at);
        }
    }
}
