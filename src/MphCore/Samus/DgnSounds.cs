// DgnSounds - MPH "DGN" sounds: samples whose volume and pitch follow curves driven by two amounts (footsteps:
// A = max, B = random per step; landing: A = air time; roll: A = ball speed; Shock Coil: A = homing time). Curves are
// read from the ROM's DGNFILES.DAT by MphRead (SoundRead.ReadDgnFiles); evaluation ported from the testbed's playtested
// hunters/mph_sound.cpp (evalDgn, curveValue, pitchMultiplier), itself MphRead (MIT) Sfx.UpdateDgn / GetDgnValue /
// CalculatePitchDiv.
using System;
using System.Collections.Generic;
using MphRead.Formats.Sound;

namespace MphSamus.Core
{
    public enum MphTerrain { Metal, Ice, Snow, Sand, Rock, Lava }
    public enum TerrainSound { Step1, Step2, Land, Roll }

    public struct DgnResult
    {
        public int Sample;          // SNDTBLS / SNDSAMPLES index
        public float Volume;        // 0..1 (0 = silent), the DGN's own volume (not the sample table's)
        public float Pitch;         // playback rate multiplier
    }

    public static class DgnSounds
    {
        // ---- TUNABLE CONSTANTS (MphRead UpdateDgn) ----
        private const float MinVolume = 1 / 130f;    // quieter than this = silent
        private const float PitchOne = 0x2000;       // DS pitch unit (8192 = original rate)
        // DGN per terrain (rows Metal..Lava) x (step 1, step 2, land, roll); testbed tools/scripts/mph_sfx.py TERRAIN_DGNS
        private static readonly int[,] TerrainDgns =
        {
            { 3, 4, 1, 0 }, { 15, 16, 14, 21 }, { 12, 13, 11, 20 }, { 18, 19, 17, 23 }, { 9, 10, 8, 22 }, { 5, 6, 7, -1 }
        };

        private static IReadOnlyList<DgnFile>? _files;
        public static string? LastError { get; private set; }

        private static bool Load()
        {
            if (_files != null) { return true; }
            try
            {
                _files = SoundRead.ReadDgnFiles();
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _files = Array.Empty<DgnFile>();
                return false;
            }
        }

        public static int EntryCount(int dgn) => Load() && dgn >= 0 && dgn < _files!.Count ? _files[dgn].Entries.Count : 0;

        // one sample of a DGN (entry index) at amounts A and B
        public static DgnResult Eval(int dgn, int entry, float amountA, float amountB)
        {
            var r = new DgnResult { Sample = -1, Pitch = 1 };
            if (!Load() || dgn < 0 || dgn >= _files!.Count || entry < 0 || entry >= _files[dgn].Entries.Count) { return r; }
            DgnFile f = _files[dgn];
            DgnFileEntry e = f.Entries[entry];
            r.Sample = (int)e.SfxId;
            if (f.Header.InitialVolume == 0) { return r; }
            float volA = Curve(e.Data1, amountA), volB = Curve(e.Data2, amountB);
            float pitchA = Curve(e.Data3, amountA), pitchB = Curve(e.Data4, amountB);
            float vol = volA / 127f * volB / 127f * f.Header.InitialVolume / 127f;
            r.Volume = vol < MinVolume ? 0 : vol;
            float pitchFac = pitchA / PitchOne * pitchB;
            if (pitchFac >= 0x4000) { pitchFac = 0x3FFF; }
            r.Pitch = PitchMultiplier(pitchFac);
            return r;
        }

        public static DgnResult Terrain(MphTerrain t, TerrainSound s, float amountA, float amountB)
        {
            int dgn = TerrainDgns[(int)t, (int)s];
            return dgn < 0 ? new DgnResult { Sample = -1, Pitch = 1 } : Eval(dgn, 0, amountA, amountB);
        }

        // GetDgnValue: piecewise curve, linear or sine-eased segments (bit 14 of the next point)
        private static float Curve(IReadOnlyList<DgnData> c, float amount)
        {
            if (c.Count == 0) { return 0; }
            if (amount <= c[0].Amount) { return c[0].Value & 0x3FFF; }
            if (amount >= c[c.Count - 1].Amount) { return c[c.Count - 1].Value & 0x3FFF; }
            for (int i = 0; i + 1 < c.Count; i++)
            {
                if (amount < c[i + 1].Amount)
                {
                    float ratio = (amount - c[i].Amount) / (c[i + 1].Amount - c[i].Amount);
                    float v1 = c[i].Value & 0x3FFF, v2 = c[i + 1].Value & 0x3FFF;
                    if ((c[i + 1].Value & 0xC000) == 0x4000) { return v1 + (v2 - v1) * MathF.Sin(MathF.PI / 2 * ratio); }
                    return v1 + (v2 - v1) * ratio;
                }
            }
            return 0;
        }

        // Sfx.CalculatePitchDiv: DS pitch value -> rate multiplier
        private static float PitchMultiplier(float pitchFac)
        {
            if (pitchFac == 0) { pitchFac = 1; }
            int p = (int)pitchFac;
            if (pitchFac <= 0xFFF) { p = -((0x600000 / p) >> 1); }
            else if (pitchFac <= 0x1FFF) { p = (768 * (p - 0x2000)) >> 12; }
            else { p = (768 * (p - 0x2000)) >> 13; }
            return MathF.Pow(2, p / 64f / 12f);
        }
    }
}
