#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace StoryPort.Editor
{
    // The extracted 9.2 clips are raw WAV. Compress them on import so ~950
    // clips do not inflate the APK, and stream the long music loops.
    public class StoryPortAudioImport : AssetPostprocessor
    {
        void OnPreprocessAudio()
        {
            if (!assetPath.Contains("/Resources/StoryPort/Audio/")) return;
            var importer = (AudioImporter)assetImporter;
            var settings = importer.defaultSampleSettings;
            bool music = assetPath.Contains("/UI/music_") || assetPath.EndsWith("_ambience.wav");
            settings.compressionFormat = AudioCompressionFormat.Vorbis;
            settings.quality = music ? .5f : .35f;
            settings.loadType = music ? AudioClipLoadType.Streaming : AudioClipLoadType.CompressedInMemory;
            importer.defaultSampleSettings = settings;
            importer.forceToMono = !music;
            importer.loadInBackground = music;
        }
    }
}
#endif
