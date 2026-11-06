// Diagnostic: records the final output of the game (what the listener hears, after every mixer)
// to a WAV file, so that artifacts heard in a raid can be measured offline.
using System;
using System.IO;
using System.Threading;
using UnityEngine;

namespace SoundEngineBackport.LiveEngine
{
    internal sealed class LiveOutputRecorder : MonoBehaviour
    {
        private const float Seconds = 10f;

        private static LiveOutputRecorder _instance;

        private float[] _buffer;
        private int _channels;
        private int _sampleRate;
        private int _written;
        private volatile bool _recording;
        private bool _saving;
        private string _label;

        /// <summary>Starts a recording on the active listener, or reports the one in progress.</summary>
        public static void Start()
        {
            AudioListener listener = FindObjectOfType<AudioListener>();
            if (listener == null)
            {
                Plugin.Log.LogWarning("[record] no active AudioListener");
                return;
            }
            if (_instance == null || _instance.gameObject != listener.gameObject)
            {
                if (_instance != null)
                {
                    Destroy(_instance);
                }
                _instance = listener.gameObject.AddComponent<LiveOutputRecorder>();
            }
            _instance.Begin();
        }

        private void Begin()
        {
            if (_recording || _saving)
            {
                Plugin.Log.LogWarning("[record] a recording is already in progress");
                return;
            }
            _sampleRate = AudioSettings.outputSampleRate;
            _channels = AudioSettings.speakerMode == AudioSpeakerMode.Mono ? 1 : 2;
            _buffer = new float[(int)(_sampleRate * Seconds) * 8];
            _written = 0;
            _label = DateTime.Now.ToString("HHmmss");
            _recording = true;
            Plugin.Log.LogWarning($"[record] t={Time.time:F3} start {_label}, {_sampleRate} Hz, dsp={AudioSettings.dspTime:F3}");
        }

        // audio thread: the final mix, after all mixers, before the device
        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (!_recording)
            {
                return;
            }
            _channels = channels;
            int count = Math.Min(data.Length, _buffer.Length - _written);
            Array.Copy(data, 0, _buffer, _written, count);
            _written += count;
            if (_written >= (int)(_sampleRate * Seconds) * channels || _written >= _buffer.Length)
            {
                _recording = false;
            }
        }

        private void Update()
        {
            if (_recording || _buffer == null || _saving)
            {
                return;
            }
            _saving = true;
            float[] samples = _buffer;
            int length = _written;
            int channels = _channels;
            int rate = _sampleRate;
            string path = Path.Combine(BepInEx.Paths.BepInExRootPath, $"seb_record_{_label}.wav");
            _buffer = null;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Write(path, samples, length, channels, rate);
                    Plugin.Log.LogWarning($"[record] saved {path} ({length / channels / (float)rate:F1} s, {channels} ch)");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[record] could not save {path}: {e.Message}");
                }
                _saving = false;
            });
        }

        // 32-bit float WAV: no conversion, the clipping and the exact values stay visible
        private static void Write(string path, float[] samples, int length, int channels, int rate)
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                int bytes = length * 4;
                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + bytes);
                writer.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)3);
                writer.Write((short)channels);
                writer.Write(rate);
                writer.Write(rate * channels * 4);
                writer.Write((short)(channels * 4));
                writer.Write((short)32);
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(bytes);
                for (int i = 0; i < length; i++)
                {
                    writer.Write(samples[i]);
                }
            }
        }
    }
}
