#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StoryPort.Editor
{
    public static class StoryPortAssetSetup
    {
        const string ResourcesRoot = "Assets/Resources/StoryPort";
        static readonly Dictionary<string, Material> convertedMaterials = new Dictionary<string, Material>();

        [MenuItem("StoryPort/Build Local Asset Catalog")]
        public static void BuildLocalAssetCatalog()
        {
            convertedMaterials.Clear();
            Directory.CreateDirectory(ResourcesRoot);
            Directory.CreateDirectory(ResourcesRoot + "/Materials");
            ImportLocalUiArt();
            CreateStoryBoardGroundMaterial();
            CopyFirst("PrimordialBase", "library_primordial_base", "library_primordial_base");
            CopyBuilding("Buildings/battle_centre", "z_bldg_battle_centre_01");
            CopyBuilding("Buildings/away_team", "z_bldg_away_team_01");
            CopyBuilding("Buildings/alliance_help", "z_bldg_alliance_help_01");
            CopyBuilding("Buildings/crystal_free", "z_bldg_gacha_free_01");
            CopyBuilding("Buildings/crystal_daily", "z_bldg_gacha_daily_01");
            CopyLibraryPiece("StoryBoard/TerrainHex", "library_primordial", "qb_landmass_1x1_01", true);
            CopyLibraryPiece("StoryBoard/TerrainHex02", "library_primordial", "qb_landmass_1x1_02", true);
            CopyLibraryPiece("StoryBoard/TerrainHex03", "library_primordial", "qb_landmass_1x1_03", true);
            CopyLibraryPiece("StoryBoard/TerrainRun", "library_primordial", "qb_landmass_3x9_01", true);
            CopyLibraryPiece("StoryBoard/TerrainWing", "library_primordial", "qb_landmass_3x9_02", true);
            CopyBoardPiece("StoryBoard/QuestHexTile", "library_common", "qb_node_01");
            CopyFirst("PrimordialTerrain", "primordial_timeofday_0_forward", "primordial_timeofday_0_forward");
            // prepare_project.py links ChicagoFightStage from the converted scene bundle.
            CopyFirst("Bots/optimusprime_cin_tf", "optimusprime_cin_tf", "optimusprime_cin_tf");
            CopyFirst("Bots/bludgeon_gs_rd20", "bludgeon_gs_rd20", "bludgeon_gs_rd20");
            CopyFirst("Bots/bumblebee_gs_kabam", "bumblebee_gs_kabam", "bumblebee_gs_kabam");
            CopyFirst("Bots/grindor_cin_rotf", "grindor_cin_rotf", "grindor_cin_rotf");
            CopyFirst("Bots/ironhide_cin_rotf", "ironhide_cin_rotf", "ironhide_cin_rotf");
            CopyFirst("Bots/jazz_gs_twm05", "jazz_gs_twm05", "jazz_gs_twm05");
            CopyFirst("Bots/kickback_gs_kabam", "kickback_gs_kabam", "kickback_gs_kabam");
            CopyFirst("Bots/mirage_gs_deluxe2016", "mirage_gs_deluxe2016", "mirage_gs_deluxe2016");
            CopyFirst("Bots/starscream_gs", "starscream_gs", "starscream_gs");
            AssetDatabase.Refresh();
            ConfigureGameTextures();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var host = new GameObject("StoryPort");
            host.AddComponent<StoryPort.StoryPortBootstrap>();
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), "Assets/StoryPort.unity");
            PlayerSettings.productName = "StoryPort";
            PlayerSettings.companyName = "Offline Preservation";
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "org.storyport.client");
            Debug.Log("StoryPort: created asset aliases and runtime scene. Imported assets remain local-only.");
        }

        static void ImportLocalUiArt()
        {
            foreach (var path in Directory.GetFiles("Assets/Resources/StoryPort", "*.*", SearchOption.AllDirectories))
            {
                if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) continue;
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                importer.SaveAndReimport();
            }
        }

        static void ConfigureGameTextures()
        {
            var normalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var linearPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var materialGuid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/Art92/Material" }))
            {
                var materialPath = AssetDatabase.GUIDToAssetPath(materialGuid);
                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null) continue;
                AddTexturePath(normalPaths, material, "_normal_tex");
                AddTexturePath(normalPaths, material, "_normal2_tex");
                AddTexturePath(linearPaths, material, "_pbr_composite_tex");
                AddTexturePath(linearPaths, material, "_ao_tex");
                AddTexturePath(linearPaths, material, "_metallic_tex");
                AddTexturePath(linearPaths, material, "_roughness_tex");
            }
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Art92/Texture2D" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;
                var name = Path.GetFileNameWithoutExtension(path);
                bool normal = normalPaths.Contains(path) || Regex.IsMatch(name, @"(?:_n|_nm|_normal)(?:_|$)", RegexOptions.IgnoreCase);
                bool packed = name.IndexOf("raoe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              name.IndexOf("pbr_composite", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              linearPaths.Contains(path);
                var desiredType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                var desiredSrgb = !normal && !packed;
                if (importer.textureType == desiredType && importer.sRGBTexture == desiredSrgb) continue;
                importer.textureType = desiredType;
                importer.sRGBTexture = desiredSrgb;
                importer.SaveAndReimport();
            }
        }

        static void AddTexturePath(HashSet<string> paths, Material material, string property)
        {
            var texture = material.GetTexture(property);
            if (texture == null) return;
            var path = AssetDatabase.GetAssetPath(texture);
            if (!string.IsNullOrEmpty(path)) paths.Add(path);
        }

        static void CreateStoryBoardGroundMaterial()
        {
            var textures = AssetDatabase.FindAssets("qb_primordial_ground_a t:Texture2D", new[] { "Assets/Art92/Texture2D" });
            if (textures.Length == 0)
            {
                Debug.LogWarning("StoryPort: missing local 9.2 primordial ground texture");
                return;
            }
            var albedoPath = AssetDatabase.GUIDToAssetPath(textures[0]);
            var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath);
            if (albedo == null) return;
            var importer = AssetImporter.GetAtPath(albedoPath) as TextureImporter;
            if (importer != null && importer.wrapMode != TextureWrapMode.Repeat)
            {
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.SaveAndReimport();
            }

            var shader = Shader.Find("Standard");
            if (shader == null) return;
            var material = new Material(shader) { name = "PrimordialStoryBoardGround" };
            material.SetTexture("_MainTex", albedo);
            material.SetTextureScale("_MainTex", new Vector2(4f, 4f));
            material.color = new Color(.37f, .39f, .32f, 1f);
            material.SetFloat("_Metallic", .05f);
            material.SetFloat("_Glossiness", .12f);
            var normals = AssetDatabase.FindAssets("qb_primordial_ground_n t:Texture2D", new[] { "Assets/Art92/Texture2D" });
            if (normals.Length > 0)
            {
                var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(normals[0]));
                if (normal != null)
                {
                    material.SetTexture("_BumpMap", normal);
                    material.SetTextureScale("_BumpMap", new Vector2(4f, 4f));
                    material.EnableKeyword("_NORMALMAP");
                }
            }

            var path = ResourcesRoot + "/StoryBoard/PrimordialGround.mat";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(material, path);
            Debug.Log("StoryPort: created local story-board material from " + albedoPath);
        }

        public static void InspectLocalAssets()
        {
            foreach (var filter in new[] { "library_primordial_base", "primordial_timeofday_0_forward", "chicago_timeofday_0_forward", "OptimusPrime_Cin_TF", "Bludgeon_GS_RD20" })
            {
                foreach (var guid in AssetDatabase.FindAssets(filter + " t:Prefab"))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var prefab = PrefabUtility.LoadPrefabContents(path);
                    var renderers = prefab.GetComponentsInChildren<Renderer>(true);
                    var bounds = new Bounds(prefab.transform.position, Vector3.zero);
                    foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
                    Debug.Log("StoryPort inspect prefab " + path + " renderers=" + renderers.Length + " bounds=" + bounds);
                    foreach (var renderer in renderers)
                    {
                        foreach (var material in renderer.sharedMaterials)
                        {
                            if (material == null) continue;
                            var baseTexture = material.GetTexture("_base_tex");
                            Debug.Log("StoryPort renderer " + renderer.name + " enabled=" + renderer.enabled + " active=" + renderer.gameObject.activeInHierarchy + " material=" + material.name + " shader=" + (material.shader != null ? material.shader.name : "MISSING") + " baseTex=" + (baseTexture != null ? AssetDatabase.GetAssetPath(baseTexture) : "none") + " mainTex=" + (material.mainTexture != null ? AssetDatabase.GetAssetPath(material.mainTexture) : "none"));
                        }
                    }
                    PrefabUtility.UnloadPrefabContents(prefab);
                    break;
                }
            }
            var meshes = AssetDatabase.FindAssets("qb_landmass_ t:Mesh");
            foreach (var guid in meshes)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (mesh != null && (mesh.name.Contains("primordial") || mesh.name.Contains("BlankTerrain")))
                    Debug.Log("StoryPort inspect mesh " + mesh.name + " vertices=" + mesh.vertexCount + " bounds=" + mesh.bounds + " path=" + path);
            }
        }

        static void CopyFirst(string target, string query, string expected)
        {
            var hits = AssetDatabase.FindAssets(query + " t:Prefab");
            foreach (var guid in hits)
            {
                var sourcePath = AssetDatabase.GUIDToAssetPath(guid);
                var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
                if (source == null || !source.name.Contains(expected, StringComparison.OrdinalIgnoreCase)) continue;
                var destination = ResourcesRoot + "/" + target + ".prefab";
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                var contents = PrefabUtility.LoadPrefabContents(sourcePath);
                foreach (var transform in contents.GetComponentsInChildren<Transform>(true))
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(transform.gameObject);
                foreach (var renderer in contents.GetComponentsInChildren<Renderer>(true))
                {
                    var sourceMaterials = renderer.sharedMaterials;
                    for (var i = 0; i < sourceMaterials.Length; i++)
                        sourceMaterials[i] = ConvertMaterial(sourceMaterials[i]);
                    renderer.sharedMaterials = sourceMaterials;
                }
                PrefabUtility.SaveAsPrefabAsset(contents, destination);
                PrefabUtility.UnloadPrefabContents(contents);
                Debug.Log("StoryPort: aliased " + sourcePath + " -> " + destination);
                return;
            }
            Debug.LogWarning("StoryPort: no prefab found for '" + query + "'. Import the matching local 9.2 bundle first.");
        }

        static void CopyBoardPiece(string target, string libraryQuery, string childName)
        {
            CopyLibraryPiece(target, libraryQuery, childName, false);
        }

        static void CopyBuilding(string target, string childName)
        {
            CopyLibraryPiece(target, "library_buildings", childName, true);
        }

        static void CopyLibraryPiece(string target, string libraryQuery, string childName, bool retainBlankTerrain)
        {
            foreach (var guid in AssetDatabase.FindAssets(libraryQuery + " t:Prefab"))
            {
                var sourcePath = AssetDatabase.GUIDToAssetPath(guid);
                if (!sourcePath.EndsWith("/" + libraryQuery + ".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                var contents = PrefabUtility.LoadPrefabContents(sourcePath);
                Material primordialTerrain = AssetDatabase.LoadAssetAtPath<Material>(ResourcesRoot + "/StoryBoard/PrimordialGround.mat");
                foreach (var renderer in contents.GetComponentsInChildren<Renderer>(true))
                    if (primordialTerrain == null && renderer.name == "qb_primordial_terrainblend_01" && renderer.sharedMaterial != null)
                    {
                        primordialTerrain = ConvertMaterial(renderer.sharedMaterial);
                        break;
                    }
                Transform match = null;
                foreach (var candidate in contents.GetComponentsInChildren<Transform>(true))
                    if (candidate.name.Equals(childName, StringComparison.Ordinal)) { match = candidate; break; }
                if (match == null)
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                    continue;
                }

                // Extract just the authored terrain module from the local 9.2
                // library, retaining mesh and texture references without
                // bringing the complete asset library into the runtime scene.
                var piece = UnityEngine.Object.Instantiate(match.gameObject);
                piece.name = childName;
                piece.transform.SetParent(null, false);
                piece.transform.localPosition = Vector3.zero;
                piece.transform.localRotation = Quaternion.identity;
                piece.transform.localScale = Vector3.one;
                if (!retainBlankTerrain)
                    foreach (var transform in piece.GetComponentsInChildren<Transform>(true))
                        if (transform.name == "BlankTerrain") UnityEngine.Object.DestroyImmediate(transform.gameObject);
                foreach (var transform in piece.GetComponentsInChildren<Transform>(true))
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(transform.gameObject);
                foreach (var renderer in piece.GetComponentsInChildren<Renderer>(true))
                {
                    var materials = renderer.sharedMaterials;
                    for (var i = 0; i < materials.Length; i++)
                        materials[i] = renderer.name == "BlankTerrain" && primordialTerrain != null
                            ? primordialTerrain
                            : ConvertMaterial(materials[i]);
                    renderer.sharedMaterials = materials;
                }

                var destination = ResourcesRoot + "/" + target + ".prefab";
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                PrefabUtility.SaveAsPrefabAsset(piece, destination);
                UnityEngine.Object.DestroyImmediate(piece);
                PrefabUtility.UnloadPrefabContents(contents);
                Debug.Log("StoryPort: extracted local board module " + childName + " -> " + destination);
                return;
            }
            Debug.LogWarning("StoryPort: board module not found: " + childName + " in " + libraryQuery);
        }

        static Material ConvertMaterial(Material source)
        {
            if (source == null) return null;
            var sourcePath = AssetDatabase.GetAssetPath(source);
            if (!string.IsNullOrEmpty(sourcePath) && convertedMaterials.TryGetValue(sourcePath, out var existing)) return existing;

            var shader = Shader.Find("StoryPort/EBPBR");
            if (shader == null) shader = source.shader != null && source.shader.name.IndexOf("Sky", StringComparison.OrdinalIgnoreCase) >= 0
                ? Shader.Find("Unlit/Texture")
                : Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            var converted = new Material(shader) { name = source.name + "_StoryPort" };

            var baseTexture = ReadTextureProperty(source, sourcePath, "_base_tex");
            if (baseTexture == null) baseTexture = source.mainTexture;
            string baseProperty = converted.HasProperty("_base_tex") ? "_base_tex" : "_MainTex";
            if (baseTexture != null && converted.HasProperty(baseProperty))
            {
                converted.SetTexture(baseProperty, baseTexture);
                try
                {
                    converted.SetTextureScale(baseProperty, source.GetTextureScale("_base_tex"));
                    converted.SetTextureOffset(baseProperty, source.GetTextureOffset("_base_tex"));
                }
                catch { }
            }
            // Decompiled Unity 2020 materials can resolve to InternalErrorShader
            // while retaining their texture properties. GetColor("_Color") on
            // that shader returns transparent black, which tints every bot
            // texture black. Only keep a tint when the source actually has one.
            var tint = Color.white;
            var sourcePathForTint = AssetDatabase.GetAssetPath(source);
            if (!TryReadSerializedColor(sourcePathForTint, "_base_col", out tint) &&
                !TryReadSerializedColor(sourcePathForTint, "_Color", out tint))
            {
                try
                {
                    var candidate = source.GetColor("_Color");
                    if (baseTexture == null ||
                        (candidate.maxColorComponent > .001f && candidate.a > .001f))
                        tint = candidate;
                }
                catch { }
            }
            if (baseTexture != null && tint.a <= .001f) tint = Color.white;
            if (converted.HasProperty("_base_col")) converted.SetColor("_base_col", tint);
            else converted.color = tint;

            var normal = ReadTextureProperty(source, sourcePath, "_normal_tex");
            string normalProperty = converted.HasProperty("_normal_tex") ? "_normal_tex" : "_BumpMap";
            if (normal != null && converted.HasProperty(normalProperty))
            {
                converted.SetTexture(normalProperty, normal);
                try
                {
                    converted.SetTextureScale(normalProperty, source.GetTextureScale("_normal_tex"));
                    converted.SetTextureOffset(normalProperty, source.GetTextureOffset("_normal_tex"));
                }
                catch { }
                if (converted.HasProperty("_BumpMap")) converted.EnableKeyword("_NORMALMAP");
            }
            var composite = ReadTextureProperty(source, sourcePath, "_pbr_composite_tex");
            if (composite != null && converted.HasProperty("_pbr_composite_tex"))
            {
                converted.SetTexture("_pbr_composite_tex", composite);
                try
                {
                    converted.SetTextureScale("_pbr_composite_tex", source.GetTextureScale("_pbr_composite_tex"));
                    converted.SetTextureOffset("_pbr_composite_tex", source.GetTextureOffset("_pbr_composite_tex"));
                }
                catch { }
                converted.SetFloat("_use_pbr_composite", 1f);
            }
            var ao = ReadTextureProperty(source, sourcePath, "_ao_tex");
            if (ao != null && converted.HasProperty("_ao_tex"))
            {
                converted.SetTexture("_ao_tex", ao);
                CopyTextureTransform(source, converted, "_ao_tex", "_ao_tex");
            }
            var metallicMap = ReadTextureProperty(source, sourcePath, "_metallic_tex");
            if (metallicMap != null && converted.HasProperty("_metallic_tex"))
            {
                converted.SetTexture("_metallic_tex", metallicMap);
                CopyTextureTransform(source, converted, "_metallic_tex", "_metallic_tex");
                converted.SetFloat("_use_metallic_tex", 1f);
            }
            var roughnessMap = ReadTextureProperty(source, sourcePath, "_roughness_tex");
            if (roughnessMap != null && converted.HasProperty("_roughness_tex"))
            {
                converted.SetTexture("_roughness_tex", roughnessMap);
                CopyTextureTransform(source, converted, "_roughness_tex", "_roughness_tex");
                converted.SetFloat("_use_roughness_tex", 1f);
            }
            var emissive = ReadTextureProperty(source, sourcePath, "_emissive_tex");
            string emissiveProperty = converted.HasProperty("_emissive_tex") ? "_emissive_tex" : "_EmissionMap";
            if (emissive != null && converted.HasProperty(emissiveProperty))
            {
                converted.SetTexture(emissiveProperty, emissive);
                CopyTextureTransform(source, converted, "_emissive_tex", emissiveProperty);
                if (converted.HasProperty("_EmissionMap"))
                {
                    converted.SetColor("_EmissionColor", source.GetColor("_EmissionColor"));
                    converted.EnableKeyword("_EMISSION");
                }
            }
            if (converted.HasProperty("_metallic_range"))
            {
                float metallic = 0;
                try { metallic = source.GetFloat("_metallic_range"); } catch { }
                converted.SetFloat("_metallic_range", Mathf.Clamp01(metallic));
            }
            if (converted.HasProperty("_roughness_range"))
            {
                float roughness = .5f;
                try { roughness = source.GetFloat("_roughness_range"); } catch { }
                converted.SetFloat("_roughness_range", Mathf.Clamp01(roughness));
            }
            if (converted.HasProperty("_emissive_range"))
            {
                float intensity = 0;
                try { intensity = source.GetFloat("_emissive_range"); } catch { }
                converted.SetFloat("_emissive_range", Mathf.Clamp(intensity, 0f, 8f));
            }
            if (converted.HasProperty("_Metallic"))
            {
                converted.SetFloat("_Metallic", Mathf.Clamp01(source.GetFloat("_metallic_range")));
            }
            if (converted.HasProperty("_Glossiness"))
            {
                converted.SetFloat("_Glossiness", Mathf.Clamp01(1f - source.GetFloat("_roughness_range")));
            }

            if (!string.IsNullOrEmpty(sourcePath))
            {
                var guid = AssetDatabase.AssetPathToGUID(sourcePath);
                var destination = ResourcesRoot + "/Materials/" + guid + ".mat";
                var stale = AssetDatabase.LoadAssetAtPath<Material>(destination);
                if (stale != null) AssetDatabase.DeleteAsset(destination);
                AssetDatabase.CreateAsset(converted, destination);
                convertedMaterials[sourcePath] = converted;
            }
            return converted;
        }

        static void CopyTextureTransform(Material source, Material target, string sourceProperty, string targetProperty)
        {
            try
            {
                target.SetTextureScale(targetProperty, source.GetTextureScale(sourceProperty));
                target.SetTextureOffset(targetProperty, source.GetTextureOffset(sourceProperty));
            }
            catch { }
        }

        static Texture ReadTextureProperty(Material material, string materialPath, string property)
        {
            Texture texture = null;
            try { texture = material.GetTexture(property); } catch { }
            if (texture != null || string.IsNullOrEmpty(materialPath) || !File.Exists(materialPath)) return texture;

            // Unity's importer may assign InternalErrorShader to these 2020
            // materials. In that state GetTexture(property) returns null even
            // though the texture GUID is still serialized in the .mat file.
            var text = File.ReadAllText(materialPath);
            var match = Regex.Match(text,
                @"(?ms)^\s*" + Regex.Escape(property) +
                @":\s*\n\s*m_Texture:\s*\{fileID:\s*\d+,\s*guid:\s*([0-9a-f]{32}),\s*type:\s*\d+\}");
            if (!match.Success) return null;
            var texturePath = AssetDatabase.GUIDToAssetPath(match.Groups[1].Value);
            return string.IsNullOrEmpty(texturePath) ? null : AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
        }

        static bool TryReadSerializedColor(string materialPath, string property, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrEmpty(materialPath) || !File.Exists(materialPath)) return false;
            var text = File.ReadAllText(materialPath);
            var match = Regex.Match(text,
                @"(?m)^\s*" + Regex.Escape(property) +
                @":\s*\{r:\s*([^,]+),\s*g:\s*([^,]+),\s*b:\s*([^,]+),\s*a:\s*([^}]+)\}");
            if (!match.Success) return false;
            var values = new float[4];
            for (var i = 0; i < values.Length; i++)
                if (!float.TryParse(match.Groups[i + 1].Value.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out values[i])) return false;
            color = new Color(values[0], values[1], values[2], values[3]);
            return true;
        }
    }
}
#endif
