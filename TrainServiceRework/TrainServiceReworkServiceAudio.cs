// File: TrainServiceReworkServiceAudio.cs
//
// Immersive Service sounds at the pit stop (3D, heard by every player – driven by the same
// job views the displays use, so multiplayer clients hear exactly what the host does):
//
//   repair          Body / Wheels / Mechanical / Electrical powertrain working   max 20 m
//   sandfilling     Sand working                                                 max 10 m
//   fluidfilling    Fuel / Oil working                                           max 10 m
//   compressor      parallel to sand / fluid filling                             max 20 m
//   electriccharge  Electric charge working                                      max 10 m
//   working_far     whenever the pit stop is working at all                      max 50 m
//   ding            once when a module has finished its work                     max 10 m
//
// The vanilla lever sound of the repair modules ("ServicePoint_Repair_01") is muted while the
// Immersive Service is active – the repair sound now plays during the actual work instead.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Audio;
using DV.ThingTypes;

namespace TrainServiceRework
{
    public static class ImmersiveServiceAudio
    {
        private const string AudioFolder = "Audio";

        private const float RepairDistance = 20f;
        private const float FillDistance = 10f;
        private const float CompressorDistance = 20f;
        private const float ElectricDistance = 10f;
        private const float WorkingDistance = 50f;
        private const float DingDistance = 10f;

        private enum Sound
        {
            None,
            Repair,
            Sand,
            Fluid,
            Electric
        }

        private sealed class StationAudio
        {
            // ONE source per sound type and pit stop – no matter how many modules of that type work
            public readonly Dictionary<Sound, AudioSource?> TypeSources = new Dictionary<Sound, AudioSource?>();
            public AudioSource? Compressor;
            public AudioSource? Working;
        }

        private static readonly Dictionary<PitStopStation, StationAudio> stationAudio = new Dictionary<PitStopStation, StationAudio>();
        private static readonly Dictionary<LocoResourceModule, float> mutedLeverSounds = new Dictionary<LocoResourceModule, float>();

        private static AudioClip? clipRepair;
        private static AudioClip? clipSand;
        private static AudioClip? clipFluid;
        private static AudioClip? clipCompressor;
        private static AudioClip? clipElectric;
        private static AudioClip? clipWorking;
        private static AudioClip? clipDing;
        private static bool clipsLoaded;

        // =====================================================
        // PUBLIC
        // =====================================================

        /// <summary>Called by the visual update for every module (≈5×/s).</summary>
        public static void UpdateStation(PitStopStation station, LocoResourceModule[] modules, bool[] running, bool immersiveEnabled)
        {
            if (station == null)
                return;

            EnsureClips();

            StationAudio audio;

            if (!stationAudio.TryGetValue(station, out audio) || audio == null)
            {
                audio = new StationAudio();
                stationAudio[station] = audio;
            }

            bool anyRunning = false;
            bool compressorNeeded = false;
            AudioMixerGroup? mixer = null;

            // first working module per sound type = where that one sound plays
            Dictionary<Sound, LocoResourceModule> playAt = new Dictionary<Sound, LocoResourceModule>();

            for (int i = 0; i < modules.Length; i++)
            {
                LocoResourceModule module = modules[i];

                if (module == null)
                    continue;

                if (mixer == null)
                    mixer = GetMixer(module);

                Sound sound = GetSound(module.resourceType);

                // vanilla lever sound of the repair modules off while Immersive Service is on
                SetLeverSoundMuted(module, immersiveEnabled && sound == Sound.Repair);

                if (i >= running.Length || !running[i])
                    continue;

                anyRunning = true;

                if (sound == Sound.Sand || sound == Sound.Fluid)
                    compressorNeeded = true;

                if (sound != Sound.None && !playAt.ContainsKey(sound))
                    playAt[sound] = module;
            }

            UpdateTypeSound(audio, Sound.Repair, playAt, mixer);
            UpdateTypeSound(audio, Sound.Sand, playAt, mixer);
            UpdateTypeSound(audio, Sound.Fluid, playAt, mixer);
            UpdateTypeSound(audio, Sound.Electric, playAt, mixer);

            // ---------------- compressor (sand / fluid) ----------------
            if (compressorNeeded)
            {
                if (audio.Compressor == null)
                    audio.Compressor = CreateSource(station.transform, "TSR_ServiceSound_Compressor", clipCompressor, CompressorDistance, mixer);

                Play(audio.Compressor);
            }
            else
            {
                Stop(audio.Compressor);
            }

            // ---------------- pit stop working at all ----------------
            if (anyRunning)
            {
                if (audio.Working == null)
                    audio.Working = CreateSource(station.transform, "TSR_ServiceSound_Working", clipWorking, WorkingDistance, mixer);

                Play(audio.Working);
            }
            else
            {
                Stop(audio.Working);
            }
        }

        private static void UpdateTypeSound(StationAudio audio, Sound sound, Dictionary<Sound, LocoResourceModule> playAt, AudioMixerGroup? mixer)
        {
            AudioSource? source;
            audio.TypeSources.TryGetValue(sound, out source);

            LocoResourceModule? module;

            if (!playAt.TryGetValue(sound, out module) || module == null)
            {
                Stop(source);
                return;
            }

            if (source == null)
            {
                source = CreateSource(module.transform, "TSR_ServiceSound_" + sound, ClipFor(sound), DistanceFor(sound), mixer);
                audio.TypeSources[sound] = source;
            }
            else if (source.transform.parent != module.transform)
            {
                // another module of this type is the one working now → move the sound there
                source.transform.SetParent(module.transform, false);
                source.transform.localPosition = Vector3.zero;
            }

            Play(source);
        }

        /// <summary>One "ding" at the module when it has finished its work.</summary>
        public static void PlayDing(LocoResourceModule module)
        {
            if (module == null)
                return;

            EnsureClips();

            AudioSource? source = CreateSource(module.transform, "TSR_ServiceSound_Ding", clipDing, DingDistance, GetMixer(module));

            if (source == null || source.clip == null)
                return;

            source.loop = false;
            source.Play();

            UnityEngine.Object.Destroy(source.gameObject, source.clip.length + 0.2f);
        }

        /// <summary>Everything silent, vanilla lever sound back (Immersive Service off / station hidden).</summary>
        public static void StopStation(PitStopStation station, LocoResourceModule[] modules)
        {
            if (station == null)
                return;

            for (int i = 0; i < modules.Length; i++)
            {
                if (modules[i] != null)
                    SetLeverSoundMuted(modules[i], false);
            }

            StationAudio audio;

            if (!stationAudio.TryGetValue(station, out audio) || audio == null)
                return;

            foreach (AudioSource? source in audio.TypeSources.Values)
                Stop(source);

            Stop(audio.Compressor);
            Stop(audio.Working);
        }

        /// <summary>World unloaded – the audio objects are destroyed with the scene.</summary>
        public static void Clear()
        {
            stationAudio.Clear();
            mutedLeverSounds.Clear();
        }

        // =====================================================
        // MAPPING
        // =====================================================
        private static Sound GetSound(ResourceType type)
        {
            switch (type)
            {
                case ResourceType.Car_DMG:
                case ResourceType.Wheels_DMG:
                case ResourceType.MechanicalPowertrain_DMG:
                case ResourceType.ElectricalPowertrain_DMG:
                    return Sound.Repair;

                case ResourceType.Sand:
                    return Sound.Sand;

                case ResourceType.Fuel:
                case ResourceType.Oil:
                    return Sound.Fluid;

                case ResourceType.ElectricCharge:
                    return Sound.Electric;

                default:
                    return Sound.None; // water / coal keep their own vanilla animation + sound
            }
        }

        private static AudioClip? ClipFor(Sound sound)
        {
            switch (sound)
            {
                case Sound.Repair: return clipRepair;
                case Sound.Sand: return clipSand;
                case Sound.Fluid: return clipFluid;
                case Sound.Electric: return clipElectric;
                default: return null;
            }
        }

        private static float DistanceFor(Sound sound)
        {
            switch (sound)
            {
                case Sound.Repair: return RepairDistance;
                case Sound.Electric: return ElectricDistance;
                default: return FillDistance;
            }
        }

        // =====================================================
        // VANILLA LEVER SOUND
        // =====================================================
        private static void SetLeverSoundMuted(LocoResourceModule module, bool muted)
        {
            LayeredAudio lever = module.flowAudioLayered;

            if (lever == null)
                return;

            if (muted)
            {
                if (mutedLeverSounds.ContainsKey(module))
                    return;

                mutedLeverSounds[module] = lever.MasterVolume;
                lever.MasterVolume = 0f;
            }
            else
            {
                float original;

                if (!mutedLeverSounds.TryGetValue(module, out original))
                    return;

                mutedLeverSounds.Remove(module);
                lever.MasterVolume = original;
            }
        }

        // =====================================================
        // AUDIO SOURCES
        // =====================================================
        private static AudioMixerGroup? GetMixer(LocoResourceModule module)
        {
            // use the game's own SFX mixer group so the volume settings apply
            if (module.flowAudioLayered != null && module.flowAudioLayered.audioMixerGroup != null)
                return module.flowAudioLayered.audioMixerGroup;

            if (module.plugConnectionAudioLayered != null && module.plugConnectionAudioLayered.audioMixerGroup != null)
                return module.plugConnectionAudioLayered.audioMixerGroup;

            return null;
        }

        private static AudioSource? CreateSource(Transform parent, string name, AudioClip? clip, float maxDistance, AudioMixerGroup? mixer)
        {
            if (clip == null || parent == null)
                return null;

            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;

            AudioSource source = go.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = true;
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 1f;
            source.maxDistance = maxDistance;
            source.dopplerLevel = 0f;
            source.volume = 1f;

            if (mixer != null)
                source.outputAudioMixerGroup = mixer;

            return source;
        }

        private static void Play(AudioSource? source)
        {
            if (source == null || source.isPlaying)
                return;

            // random start so several loops don't sound identical
            if (source.clip != null && source.clip.length > 0.1f)
                source.time = UnityEngine.Random.Range(0f, source.clip.length * 0.9f);

            source.Play();
        }

        private static void Stop(AudioSource? source)
        {
            if (source != null && source.isPlaying)
                source.Stop();
        }

        // =====================================================
        // WAV LOADING (PCM, from <mod>/Audio)
        // =====================================================
        private static void EnsureClips()
        {
            if (clipsLoaded)
                return;

            clipsLoaded = true;

            string folder = Path.Combine(Main.ModPath, AudioFolder);

            clipRepair = LoadWav(folder, "repair.wav");
            clipSand = LoadWav(folder, "sandfilling.wav");
            clipFluid = LoadWav(folder, "fluidfilling.wav");
            clipCompressor = LoadWav(folder, "compressor.wav");
            clipElectric = LoadWav(folder, "electriccharge.wav");
            clipWorking = LoadWav(folder, "working_far.wav");
            clipDing = LoadWav(folder, "ding.wav");
        }

        private static AudioClip? LoadWav(string folder, string fileName)
        {
            string path = Path.Combine(folder, fileName);

            try
            {
                if (!File.Exists(path))
                {
                    Main.LogWarning("IMMERSIVE SERVICE -> sound file missing: " + path);
                    return null;
                }

                byte[] bytes = File.ReadAllBytes(path);

                if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
                {
                    Main.LogWarning("IMMERSIVE SERVICE -> not a WAV file: " + path);
                    return null;
                }

                int format = 0;
                int channels = 0;
                int sampleRate = 0;
                int bits = 0;
                int dataStart = -1;
                int dataLength = 0;
                int position = 12;

                while (position + 8 <= bytes.Length)
                {
                    string chunkId = Encoding.ASCII.GetString(bytes, position, 4);
                    int chunkSize = BitConverter.ToInt32(bytes, position + 4);
                    int body = position + 8;

                    if (chunkId == "fmt " && body + 16 <= bytes.Length)
                    {
                        format = BitConverter.ToUInt16(bytes, body);
                        channels = BitConverter.ToUInt16(bytes, body + 2);
                        sampleRate = BitConverter.ToInt32(bytes, body + 4);
                        bits = BitConverter.ToUInt16(bytes, body + 14);
                    }
                    else if (chunkId == "data")
                    {
                        dataStart = body;
                        dataLength = Math.Min(chunkSize, bytes.Length - body);
                        break;
                    }

                    if (chunkSize < 0)
                        break;

                    position = body + chunkSize + (chunkSize & 1);
                }

                if (format != 1 || channels <= 0 || sampleRate <= 0 || dataStart < 0 || (bits != 8 && bits != 16 && bits != 24 && bits != 32))
                {
                    Main.LogWarning($"IMMERSIVE SERVICE -> unsupported WAV (PCM 8/16/24/32 bit only): {path}");
                    return null;
                }

                int bytesPerSample = bits / 8;
                int sampleCount = dataLength / bytesPerSample;
                int frames = sampleCount / channels;
                sampleCount = frames * channels;

                float[] samples = new float[sampleCount];

                for (int i = 0; i < sampleCount; i++)
                {
                    int offset = dataStart + i * bytesPerSample;

                    switch (bits)
                    {
                        case 8:
                            samples[i] = (bytes[offset] - 128) / 128f;
                            break;
                        case 16:
                            samples[i] = BitConverter.ToInt16(bytes, offset) / 32768f;
                            break;
                        case 24:
                            int value24 = bytes[offset] | (bytes[offset + 1] << 8) | ((sbyte)bytes[offset + 2] << 16);
                            samples[i] = value24 / 8388608f;
                            break;
                        default:
                            samples[i] = BitConverter.ToInt32(bytes, offset) / 2147483648f;
                            break;
                    }
                }

                AudioClip clip = AudioClip.Create("TSR_" + Path.GetFileNameWithoutExtension(fileName), frames, channels, sampleRate, false);
                clip.SetData(samples, 0);

                Main.Log($"IMMERSIVE SERVICE -> sound loaded {fileName} ({channels} ch, {sampleRate} Hz, {clip.length:F1} s)");
                return clip;
            }
            catch (Exception ex)
            {
                Main.LogWarning($"IMMERSIVE SERVICE -> sound {fileName} could not be loaded: {ex.Message}");
                return null;
            }
        }
    }
}
