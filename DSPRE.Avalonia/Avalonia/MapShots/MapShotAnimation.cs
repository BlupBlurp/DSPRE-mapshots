using System;
using System.Collections.Generic;
using DSPRE.Avalonia.Gl;
using DSPRE.ROMFiles;

namespace DSPRE.Avalonia.MapShots
{
    /// <summary>
    /// Plays a map scene's field animations frame by frame for a headless capture. It combines the
    /// building animator the map view uses with the area's terrain scroll and the map's field texture
    /// animations, all on the field's 30-steps-a-second clock.
    /// </summary>
    public sealed class MapShotAnimation
    {
        /// <summary>Frames a capture uses when an animation has no finite loop.</summary>
        public const int DefaultFrames = 120;

        private readonly SceneBuildingAnimator _buildings;
        private readonly List<(int MaterialKey, TextureSrtAnimation Anim, int Index)> _ground;
        private readonly List<(int MaterialKey, List<(string Swap, int Frames)> Sequence)> _field;

        /// <summary>Frames until every animation in the scene starts over. One means no finite loop.</summary>
        public int LoopFrames { get; }

        /// <summary>Whether the scene has anything the field clock moves.</summary>
        public bool HasAnything { get; }

        public MapShotAnimation(MapShotScene scene)
        {
            if (scene?.Scene == null) return;
            _buildings = new SceneBuildingAnimator(scene.Scene, scene.Indoor, scene.TimeOfDay);
            _ground = scene.Scene.GroundScrolls ?? new List<(int, TextureSrtAnimation, int)>();
            _field = scene.Scene.FieldAnimations ?? new List<(int, List<(string, int)>)>();

            int loop = _buildings.LoopFrames;
            foreach ((int _, TextureSrtAnimation anim, int _) in _ground)
                loop = CombineLoop(loop, anim?.FrameCount ?? 1);
            foreach ((int _, List<(string Swap, int Frames)> sequence) in _field)
                loop = CombineLoop(loop, SequenceFrames(sequence));

            LoopFrames = Math.Max(1, loop);
            HasAnything = _buildings.HasAnything || _ground.Count > 0 || _field.Count > 0;
        }

        /// <summary>The animation state on one field frame.</summary>
        public MapShotFrame At(int frame)
        {
            MapShotFrame result = new MapShotFrame();
            if (frame < 0) frame = 0;

            Dictionary<int, float[]> matrices = new Dictionary<int, float[]>();
            foreach ((int key, TextureSrtAnimation anim, int index) in _ground)
            {
                if (anim == null) continue;
                int length = Math.Max(1, anim.FrameCount);
                matrices[key] = anim.Evaluate(index, frame % length).ToMatrix3();
            }

            // The GL view lets a building animation replace a material's terrain scroll for the frame.
            SceneBuildingAnimator.Frame buildings = _buildings?.At(frame);
            if (buildings?.TextureMatrices != null)
                foreach (KeyValuePair<int, float[]> kv in buildings.TextureMatrices) matrices[kv.Key] = kv.Value;
            if (matrices.Count > 0) result.TextureMatrices = matrices;

            Dictionary<int, string> swaps = new Dictionary<int, string>();
            foreach ((int key, List<(string Swap, int Frames)> sequence) in _field)
            {
                int total = SequenceFrames(sequence);
                if (total <= 0) continue;
                int at = frame % total;
                foreach ((string swap, int frames) in sequence)
                {
                    if (at < frames) { swaps[key] = swap; break; }
                    at -= frames;
                }
            }
            // The GL view prefers a field texture swap over a building's, so only fill the gaps.
            if (buildings?.TextureSwaps != null)
                foreach (KeyValuePair<int, string> kv in buildings.TextureSwaps)
                    if (!swaps.ContainsKey(kv.Key)) swaps[kv.Key] = kv.Value;
            if (swaps.Count > 0) result.TextureSwaps = swaps;

            result.MaterialFades = buildings?.MaterialFades;
            result.MovedParts = buildings?.MovedParts;
            return result;
        }

        private static int SequenceFrames(List<(string Swap, int Frames)> sequence)
        {
            int total = 0;
            if (sequence == null) return 0;
            foreach ((string _, int frames) in sequence) total += Math.Max(1, frames);
            return total;
        }

        /// <summary>The least common multiple of two loops, kept small enough to be a practical capture.</summary>
        private static int CombineLoop(int a, int b)
        {
            if (a <= 1) return Math.Max(1, b);
            if (b <= 1) return a;
            int x = a, y = b;
            while (y != 0) { int t = x % y; x = y; y = t; }
            long value = (long)a / x * b;
            return value > 600 ? 600 : (int)value;
        }
    }
}
