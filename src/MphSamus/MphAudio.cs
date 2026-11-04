// MphAudio - plays MPH samples through ULTRAKILL's SFX mixer group (so its volume slider applies).
// One AudioSource per voice; looping samples (charge hum) play the intro then loop from their loop point.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace MphSamus
{
    public sealed class MphAudio : MonoBehaviour
    {
        private readonly List<AudioSource> _voices = new List<AudioSource>();
        private readonly Dictionary<string, AudioSource> _loops = new Dictionary<string, AudioSource>();
        private AudioMixerGroup _group;
        public AudioMixerGroup Group => _group;   // ULTRAKILL's SFX mixer (volume slider, underwater muffle)

        private void Awake()
        {
            _group = MonoSingleton<AudioMixerController>.Instance != null ? MonoSingleton<AudioMixerController>.Instance.allGroup : null;
        }

        private AudioSource Voice()
        {
            foreach (AudioSource s in _voices)
            {
                if (!s.isPlaying && !_loops.ContainsValue(s)) { return s; }
            }
            var src = gameObject.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0;
            src.outputAudioMixerGroup = _group;
            _voices.Add(src);
            return src;
        }

        // volume: 0..1 on top of the sample's SNDTBLS volume (raw: the volume as is - DGN sounds carry their own)
        public void Play(string name, float volume = 1, float pitch = 1, bool raw = false)
        {
            AudioClip c = MphAssets.Clip(name, out float v, out _);
            if (c == null) { return; }
            AudioSource s = Voice();
            s.clip = c;
            s.loop = false;
            s.pitch = pitch;
            s.volume = (raw ? 1 : v) * volume * Plugin.SfxVolume.Value;
            s.Play();
        }

        // 3D one-shot at a point (beam hits, pickups)
        public void PlayAt(string name, Vector3 pos, float volume = 1)
        {
            AudioClip c = MphAssets.Clip(name, out float v, out _);
            if (c == null) { return; }
            var go = new GameObject("mph_sfx_" + name);
            go.transform.position = pos;
            var s = go.AddComponent<AudioSource>();
            s.clip = c;
            s.outputAudioMixerGroup = _group;
            s.spatialBlend = 1;
            s.minDistance = 8;
            s.maxDistance = 120;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.volume = v * volume * Plugin.SfxVolume.Value;
            s.Play();
            Destroy(go, c.length + 0.1f);
        }

        public void StartLoop(string name, float volume = 1)
        {
            if (_loops.ContainsKey(name)) { return; }
            AudioClip c = MphAssets.Clip(name, out float v, out float loopStart);
            if (c == null) { return; }
            AudioSource s = Voice();
            s.clip = c;
            s.loop = loopStart <= 0;   // with a loop point: restarted from it in Update
            s.pitch = 1;
            s.volume = v * volume * Plugin.SfxVolume.Value;
            s.Play();
            _loops[name] = s;
            _loopStart[name] = loopStart;
        }

        private readonly Dictionary<string, float> _loopStart = new Dictionary<string, float>();

        // a loop whose volume/pitch follow a value each step (Morph Ball roll); volume <= 0 stops it
        public void SetLoop(string name, float volume, float pitch, bool raw = false)
        {
            if (volume <= 0)
            {
                StopLoop(name);
                return;
            }
            StartLoop(name, volume);
            if (_loops.TryGetValue(name, out AudioSource s))
            {
                MphAssets.Clip(name, out float v, out _);
                s.volume = (raw ? 1 : v) * volume * Plugin.SfxVolume.Value;
                s.pitch = pitch;
            }
        }

        public void StopLoop(string name)
        {
            if (_loops.TryGetValue(name, out AudioSource s))
            {
                s.Stop();
                s.loop = false;
                _loops.Remove(name);
            }
        }

        public void StopAll()
        {
            foreach (AudioSource s in _voices) { s.Stop(); }
            _loops.Clear();
        }

        private void Update()
        {
            bool paused = Time.timeScale == 0;
            // loop points: after the intro, continue from the sample's loop start instead of 0
            foreach (KeyValuePair<string, AudioSource> kv in _loops)
            {
                float ls = _loopStart.TryGetValue(kv.Key, out float l) ? l : -1;
                AudioSource s = kv.Value;
                if (!paused && ls > 0 && !s.isPlaying && s.clip != null)
                {
                    s.Play();
                    s.time = Mathf.Min(ls, s.clip.length - 0.01f);
                }
            }
            // pause with the game
            foreach (AudioSource s in _voices)
            {
                if (paused && s.isPlaying) { s.Pause(); }
                else if (!paused && !s.isPlaying && s.time > 0 && s.time < (s.clip ? s.clip.length : 0)) { s.UnPause(); }
            }
        }
    }
}
