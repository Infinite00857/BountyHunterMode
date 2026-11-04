// SoundBank - MPH sound samples by SNDTBLS name (e.g. "BEAM", "JUMP"), decoded to 16-bit PCM by MphRead
// (SoundRead.GetWaveData: IMA-ADPCM / PCM8 / PCM16). SNDTBLS entry i = SNDSAMPLES sample i (testbed mph_sfx.py).
using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Formats.Sound;

namespace MphSamus.Core
{
    public sealed class PcmSound
    {
        public string Name = "";
        public int SampleRate;
        public float[] Samples = Array.Empty<float>();   // mono, -1..1
        public bool Loop;
        public int LoopStart;                              // in samples
        public float Volume = 1;                           // SNDTBLS InitialVolume / 127
    }

    public static class SoundBank
    {
        private static IReadOnlyList<SoundSample>? _samples;
        private static readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<byte> _volumes = new List<byte>();
        private static readonly Dictionary<string, PcmSound?> _cache = new Dictionary<string, PcmSound?>(StringComparer.OrdinalIgnoreCase);

        public static void Load()
        {
            if (_samples != null)
            {
                return;
            }
            SoundTable table = SoundRead.ReadSoundTables();
            for (int i = 0; i < table.Entries.Count; i++)
            {
                string name = table.Entries[i].Name;
                if (!_index.ContainsKey(name))
                {
                    _index[name] = i;
                }
                _volumes.Add(table.Entries[i].InitialVolume);
            }
            _samples = SoundRead.ReadSoundSamples();
        }

        public static IEnumerable<string> Names => _index.Keys;
        public static string? NameAt(int sample)
        {
            Load();
            foreach (KeyValuePair<string, int> kv in _index)
            {
                if (kv.Value == sample) { return kv.Key; }
            }
            return null;
        }
        public static string? LastError { get; private set; }

        public static PcmSound? Get(string name)
        {
            Load();
            if (_cache.TryGetValue(name, out PcmSound? cached))
            {
                return cached;
            }
            PcmSound? result = null;
            if (_index.TryGetValue(name, out int i) && _samples != null && i < _samples.Count)
            {
                try
                {
                    result = Decode(_samples[i], name, _volumes[i]);
                }
                catch (Exception ex)
                {
                    LastError = $"{name}: {ex}";
                    result = null;   // empty ROM slot
                }
            }
            _cache[name] = result;
            return result;
        }

        private static PcmSound? Decode(SoundSample sample, string name, byte volume)
        {
            // raw samples: ADPCM decodes to 16-bit LE, PCM8 comes back unsigned 8-bit (no WAV header)
            byte[] raw = SoundRead.GetWaveData(sample);
            bool pcm8 = sample.Format == WaveFormat.PCM8;
            int n = pcm8 ? raw.Length : raw.Length / 2;
            if (n <= 0)
            {
                return null;
            }
            var s = new float[n];
            for (int i = 0; i < n; i++)
            {
                s[i] = pcm8 ? (raw[i] - 128) / 128f : BitConverter.ToInt16(raw, i * 2) / 32768f;
            }
            int rate = sample.SampleRate;
            return new PcmSound
            {
                Name = name,
                SampleRate = rate,
                Samples = s,
                Loop = sample.Loop,
                // loop start: SampleStart is in 32-bit words (ADPCM: MphRead's LoopStart already converted)
                LoopStart = !sample.Loop ? 0 : Math.Max(0, Math.Min(n - 1, sample.Format == WaveFormat.ADPCM
                    ? sample.LoopStart : (int)sample.SampleStart * (sample.Format == WaveFormat.PCM8 ? 4 : 2))),
                Volume = volume / 127f
            };
        }
    }
}
