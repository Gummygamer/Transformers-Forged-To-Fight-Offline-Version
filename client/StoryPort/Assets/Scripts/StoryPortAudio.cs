using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace StoryPort
{
    // Plays the locally extracted 9.2 music, UI and combat clips from
    // Resources/StoryPort/Audio. Missing clips are silent, never errors.
    public class StoryPortAudio : MonoBehaviour
    {
        AudioSource music;
        AudioSource effects;
        string currentMusic = "";
        readonly Dictionary<string, AudioClip> uiClips = new Dictionary<string, AudioClip>();
        readonly Dictionary<string, Dictionary<string, List<AudioClip>>> soundSets = new Dictionary<string, Dictionary<string, List<AudioClip>>>();

        void Awake()
        {
            music = gameObject.AddComponent<AudioSource>();
            music.loop = true;
            music.playOnAwake = false;
            music.volume = .5f;
            effects = gameObject.AddComponent<AudioSource>();
            effects.playOnAwake = false;
        }

        public void Music(string name, bool loop = true)
        {
            if (music == null || name == currentMusic) return;
            currentMusic = name;
            music.Stop();
            if (string.IsNullOrEmpty(name)) return;
            var clip = Ui(name);
            if (clip == null) return;
            music.clip = clip;
            music.loop = loop;
            music.Play();
        }

        public void Sfx(string name, float volume = .8f)
        {
            var clip = Ui(name);
            if (clip != null && effects != null) effects.PlayOneShot(clip, volume);
        }

        AudioClip Ui(string name)
        {
            if (!uiClips.TryGetValue(name, out var clip))
            {
                clip = Resources.Load<AudioClip>("StoryPort/Audio/UI/" + name);
                uiClips[name] = clip;
            }
            return clip;
        }

        // Combat cue for a fighter: kind is e.g. "attack_2", "attack_hit_2",
        // "hit_react_light", "block_react", "dash", "dodge", "knockout". A
        // numbered kind falls back to its un-numbered family.
        public void Combat(string botKey, string kind, float volume = .9f)
        {
            var set = SoundSetFor(botKey);
            var clips = Load(set);
            if (!clips.TryGetValue(kind, out var options))
            {
                var family = Regex.Replace(kind, "_\\d+$", "");
                if (!clips.TryGetValue(family, out options)) return;
            }
            if (options.Count == 0 || effects == null) return;
            effects.PlayOneShot(options[UnityEngine.Random.Range(0, options.Count)], volume);
        }

        public void Preload(string botKey) { Load(SoundSetFor(botKey)); }

        Dictionary<string, List<AudioClip>> Load(string set)
        {
            if (soundSets.TryGetValue(set, out var cached)) return cached;
            var map = new Dictionary<string, List<AudioClip>>();
            foreach (var clip in Resources.LoadAll<AudioClip>("StoryPort/Audio/Char/" + set))
            {
                var name = clip.name.StartsWith(set + "_", StringComparison.Ordinal) ? clip.name.Substring(set.Length + 1) : clip.name;
                // "attack_2_c" -> "attack_2" and "attack"; "damage_1b" -> "damage".
                var step = Regex.Replace(Regex.Replace(name, "_[a-z]$", ""), "(\\d)[a-z]$", "$1");
                var family = Regex.Replace(step, "_\\d+$", "");
                Add(map, step, clip);
                if (family != step) Add(map, family, clip);
            }
            soundSets[set] = map;
            return map;
        }

        static void Add(Dictionary<string, List<AudioClip>> map, string key, AudioClip clip)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<AudioClip>();
            list.Add(clip);
        }

        // Sound set per fighter, following each bot's build: film bots use the
        // cinematic set, Generations toys the generations set, and so on.
        public static string SoundSetFor(string botKey)
        {
            var key = (botKey ?? "").ToLowerInvariant();
            if (key.Contains("bludgeon")) return "swords";
            if (key.Contains("_cin_")) return "cinematic";
            if (key.Contains("optimus")) return "generations";
            if (key.Contains("kickback")) return "feral";
            if (key.Contains("starscream")) return "tactition";
            if (key.Contains("bumblebee") || key.Contains("jazz") || key.Contains("mirage")) return "agile";
            return "brawler";
        }
    }
}
