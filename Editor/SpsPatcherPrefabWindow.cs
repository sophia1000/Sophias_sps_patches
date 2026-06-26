using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace ContactFix.Editor
{
    internal class SpsPatcherPrefabWindow : EditorWindow
    {
        private const string WindowTitle = "SPS Patcher";

        [SerializeField] private GameObject _avatar;
        [SerializeField] private Vector2 _scroll;
        [SerializeField] private bool _installToAllAvatars;

        private string _prefabsFolder;
        private List<GameObject> _prefabs = new List<GameObject>();
        private List<GameObject> _sceneAvatars = new List<GameObject>();
        private string _status;

        [MenuItem("SPS Patcher/Prefab Installer")]
        public static void ShowWindow()
        {
            var window = GetWindow<SpsPatcherPrefabWindow>(WindowTitle);
            window.minSize = new Vector2(360f, 240f);
            window.RefreshPrefabs();
            window.RefreshSceneAvatars();
            window.TryUseSelectedAvatar(force: true);
            window.Show();
        }

        private void OnEnable()
        {
            RefreshPrefabs();
            RefreshSceneAvatars();
            TryUseSelectedAvatar(force: true);
        }

        private void OnSelectionChange()
        {
            TryUseSelectedAvatar();
            Repaint();
        }

        private void OnHierarchyChange()
        {
            RefreshSceneAvatars();
            TryUseSelectedAvatar();
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Sophia's SPS Patcher", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_installToAllAvatars))
                    _avatar = (GameObject)EditorGUILayout.ObjectField("Avatar", _avatar, typeof(GameObject), true);

                if (GUILayout.Button("Refresh", GUILayout.Width(80f)))
                {
                    RefreshSceneAvatars();
                    TryUseSelectedAvatar(force: true);
                }
            }

            _installToAllAvatars = EditorGUILayout.ToggleLeft($"Install To All Avatars ({_sceneAvatars.Count})", _installToAllAvatars);

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Prefabs", EditorStyles.boldLabel);

            EditorGUILayout.Space(4f);

            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (_prefabs.Count == 0)
            {
                EditorGUILayout.HelpBox("No prefabs were found. Add prefab assets to a Prefabs folder beside this tool.", MessageType.Warning);
            }
            else
            {
                foreach (var prefab in _prefabs)
                {
                    if (prefab == null) continue;

                    if (GUILayout.Button(prefab.name, GUILayout.Height(28f)))
                        InstallPrefab(prefab);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void TryUseSelectedAvatar(bool force = false)
        {
            var selected = Selection.activeGameObject;
            var descriptor = selected != null ? selected.GetComponentInParent<VRCAvatarDescriptor>(true) : null;
            if (descriptor != null)
            {
                _avatar = descriptor.gameObject;
                return;
            }

            if (!force && _avatar != null) return;

            RefreshSceneAvatars();
            if (_sceneAvatars.Count > 0)
                _avatar = _sceneAvatars[0];
        }

        private void RefreshSceneAvatars()
        {
            _sceneAvatars = Resources.FindObjectsOfTypeAll<VRCAvatarDescriptor>()
                .Where(x => x != null && x.gameObject.scene.IsValid() && x.gameObject.scene.isLoaded)
                .Select(x => x.gameObject)
                .Distinct()
                .OrderBy(x => x.name)
                .ToList();
        }

        private void RefreshPrefabs()
        {
            _prefabsFolder = FindPrefabsFolder();
            _prefabs.Clear();

            if (string.IsNullOrEmpty(_prefabsFolder))
            {
                _status = "Could not find a Prefabs folder beside this tool.";
                return;
            }

            _prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { _prefabsFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(Path.GetFileNameWithoutExtension)
                .Select(path => AssetDatabase.LoadAssetAtPath<GameObject>(path))
                .Where(prefab => prefab != null)
                .ToList();

            if (_prefabs.Count == 0)
                _status = "Prefabs folder found, but it has no prefab assets yet.";
            else if (_status != null && (_status.StartsWith("Found ") || _status.Contains("no prefab assets")))
                _status = null;
        }

        private static string FindPrefabsFolder()
        {
            var scriptGuids = AssetDatabase.FindAssets($"{nameof(SpsPatcherPrefabWindow)} t:MonoScript");
            foreach (var guid in scriptGuids)
            {
                var scriptPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(scriptPath)) continue;

                var editorFolder = Path.GetDirectoryName(scriptPath)?.Replace('\\', '/');
                var toolFolder = Path.GetDirectoryName(editorFolder ?? "")?.Replace('\\', '/');
                if (string.IsNullOrEmpty(toolFolder)) continue;

                var prefabsFolder = $"{toolFolder}/Prefabs";
                if (AssetDatabase.IsValidFolder(prefabsFolder))
                    return prefabsFolder;
            }

            return null;
        }

        private void InstallPrefab(GameObject prefab)
        {
            if (prefab == null) return;

            var targets = GetInstallTargets();
            if (targets.Count == 0)
            {
                _status = _installToAllAvatars
                    ? "No VRC avatars were found in the loaded scene."
                    : "Select an avatar or assign one in the Avatar field first.";
                Debug.LogWarning("[SPS Patcher] No avatar target found for prefab install.");
                return;
            }

            var prefabPaths = new HashSet<string>(
                _prefabs.Where(x => x != null).Select(AssetDatabase.GetAssetPath),
                System.StringComparer.OrdinalIgnoreCase);
            var prefabNames = new HashSet<string>(
                _prefabs.Where(x => x != null).Select(x => x.name),
                System.StringComparer.OrdinalIgnoreCase);

            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName($"Install {prefab.name}");

            var added = 0;
            var removed = 0;
            foreach (var avatar in targets)
            {
                if (avatar == null) continue;
                if (InstallPrefabOnAvatar(prefab, avatar, prefabPaths, prefabNames, out var removedForAvatar))
                {
                    added++;
                    removed += removedForAvatar;
                }
            }

            Undo.CollapseUndoOperations(undoGroup);

            _status = $"Added '{prefab.name}' to {added} avatar(s) and removed {removed} existing SPS patcher prefab(s).";
            Debug.Log($"[SPS Patcher] {_status}");
        }

        private bool InstallPrefabOnAvatar(
            GameObject prefab,
            GameObject avatar,
            HashSet<string> prefabPaths,
            HashSet<string> prefabNames,
            out int removed)
        {
            removed = RemoveInstalledPrefabs(avatar, prefabPaths, prefabNames);

            var instance = PrefabUtility.InstantiatePrefab(prefab, avatar.transform) as GameObject;
            if (instance == null)
            {
                Debug.LogError($"[SPS Patcher] Failed to instantiate prefab '{prefab.name}' on '{avatar.name}'.");
                return false;
            }

            instance.name = prefab.name;
            Undo.RegisterCreatedObjectUndo(instance, $"Add {prefab.name}");
            EditorUtility.SetDirty(avatar);
            EditorSceneManager.MarkSceneDirty(avatar.scene);

            return true;
        }

        private List<GameObject> GetInstallTargets()
        {
            if (_avatar == null)
                TryUseSelectedAvatar(force: true);

            if (_installToAllAvatars)
            {
                RefreshSceneAvatars();
                return _sceneAvatars.ToList();
            }

            if (_avatar == null) return new List<GameObject>();

            var descriptor = _avatar.GetComponentInParent<VRCAvatarDescriptor>(true);
            var avatar = descriptor != null ? descriptor.gameObject : _avatar;
            return avatar != null ? new List<GameObject> { avatar } : new List<GameObject>();
        }

        private static int RemoveInstalledPrefabs(GameObject avatar, HashSet<string> prefabPaths, HashSet<string> prefabNames)
        {
            var toRemove = new List<GameObject>();
            var transforms = avatar.GetComponentsInChildren<Transform>(true);

            foreach (var transform in transforms)
            {
                var go = transform.gameObject;
                if (go == avatar) continue;
                if (HasAncestorInList(transform, toRemove)) continue;

                var directChildNameMatch = transform.parent == avatar.transform && prefabNames.Contains(CleanCloneName(go.name));
                if (IsKnownPrefabInstanceRoot(go, prefabPaths) || directChildNameMatch)
                    toRemove.Add(go);
            }

            foreach (var go in toRemove)
                Undo.DestroyObjectImmediate(go);

            return toRemove.Count;
        }

        private static bool IsKnownPrefabInstanceRoot(GameObject go, HashSet<string> prefabPaths)
        {
            if (PrefabUtility.GetNearestPrefabInstanceRoot(go) != go) return false;

            var source = PrefabUtility.GetCorrespondingObjectFromSource(go);
            if (source == null) return false;

            var path = AssetDatabase.GetAssetPath(source);
            return !string.IsNullOrEmpty(path) && prefabPaths.Contains(path);
        }

        private static bool HasAncestorInList(Transform transform, List<GameObject> objects)
        {
            var parent = transform.parent;
            while (parent != null)
            {
                if (objects.Contains(parent.gameObject))
                    return true;
                parent = parent.parent;
            }

            return false;
        }

        private static string CleanCloneName(string name)
        {
            return (name ?? "").Replace("(Clone)", "").Trim();
        }
    }
}
