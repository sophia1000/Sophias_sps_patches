using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Editor.BuildPipeline;
using AvatarParameterDriver = VRC.SDKBase.VRC_AvatarParameterDriver;
using DriverParameter = VRC.SDKBase.VRC_AvatarParameterDriver.Parameter;
using Object = UnityEngine.Object;

namespace ContactFix.Editor
{
    public class ContactFixMergerHook : IVRCSDKPreprocessAvatarCallback
    {
        // After VRCFury (~-10000), before the SDK strips IEditorOnly at -1024.
        public int callbackOrder => -1025;

        public bool OnPreprocessAvatar(GameObject avatar)
        {
            var mergers = avatar.GetComponentsInChildren<ContactFixMerger>(true);
            if (mergers.Length == 0) return true;

            var report = mergers.Any(x => x.debugReport) ? new List<string>() : null;
            Report(report, $"Found {mergers.Length} ContactFixMerger component(s) on avatar '{avatar.name}'.");

            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            var fx = descriptor != null ? GetFxController(descriptor) : null;
            if (fx == null)
            {
                Debug.LogError("[ContactFixMerger] No FX AnimatorController found. Skipped so upload can continue.");
                Report(report, "FAILED: No FX AnimatorController found.");
                ShowDebugReport(report);
                return true;
            }

            foreach (var merger in mergers)
            {
                var source = merger.sourceController as AnimatorController;
                if (source == null)
                {
                    Debug.LogWarning($"[ContactFixMerger] '{merger.name}' has no source AnimatorController assigned; skipped.");
                    Report(report, $"SKIP: '{merger.name}' has no source AnimatorController assigned.");
                    continue;
                }

                try
                {
                    Report(report, $"START: Merge from '{source.name}'.");
                    new MergeJob(fx, avatar, merger.lengthAAPValue, report).Run(source);
                    Report(report, $"DONE: Merge from '{source.name}'.");
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[ContactFixMerger] Job failed and was skipped so upload can continue.\n{ex}");
                    Report(report, $"FAILED: Job threw {ex.GetType().Name}: {ex.Message}");
                }
            }

            EditorUtility.SetDirty(fx);
            try
            {
                AssetDatabase.SaveAssets();
                Report(report, "Saved generated FX assets.");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ContactFixMerger] Could not save generated FX assets; upload will continue.\n{ex}");
                Report(report, $"FAILED: SaveAssets threw {ex.GetType().Name}: {ex.Message}");
            }

            ShowDebugReport(report);
            return true;
        }

        private static AnimatorController GetFxController(VRCAvatarDescriptor descriptor)
        {
            foreach (var layer in descriptor.baseAnimationLayers)
                if (layer.type == VRCAvatarDescriptor.AnimLayerType.FX)
                    return layer.animatorController as AnimatorController;
            return null;
        }

        private static void Report(List<string> report, string message)
        {
            report?.Add(message);
        }

        private static void ShowDebugReport(List<string> report)
        {
            if (report == null || report.Count == 0) return;

            var sb = new StringBuilder();
            sb.AppendLine($"Contact Fix Debug Report ({report.Count} event(s))");
            sb.AppendLine();
            for (int i = 0; i < report.Count; i++)
                sb.AppendLine($"{i + 1}. {report[i]}");

            var text = sb.ToString();
            Debug.Log("[ContactFix Debug Report]\n" + text);
            ContactFixDebugReportWindow.ShowReport(text);
        }
    }

    internal class ContactFixDebugReportWindow : EditorWindow
    {
        [SerializeField] private string _reportText = "";
        [SerializeField] private Vector2 _scroll;
        [SerializeField] private float _fontSize = 12f;

        public static void ShowReport(string text)
        {
            var window = GetWindow<ContactFixDebugReportWindow>("ContactFix Debug");
            window.minSize = new Vector2(420f, 260f);
            window._reportText = text ?? "";
            window._scroll = Vector2.zero;
            window.Show();
            window.Repaint();
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Debug Report", EditorStyles.boldLabel, GUILayout.Width(95f));
                GUILayout.Label("Text Size", GUILayout.Width(55f));
                _fontSize = GUILayout.HorizontalSlider(_fontSize, 9f, 24f, GUILayout.Width(120f));
                GUILayout.Label(Mathf.RoundToInt(_fontSize).ToString(), GUILayout.Width(24f));
                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Copy", EditorStyles.toolbarButton, GUILayout.Width(48f)))
                    EditorGUIUtility.systemCopyBuffer = _reportText ?? "";
            }

            var style = new GUIStyle(EditorStyles.textArea)
            {
                wordWrap = true,
                fontSize = Mathf.RoundToInt(_fontSize)
            };

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            var content = new GUIContent(_reportText ?? "");
            var width = Mathf.Max(position.width - 28f, 100f);
            var height = Mathf.Max(position.height - 30f, style.CalcHeight(content, width));
            EditorGUILayout.SelectableLabel(_reportText ?? "", style, GUILayout.Height(height), GUILayout.ExpandWidth(true));
            EditorGUILayout.EndScrollView();
        }
    }

    [CustomEditor(typeof(ContactFixMerger))]
    internal class ContactFixMergerInspector : UnityEditor.Editor
    {
        private SerializedProperty _lengthAAPValue;
        private SerializedProperty _debugReport;

        private void OnEnable()
        {
            _lengthAAPValue = serializedObject.FindProperty(nameof(ContactFixMerger.lengthAAPValue));
            _debugReport = serializedObject.FindProperty(nameof(ContactFixMerger.debugReport));
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("Sophia's SPS Patcher", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);

            EditorGUILayout.LabelField("Fallback Penetrator Length For Orifices", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_lengthAAPValue);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_debugReport);

            serializedObject.ApplyModifiedProperties();
        }
    }

    internal class MergeJob
    {
        private static readonly Regex VfRegex =
            new Regex(@"^VF\d+_(.*)$", RegexOptions.IgnoreCase);
        private static readonly Regex SpsPlugRegex =
            new Regex(@"^VF\d+_SFFix BakedSpsPlug - Rcv(?<num>\d*)$", RegexOptions.IgnoreCase);
        private static readonly Regex VfDefaultsLayerRegex =
            new Regex(@"^\[VF\d+\]\s*Defaults$", RegexOptions.IgnoreCase);
        private static readonly Regex VfLayerToTreeServiceLayerRegex =
            new Regex(@"^\[VF\d+\]\s*LayerToTreeService$", RegexOptions.IgnoreCase);

        private const string M1 = "vf??_";
        private const string REPATH = "repath_/";
        private const string RESCAN = "rescan_";
        private const string SPS_PLUG_BASE = "SFFix BakedSpsPlug - Rcv";
        private const string SCALE_DETECTOR_RECEIVER_NAME = "Scale Detector (Receiver)";
        private const string SCALE_MOD_PARAM = "Scalespsmod";
        private const string SHARED_SCALE_FACTOR = "ScaleFactor";
        private const string AAP_CONVERT = "aap convert";
        private const float TYPE_LENGTH_FROM = 0.3f;
        private const float LENGTH_SCALE_FACTOR_MAX = 10f;
        private const float FLOAT_MATCH_EPSILON = 0.0001f;

        private readonly AnimatorController _fx;
        private readonly bool _fxIsAsset;
        private readonly GameObject _avatar;
        private readonly AnimatorControllerParameter[] _targetParams;
        private readonly float _lengthAAPValue;
        private readonly List<string> _report;
        private readonly Dictionary<string, string> _targetParamByVfSuffix;

        private Dictionary<string, string> _renameMap;
        private Dictionary<AnimatorStateMachine, AnimatorStateMachine> _smMap;
        private Dictionary<AnimatorState, AnimatorState> _stateMap;
        private Dictionary<string, List<string>> _pathCache;
        private Dictionary<Transform, string> _transformPathCache;
        private Dictionary<AnimationClip, ClipAnalysis> _clipAnalysisCache;
        private Dictionary<string, List<(string path, System.Type type)>> _materialTargetCache;
        private List<string> _avatarPaths;
        private List<(string path, System.Type type, Material[] materials)> _rendererRecords;
        private List<PlugInstance> _instances;
        private Dictionary<string, List<ScalePatchTarget>> _scalePatchTargetsByPath;
        private int _aapConvertExpansions;

        private enum LengthPatchMode
        {
            Off,
            Direct,
            ScaleFactorMultiply
        }

        public MergeJob(AnimatorController fx, GameObject avatar, float lengthAAPValue, List<string> report)
        {
            _fx = fx;
            _avatar = avatar;
            _targetParams = fx.parameters;
            _fxIsAsset = AssetDatabase.Contains(fx);
            _lengthAAPValue = lengthAAPValue;
            _report = report;
            _targetParamByVfSuffix = BuildTargetParamSuffixMap(_targetParams);
            _pathCache = new Dictionary<string, List<string>>();
            _transformPathCache = new Dictionary<Transform, string>();
            _clipAnalysisCache = new Dictionary<AnimationClip, ClipAnalysis>();
            _materialTargetCache = new Dictionary<string, List<(string path, System.Type type)>>();
        }

        public void Run(AnimatorController source)
        {
            var originalLayerCount = _fx.layers.Length;
            _renameMap = BuildVfMarkerMap(source);

            _instances = FindPlugInstances();
            CollectScaleCandidateSources(_instances);
            _instances = _instances.OrderBy(x => x.index).ToList();
            _scalePatchTargetsByPath = BuildScalePatchTargetMap(_instances);

            if (_instances.Count == 0)
            {
                if (DebugEnabled)
                    Warn("No SPS plug contact instances were found; merge skipped.");
                Report("SKIP: No SPS plug instances found.");
                return;
            }

            AddGeneratedParameters(source);
            PatchOriginalFx(originalLayerCount);
            AppendSourceLayers(source);
        }

        // ---------- detect contact instances ----------

        private List<PlugInstance> FindPlugInstances()
        {
            var byIndex = new Dictionary<int, PlugInstance>();
            var componentsScanned = 0;
            var detectorObjects = _avatar.GetComponentsInChildren<Transform>(true)
                .Where(x => string.Equals(x.name, SCALE_DETECTOR_RECEIVER_NAME, System.StringComparison.Ordinal))
                .ToArray();

            if (DebugEnabled)
                Report($"Found {detectorObjects.Length} '{SCALE_DETECTOR_RECEIVER_NAME}' object(s) to inspect for SPS plug contacts.");

            foreach (var detector in detectorObjects)
            {
                var foundOnDetector = false;
                var detectorPath = GetTransformPath(detector);
                foreach (var component in detector.GetComponents<Component>())
                {
                    if (component == null) continue;

                    componentsScanned++;
                    var parameter = FindSpsParameterOnComponent(component, out var stringValues);
                    if (string.IsNullOrEmpty(parameter))
                    {
                        if (DebugEnabled && LooksLikeContactComponent(component))
                            Report($"Contact-like component '{component.GetType().Name}' on '{detectorPath}' had no SPS plug parameter. Strings: {SummarizeStrings(stringValues)}");
                        continue;
                    }

                    foundOnDetector = true;
                    var match = SpsPlugRegex.Match(parameter);

                    var suffix = match.Groups["num"].Value;
                    var index = string.IsNullOrEmpty(suffix) ? 1 : int.Parse(suffix);
                    if (byIndex.ContainsKey(index))
                    {
                        Report($"SKIP: Duplicate SPS plug parameter '{parameter}' on '{component.name}'. First one wins.");
                        continue;
                    }

                    byIndex[index] = new PlugInstance
                    {
                        index = index,
                        suffix = index <= 1 ? "" : index.ToString(),
                        vfParam = parameter,
                        scaleParam = SCALE_MOD_PARAM + (index <= 1 ? "" : index.ToString()),
                        contact = component.transform
                    };
                    Report($"Instance {index}: detected '{parameter}' on component '{component.GetType().Name}' at '{detectorPath}'.");
                }

                if (DebugEnabled && !foundOnDetector)
                    Report($"Scale detector object '{detectorPath}' had no component with an SPS plug parameter.");
            }

            foreach (var parameter in _targetParams.Select(x => x.name))
            {
                var match = SpsPlugRegex.Match(parameter ?? "");
                if (!match.Success) continue;

                var suffix = match.Groups["num"].Value;
                var index = string.IsNullOrEmpty(suffix) ? 1 : int.Parse(suffix);
                if (byIndex.ContainsKey(index)) continue;

                byIndex[index] = new PlugInstance
                {
                    index = index,
                    suffix = index <= 1 ? "" : index.ToString(),
                    vfParam = parameter,
                    scaleParam = SCALE_MOD_PARAM + (index <= 1 ? "" : index.ToString())
                };
                Report($"Instance {index}: detected '{parameter}' from target FX parameters, but no contact component was found.");
            }

            var result = byIndex.Values.OrderBy(x => x.index).ToList();
            Report($"Scanned {componentsScanned} component(s) on '{SCALE_DETECTOR_RECEIVER_NAME}' object(s); found {result.Count} SPS plug instance(s).");
            return result;
        }

        private string FindSpsParameterOnComponent(Component component, out List<string> debugStrings)
        {
            debugStrings = DebugEnabled ? new List<string>() : null;

            var parameter = FindSpsParameterInValues(GetSerializedStringValues(component), debugStrings);
            if (!string.IsNullOrEmpty(parameter)) return parameter;

            return FindSpsParameterInValues(GetReflectedStringValues(component), debugStrings);
        }

        private static string FindSpsParameterInValues(IEnumerable<string> values, List<string> debugStrings)
        {
            foreach (var value in values)
            {
                var trimmed = (value ?? "").Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                debugStrings?.Add(trimmed);
                if (SpsPlugRegex.IsMatch(trimmed))
                    return trimmed;
            }

            return null;
        }

        private static bool LooksLikeContactComponent(Component component)
        {
            if (component == null) return false;
            return component.GetType().Name.IndexOf("Contact", System.StringComparison.OrdinalIgnoreCase) >= 0
                || component.name.IndexOf("Contact", System.StringComparison.OrdinalIgnoreCase) >= 0
                || component.name.IndexOf("Sps", System.StringComparison.OrdinalIgnoreCase) >= 0
                || component.name.IndexOf("Plug", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string SummarizeStrings(List<string> values)
        {
            if (values == null || values.Count == 0) return "(none)";
            return string.Join(" | ", values.Take(12));
        }

        private static IEnumerable<string> GetSerializedStringValues(Component component)
        {
            var values = new List<string>();
            try
            {
                var serialized = new SerializedObject(component);
                var iterator = serialized.GetIterator();
                while (iterator.Next(true))
                {
                    if (iterator.propertyType == SerializedPropertyType.String)
                        values.Add(iterator.stringValue);
                }
            }
            catch
            {
                // Some SDK components expose editor-only data oddly; reflection below is the fallback.
            }

            return values;
        }

        private static IEnumerable<string> GetReflectedStringValues(Component component)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var values = new List<string>();

            for (var type = component.GetType(); type != null && type != typeof(Component); type = type.BaseType)
            {
                foreach (var field in type.GetFields(flags | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType != typeof(string)) continue;
                    try
                    {
                        values.Add(field.GetValue(component) as string);
                    }
                    catch
                    {
                        // Ignore fields Unity/SDK reflection does not allow us to read.
                    }
                }

                foreach (var prop in type.GetProperties(flags | BindingFlags.DeclaredOnly))
                {
                    if (prop.PropertyType != typeof(string) || !prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                    try
                    {
                        values.Add(prop.GetValue(component, null) as string);
                    }
                    catch
                    {
                        // Ignore properties Unity/SDK reflection does not allow us to read.
                    }
                }
            }


            return values;
        }

        // ---------- find candidate parent scale paths ----------

        private void CollectScaleCandidateSources(List<PlugInstance> instances)
        {
            foreach (var inst in instances)
            {
                inst.scaleSources = new List<ScaleSource>();

                if (inst.contact == null)
                {
                    if (DebugEnabled)
                        Report($"SKIP: Instance {inst.index} parameter '{inst.vfParam}' has no contact transform, so parent scale animation cannot be matched.");
                    continue;
                }

                if (DebugEnabled)
                    Report($"Instance {inst.index}: collecting parent scale candidates from contact '{inst.contact.name}' at '{GetTransformPath(inst.contact)}'.");

                for (var t = inst.contact;
                     t != null && t != _avatar.transform;
                     t = t.parent)
                {
                    var path = GetTransformPath(t);
                    inst.scaleSources.Add(new ScaleSource
                    {
                        transform = t,
                        path = path,
                        defaultScale = t.localScale
                    });

                    if (IsArmatureTransform(t))
                    {
                        if (DebugEnabled)
                            Report($"Instance {inst.index}: stopped parent candidate search at armature '{path}'.");
                        break;
                    }
                }

                if (DebugEnabled)
                    Report($"Instance {inst.index}: collected {inst.scaleSources.Count} parent scale candidate path(s).");
            }
        }

        // ---------- parameters ----------

        private void AddGeneratedParameters(AnimatorController source)
        {
            var list = _fx.parameters.ToList();
            var existing = new HashSet<string>(list.Select(x => x.name));

            foreach (var sourceParam in source.parameters)
            {
                if (IsMarker(sourceParam.name, M1)) continue;
                if (IsShortSpsPlugParam(sourceParam.name)) continue;

                AddParameter(list, existing, sourceParam.name, sourceParam);
                foreach (var inst in _instances)
                {
                    var mapped = MapParam(sourceParam.name, inst);
                    if (!string.IsNullOrEmpty(mapped))
                        AddParameter(list, existing, mapped, sourceParam);
                }
            }

            foreach (var inst in _instances)
            {
                AddParameter(list, existing, inst.scaleParam, null, AnimatorControllerParameterType.Float, 1f);
                SetScaleParamDefault(list, inst.scaleParam);
            }

            AddParameter(list, existing, SHARED_SCALE_FACTOR, null, AnimatorControllerParameterType.Float, 1f);
            SetScaleParamDefault(list, SHARED_SCALE_FACTOR);

            _fx.parameters = list.ToArray();
            Report($"Target FX now has {_fx.parameters.Length} parameter definition(s).");
        }

        private static void AddParameter(
            List<AnimatorControllerParameter> list,
            HashSet<string> existing,
            string name,
            AnimatorControllerParameter source,
            AnimatorControllerParameterType fallbackType = AnimatorControllerParameterType.Float,
            float fallbackFloat = 0f)
        {
            if (string.IsNullOrEmpty(name) || existing.Contains(name)) return;

            list.Add(new AnimatorControllerParameter
            {
                name = name,
                type = source != null ? source.type : fallbackType,
                defaultBool = source != null && source.defaultBool,
                defaultFloat = source != null ? source.defaultFloat : fallbackFloat,
                defaultInt = source != null ? source.defaultInt : 0
            });
            existing.Add(name);
        }

        private static void SetScaleParamDefault(List<AnimatorControllerParameter> list, string name)
        {
            var param = list.FirstOrDefault(x => x.name == name);
            if (param == null) return;

            if (param.type == AnimatorControllerParameterType.Float)
                param.defaultFloat = 1f;
            else if (param.type == AnimatorControllerParameterType.Int)
                param.defaultInt = 1;
            else if (param.type == AnimatorControllerParameterType.Bool)
                param.defaultBool = true;
        }

        // ---------- patch original FX ----------

        private void PatchOriginalFx(int originalLayerCount)
        {
            var seenStateMachines = new HashSet<AnimatorStateMachine>();
            var clipMap = new Dictionary<AnimationClip, AnimationClip>();
            var patchedRefs = 0;
            var layerCount = Mathf.Min(originalLayerCount, _fx.layers.Length);
            var lengthLayerModes = GetLengthPatchLayerModes(layerCount);

            for (int i = 0; i < layerCount; i++)
            {
                var sm = _fx.layers[i].stateMachine;
                var lengthMode = lengthLayerModes.TryGetValue(i, out var mode) ? mode : LengthPatchMode.Off;
                if (sm != null)
                {
                    var motionMap = new Dictionary<Motion, Motion>();
                    PatchOriginalStateMachine(sm, seenStateMachines, motionMap, clipMap, lengthMode, ref patchedRefs);
                }
            }

            Report($"Patched {patchedRefs} original-FX reference/curve item(s).");
            ReportUnusedScaleCandidates();
        }

        private void ReportUnusedScaleCandidates()
        {
            if (!DebugEnabled || _scalePatchTargetsByPath == null) return;

            var usedInstances = new HashSet<PlugInstance>();
            foreach (var targets in _scalePatchTargetsByPath.Values)
            {
                foreach (var target in targets)
                    if (target.used)
                        usedInstances.Add(target.instance);
            }

            foreach (var inst in _instances)
            {
                if (inst.contact == null || usedInstances.Contains(inst)) continue;
                Report($"FAILED: Instance {inst.index} had no parent object with localScale curves.");
            }
        }

        private Dictionary<int, LengthPatchMode> GetLengthPatchLayerModes(int layerCount)
        {
            var result = new Dictionary<int, LengthPatchMode>();

            for (int i = 0; i < layerCount; i++)
            {
                if (!VfDefaultsLayerRegex.IsMatch(_fx.layers[i].name ?? "")) continue;
                result[i] = LengthPatchMode.Direct;
                break;
            }

            for (int i = layerCount - 1; i >= 0; i--)
            {
                if (!VfLayerToTreeServiceLayerRegex.IsMatch(_fx.layers[i].name ?? "")) continue;
                result[i] = LengthPatchMode.ScaleFactorMultiply;
                break;
            }

            if (DebugEnabled)
            {
                Report(result.Count > 0
                    ? "VF##_.../Length AAP patch will scan original-FX layer(s): " +
                      string.Join(", ", result.OrderBy(x => x.Key).Select(x => $"'{_fx.layers[x.Key].name}' as {x.Value}"))
                    : "VF##_.../Length AAP patch found no matching original-FX utility layers.");
            }

            return result;
        }

        private void PatchOriginalStateMachine(
            AnimatorStateMachine sm,
            HashSet<AnimatorStateMachine> seenStateMachines,
            Dictionary<Motion, Motion> motionMap,
            Dictionary<AnimationClip, AnimationClip> clipMap,
            LengthPatchMode lengthMode,
            ref int patchedRefs)
        {
            if (sm == null || !seenStateMachines.Add(sm)) return;

            foreach (var child in sm.states)
            {
                var state = child.state;
                if (state == null) continue;

                var changed = false;

                var patchedMotion = PatchOriginalMotion(state.motion, motionMap, clipMap, lengthMode, ref patchedRefs);
                if (patchedMotion != state.motion)
                {
                    state.motion = patchedMotion;
                    changed = true;
                }

                if (changed) EditorUtility.SetDirty(state);
            }

            foreach (var childSm in sm.stateMachines)
            {
                PatchOriginalStateMachine(childSm.stateMachine, seenStateMachines, motionMap, clipMap, lengthMode, ref patchedRefs);
            }
        }

        private Motion PatchOriginalMotion(
            Motion motion,
            Dictionary<Motion, Motion> motionMap,
            Dictionary<AnimationClip, AnimationClip> clipMap,
            LengthPatchMode lengthMode,
            ref int patchedRefs)
        {
            if (motion == null) return null;
            if (motionMap.TryGetValue(motion, out var mapped)) return mapped;

            if (motion is AnimationClip clip)
            {
                if (lengthMode == LengthPatchMode.ScaleFactorMultiply)
                {
                    var lengthBindings = GetLengthPatchBindings(clip);
                    if (lengthBindings.Count > 0)
                    {
                        var scaleFactorTree = BuildScaleFactorLengthTree(clip, lengthBindings, out var keyCount);
                        if (scaleFactorTree != null)
                        {
                            patchedRefs += keyCount;
                            motionMap[motion] = scaleFactorTree;
                            Report($"Replaced clip '{clip.name}' with ScaleFactor /Length blend tree using {keyCount} affected key(s).");
                            return scaleFactorTree;
                        }
                    }
                }

                var patched = PatchOriginalClip(clip, clipMap, lengthMode == LengthPatchMode.Direct, ref patchedRefs);
                motionMap[motion] = patched;
                return patched;
            }

            if (motion is BlendTree tree)
            {
                var changed = false;

                var children = tree.children;
                for (int i = 0; i < children.Length; i++)
                {
                    var child = children[i];

                    var childMotion = PatchOriginalMotion(child.motion, motionMap, clipMap, lengthMode, ref patchedRefs);
                    if (childMotion != child.motion)
                    {
                        child.motion = childMotion;
                        changed = true;
                    }

                    children[i] = child;
                }

                if (changed)
                {
                    tree.children = children;
                    EditorUtility.SetDirty(tree);
                }

                motionMap[motion] = motion;
                return motion;
            }

            motionMap[motion] = motion;
            return motion;
        }

        private BlendTree BuildScaleFactorLengthTree(
            AnimationClip original,
            List<EditorCurveBinding> lengthBindings,
            out int keyCount)
        {
            var zeroClip = CloneClipWithLengthValue(original, lengthBindings, 0f, " ScaleFactor 0", out var zeroKeys);
            var oneClip = CloneClipWithLengthValue(original, lengthBindings, _lengthAAPValue, " ScaleFactor 1", out var oneKeys);
            var tenClip = CloneClipWithLengthValue(original, lengthBindings, _lengthAAPValue * LENGTH_SCALE_FACTOR_MAX, " ScaleFactor 10", out var tenKeys);
            keyCount = Mathf.Max(zeroKeys, Mathf.Max(oneKeys, tenKeys));

            if (keyCount <= 0)
            {
                Object.DestroyImmediate(zeroClip);
                Object.DestroyImmediate(oneClip);
                Object.DestroyImmediate(tenClip);
                return null;
            }

            var tree = new BlendTree
            {
                name = original.name + " Length x ScaleFactor",
                blendType = BlendTreeType.Simple1D,
                blendParameter = SHARED_SCALE_FACTOR,
                useAutomaticThresholds = false,
                minThreshold = 0f,
                maxThreshold = LENGTH_SCALE_FACTOR_MAX
            };

            tree.children = new[]
            {
                new ChildMotion
                {
                    motion = zeroClip,
                    threshold = 0f,
                    timeScale = 1f
                },
                new ChildMotion
                {
                    motion = oneClip,
                    threshold = 1f,
                    timeScale = 1f
                },
                new ChildMotion
                {
                    motion = tenClip,
                    threshold = LENGTH_SCALE_FACTOR_MAX,
                    timeScale = 1f
                }
            };

            AddSub(zeroClip);
            AddSub(oneClip);
            AddSub(tenClip);
            AddSub(tree);
            EditorUtility.SetDirty(zeroClip);
            EditorUtility.SetDirty(oneClip);
            EditorUtility.SetDirty(tenClip);
            EditorUtility.SetDirty(tree);
            return tree;
        }

        private AnimationClip PatchOriginalClip(
            AnimationClip original,
            Dictionary<AnimationClip, AnimationClip> clipMap,
            bool allowLengthPatch,
            ref int patchedRefs)
        {
            if (clipMap.TryGetValue(original, out var existing)) return existing;

            var scaleTargets = GetClipScalePatchTargets(original);
            var needsScalePatch = scaleTargets.Count > 0;
            var lengthBindings = allowLengthPatch ? GetLengthPatchBindings(original) : null;
            var needsLengthPatch = lengthBindings != null && lengthBindings.Count > 0;
            if (!needsScalePatch && !needsLengthPatch)
            {
                clipMap[original] = original;
                return original;
            }

            var clone = Object.Instantiate(original);
            clone.name = original.name;
            AddSub(clone);
            Report($"Patching original-FX clip '{original.name}' for " +
                   (needsScalePatch && needsLengthPatch
                       ? "detected contact parent scale and VF##_.../Length AAP curves."
                       : needsScalePatch
                           ? "detected contact parent scale curves."
                           : "VF##_.../Length AAP curves."));

            if (needsScalePatch)
            {
                foreach (var target in scaleTargets)
                {
                    var curve = BuildNormalizedScaleCurve(original, target.source, out var scalePath);
                    if (curve == null)
                    {
                        if (DebugEnabled)
                            Report($"Clip '{original.name}' did not animate expected parent scale path '{target.source.path}' for instance {target.instance.index}.");
                        continue;
                    }

                    var binding = EditorCurveBinding.FloatCurve("", typeof(Animator), target.instance.scaleParam);
                    AnimationUtility.SetEditorCurve(clone, binding, curve);
                    target.used = true;
                    patchedRefs++;
                    Report($"Added normalized scale curve '{target.instance.scaleParam}' to clip '{original.name}' from '{scalePath}'.");
                }
            }

            if (needsLengthPatch)
                patchedRefs += PatchLengthAAP(clone, lengthBindings);

            EditorUtility.SetDirty(clone);
            clipMap[original] = clone;
            return clone;
        }

        private List<EditorCurveBinding> GetLengthPatchBindings(AnimationClip clip)
        {
            var result = new List<EditorCurveBinding>();
            foreach (var binding in GetClipAnalysis(clip).lengthBindings)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (CurveHasValue(curve, TYPE_LENGTH_FROM))
                    result.Add(binding);
            }
            return result;
        }

        private AnimationClip CloneClipWithLengthValue(
            AnimationClip original,
            List<EditorCurveBinding> lengthBindings,
            float value,
            string suffix,
            out int affectedKeys)
        {
            affectedKeys = 0;
            var clone = Object.Instantiate(original);
            clone.name = original.name + suffix;

            foreach (var binding in lengthBindings)
            {
                var sourceCurve = AnimationUtility.GetEditorCurve(clone, binding);
                var patchedCurve = BuildLengthValueCurve(sourceCurve, value, out var changedKeys);
                if (changedKeys <= 0) continue;

                AnimationUtility.SetEditorCurve(clone, binding, patchedCurve);
                affectedKeys += changedKeys;
            }

            return clone;
        }

        private AnimationCurve BuildLengthValueCurve(AnimationCurve source, float value, out int affectedKeys)
        {
            affectedKeys = 0;
            if (source == null || source.length == 0) return null;

            var keys = source.keys;
            for (int i = 0; i < keys.Length; i++)
            {
                var isAffected = Mathf.Abs(keys[i].value - TYPE_LENGTH_FROM) <= FLOAT_MATCH_EPSILON;
                if (isAffected) affectedKeys++;
                if (isAffected)
                    keys[i].value = value;
            }

            var curve = new AnimationCurve(keys)
            {
                preWrapMode = source.preWrapMode,
                postWrapMode = source.postWrapMode
            };
            return curve;
        }

        private Dictionary<string, List<ScalePatchTarget>> BuildScalePatchTargetMap(List<PlugInstance> instances)
        {
            var result = new Dictionary<string, List<ScalePatchTarget>>();
            foreach (var inst in instances)
            {
                if (inst.scaleSources == null) continue;

                for (int i = 0; i < inst.scaleSources.Count; i++)
                {
                    var source = inst.scaleSources[i];
                    if (source == null || string.IsNullOrEmpty(source.path)) continue;

                    if (!result.TryGetValue(source.path, out var list))
                    {
                        list = new List<ScalePatchTarget>();
                        result[source.path] = list;
                    }

                    list.Add(new ScalePatchTarget
                    {
                        instance = inst,
                        source = source,
                        sourceOrder = i
                    });
                }
            }

            return result;
        }

        private List<ScalePatchTarget> GetClipScalePatchTargets(AnimationClip clip)
        {
            if (_scalePatchTargetsByPath == null || _scalePatchTargetsByPath.Count == 0)
                return new List<ScalePatchTarget>();

            var chosenByInstance = new Dictionary<PlugInstance, ScalePatchTarget>();
            foreach (var binding in GetClipAnalysis(clip).scaleBindings)
            {
                if (!_scalePatchTargetsByPath.TryGetValue(binding.path, out var targets)) continue;

                foreach (var target in targets)
                {
                    if (!chosenByInstance.TryGetValue(target.instance, out var existing)
                        || target.sourceOrder < existing.sourceOrder)
                    {
                        chosenByInstance[target.instance] = target;
                    }
                }
            }

            return chosenByInstance.Values.OrderBy(x => x.instance.index).ToList();
        }

        private AnimationCurve BuildNormalizedScaleCurve(AnimationClip clip, ScaleSource source, out string scalePath)
        {
            scalePath = null;
            if (source == null) return null;

            if (!GetClipAnalysis(clip).scaleBindingsByPath.TryGetValue(source.path, out var sourceBindings))
                return null;

            var candidates = sourceBindings.OrderBy(GetScaleAxis).ToArray();

            AnimationCurve firstCurve = null;
            foreach (var binding in candidates)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.length == 0) continue;

                var normalized = NormalizeScaleCurve(curve, GetDefaultScaleForAxis(source.defaultScale, GetScaleAxis(binding)));
                if (firstCurve == null) firstCurve = normalized;
                if (!HasNonZeroKey(curve)) continue;

                scalePath = source.path;
                return normalized;
            }

            if (firstCurve != null)
            {
                scalePath = source.path;
                return firstCurve;
            }

            return null;
        }

        private static bool IsArmatureTransform(Transform transform)
        {
            if (transform == null) return false;
            return string.Equals(transform.name, "Armature", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(transform.name, "Root", System.StringComparison.OrdinalIgnoreCase);
        }

        private static AnimationCurve NormalizeScaleCurve(AnimationCurve source, float defaultScale)
        {
            if (Mathf.Abs(defaultScale) < 0.0001f) defaultScale = 1f;

            var result = new AnimationCurve
            {
                preWrapMode = source.preWrapMode,
                postWrapMode = source.postWrapMode
            };

            foreach (var sourceKey in source.keys)
            {
                var key = sourceKey;
                key.value = Mathf.Clamp(key.value / defaultScale, 0f, 10f);
                key.inTangent /= defaultScale;
                key.outTangent /= defaultScale;
                result.AddKey(key);
            }

            return result;
        }

        // ---------- clone source into FX ----------

        private void AppendSourceLayers(AnimatorController source)
        {
            _aapConvertExpansions = 0;
            Report($"Appending {source.layers.Length} source layer(s); expanding '{AAP_CONVERT}' for {_instances.Count} instance(s).");

            for (int i = 0; i < source.layers.Length; i++)
            {
                var src = source.layers[i];
                var clonedSM = src.stateMachine != null ? CloneLayerStateMachine(src.stateMachine) : null;

                _fx.AddLayer(new AnimatorControllerLayer
                {
                    name = src.name,
                    defaultWeight = (i == 0 && src.defaultWeight == 0f) ? 1f : src.defaultWeight,
                    blendingMode = src.blendingMode,
                    iKPass = src.iKPass,
                    syncedLayerIndex = src.syncedLayerIndex,
                    syncedLayerAffectsTiming = src.syncedLayerAffectsTiming,
                    avatarMask = src.avatarMask,
                    stateMachine = clonedSM
                });
            }

            if (_aapConvertExpansions == 0 && DebugEnabled)
                Warn($"No child motion named '{AAP_CONVERT}' was found inside the cloned source blend trees.");
        }

        private AnimatorStateMachine CloneLayerStateMachine(AnimatorStateMachine src)
        {
            _smMap = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
            _stateMap = new Dictionary<AnimatorState, AnimatorState>();
            var root = CloneSMStructure(src);
            WireSM(src);
            return root;
        }

        private AnimatorStateMachine CloneSMStructure(AnimatorStateMachine src)
        {
            var sm = new AnimatorStateMachine { name = src.name };
            AddSub(sm);
            _smMap[src] = sm;

            sm.anyStatePosition = src.anyStatePosition;
            sm.entryPosition = src.entryPosition;
            sm.exitPosition = src.exitPosition;
            sm.parentStateMachinePosition = src.parentStateMachinePosition;
            sm.behaviours = CloneBehaviours(src.behaviours, null);

            var states = new List<ChildAnimatorState>();
            foreach (var child in src.states)
            {
                var s = child.state;
                var ns = new AnimatorState
                {
                    name = s.name,
                    speed = s.speed,
                    cycleOffset = s.cycleOffset,
                    mirror = s.mirror,
                    iKOnFeet = s.iKOnFeet,
                    writeDefaultValues = s.writeDefaultValues,
                    tag = s.tag,
                    speedParameterActive = s.speedParameterActive,
                    speedParameter = MapParam(s.speedParameter, null),
                    cycleOffsetParameterActive = s.cycleOffsetParameterActive,
                    cycleOffsetParameter = MapParam(s.cycleOffsetParameter, null),
                    mirrorParameterActive = s.mirrorParameterActive,
                    mirrorParameter = MapParam(s.mirrorParameter, null),
                    timeParameterActive = s.timeParameterActive,
                    timeParameter = MapParam(s.timeParameter, null)
                };
                AddSub(ns);
                ns.motion = CloneMotion(s.motion, null);
                ns.behaviours = CloneBehaviours(s.behaviours, null);
                _stateMap[s] = ns;
                states.Add(new ChildAnimatorState { state = ns, position = child.position });
            }
            sm.states = states.ToArray();

            var childSMs = new List<ChildAnimatorStateMachine>();
            foreach (var child in src.stateMachines)
            {
                var nsm = CloneSMStructure(child.stateMachine);
                childSMs.Add(new ChildAnimatorStateMachine { stateMachine = nsm, position = child.position });
            }
            sm.stateMachines = childSMs.ToArray();

            return sm;
        }

        private void WireSM(AnimatorStateMachine src)
        {
            var sm = _smMap[src];

            if (src.defaultState != null && _stateMap.TryGetValue(src.defaultState, out var defaultState))
                sm.defaultState = defaultState;

            foreach (var child in src.states)
            {
                var ns = _stateMap[child.state];
                var transitions = new List<AnimatorStateTransition>();
                foreach (var transition in child.state.transitions)
                {
                    var clone = CloneStateTransition(transition);
                    if (clone != null) transitions.Add(clone);
                }
                ns.transitions = transitions.ToArray();
            }

            var anyTransitions = new List<AnimatorStateTransition>();
            foreach (var transition in src.anyStateTransitions)
            {
                var clone = CloneStateTransition(transition);
                if (clone != null) anyTransitions.Add(clone);
            }
            sm.anyStateTransitions = anyTransitions.ToArray();

            foreach (var transition in src.entryTransitions)
            {
                var conditions = MapConditions(transition.conditions, null);
                if (conditions == null) continue;

                AnimatorTransition nt = null;
                if (transition.destinationState != null && _stateMap.TryGetValue(transition.destinationState, out var dst))
                    nt = sm.AddEntryTransition(dst);
                else if (transition.destinationStateMachine != null && _smMap.TryGetValue(transition.destinationStateMachine, out var dstSm))
                    nt = sm.AddEntryTransition(dstSm);

                if (nt == null) continue;
                nt.name = transition.name;
                nt.conditions = conditions;
            }

            foreach (var child in src.stateMachines)
            {
                var clonedChild = _smMap[child.stateMachine];
                var transitions = new List<AnimatorTransition>();
                foreach (var transition in src.GetStateMachineTransitions(child.stateMachine))
                {
                    var clone = CloneAnimatorTransition(transition);
                    if (clone != null) transitions.Add(clone);
                }
                sm.SetStateMachineTransitions(clonedChild, transitions.ToArray());
            }

            foreach (var child in src.stateMachines)
                WireSM(child.stateMachine);
        }

        private AnimatorStateTransition CloneStateTransition(AnimatorStateTransition transition)
        {
            var conditions = MapConditions(transition.conditions, null);
            if (conditions == null) return null;

            var nt = new AnimatorStateTransition
            {
                name = transition.name,
                isExit = transition.isExit,
                hasExitTime = transition.hasExitTime,
                exitTime = transition.exitTime,
                hasFixedDuration = transition.hasFixedDuration,
                duration = transition.duration,
                offset = transition.offset,
                interruptionSource = transition.interruptionSource,
                orderedInterruption = transition.orderedInterruption,
                canTransitionToSelf = transition.canTransitionToSelf,
                solo = transition.solo,
                mute = transition.mute,
                conditions = conditions
            };

            if (transition.destinationState != null && _stateMap.TryGetValue(transition.destinationState, out var dst))
                nt.destinationState = dst;
            if (transition.destinationStateMachine != null && _smMap.TryGetValue(transition.destinationStateMachine, out var dstSm))
                nt.destinationStateMachine = dstSm;

            AddSub(nt);
            return nt;
        }

        private AnimatorTransition CloneAnimatorTransition(AnimatorTransition transition)
        {
            var conditions = MapConditions(transition.conditions, null);
            if (conditions == null) return null;

            var nt = new AnimatorTransition
            {
                name = transition.name,
                isExit = transition.isExit,
                solo = transition.solo,
                mute = transition.mute,
                conditions = conditions
            };

            if (transition.destinationState != null && _stateMap.TryGetValue(transition.destinationState, out var dst))
                nt.destinationState = dst;
            if (transition.destinationStateMachine != null && _smMap.TryGetValue(transition.destinationStateMachine, out var dstSm))
                nt.destinationStateMachine = dstSm;

            AddSub(nt);
            return nt;
        }

        private AnimatorCondition[] MapConditions(AnimatorCondition[] source, PlugInstance inst)
        {
            if (source == null) return new AnimatorCondition[0];

            var result = new AnimatorCondition[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                var condition = source[i];
                var mapped = MapParam(condition.parameter, inst);
                if (IsMarker(condition.parameter, M1) && string.IsNullOrEmpty(mapped))
                {
                    if (DebugEnabled)
                        Warn($"Could not resolve transition marker '{condition.parameter}'; transition skipped.");
                    return null;
                }
                condition.parameter = mapped;
                result[i] = condition;
            }
            return result;
        }

        private StateMachineBehaviour[] CloneBehaviours(StateMachineBehaviour[] source, PlugInstance inst)
        {
            if (source == null || source.Length == 0) return new StateMachineBehaviour[0];

            var result = new StateMachineBehaviour[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == null) continue;
                var clone = Object.Instantiate(source[i]);
                var ignoredPatchedRefs = 0;
                RemapSourceDriverBehaviours(new[] { clone }, inst, ref ignoredPatchedRefs);
                AddSub(clone);
                result[i] = clone;
            }
            return result;
        }

        private Motion CloneMotion(Motion motion, PlugInstance inst)
        {
            if (motion == null) return null;

            if (motion is AnimationClip clip)
            {
                var clone = Object.Instantiate(clip);
                clone.name = NameForInstance(clip.name, inst);
                ProcessSourceClip(clone, inst);
                AddSub(clone);
                return clone;
            }

            if (motion is BlendTree tree)
                return CloneBlendTree(tree, inst);

            return motion;
        }

        private BlendTree CloneBlendTree(BlendTree source, PlugInstance inst)
        {
            var clone = new BlendTree
            {
                name = NameForInstance(source.name, inst),
                blendType = source.blendType,
                blendParameter = MapParam(source.blendParameter, inst),
                blendParameterY = MapParam(source.blendParameterY, inst),
                useAutomaticThresholds = source.useAutomaticThresholds,
                minThreshold = source.minThreshold,
                maxThreshold = source.maxThreshold
            };
            AddSub(clone);

            var children = new List<ChildMotion>();
            foreach (var sourceChild in source.children)
            {
                if (inst == null && IsAapConvertMotion(sourceChild.motion))
                {
                    foreach (var plug in _instances)
                    {
                        var child = sourceChild;
                        child.motion = CloneMotion(sourceChild.motion, plug);
                        child.directBlendParameter = sourceChild.directBlendParameter;
                        children.Add(child);
                    }
                    _aapConvertExpansions++;
                    Report($"Expanded '{AAP_CONVERT}' inside direct blend tree '{source.name}' to {_instances.Count} child motion(s).");
                    continue;
                }

                var normalChild = sourceChild;
                normalChild.motion = CloneMotion(sourceChild.motion, inst);
                normalChild.directBlendParameter = MapParam(sourceChild.directBlendParameter, inst);
                children.Add(normalChild);
            }

            clone.children = children.ToArray();
            return clone;
        }

        private void ProcessSourceClip(AnimationClip clip, PlugInstance inst)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);

                if (binding.path.StartsWith(RESCAN))
                {
                    var baseProp = MaterialPropBaseName(binding.propertyName);
                    if (baseProp == null)
                    {
                        AnimationUtility.SetEditorCurve(clip, binding, null);
                        if (DebugEnabled)
                            Warn($"[rescan_] '{binding.propertyName}' on a rescan_ path is not a material property; skipped.");
                        Report($"FAILED: Clip '{clip.name}' rescan_ binding '{binding.propertyName}' is not a material property; removed placeholder curve.");
                        continue;
                    }

                    var scope = binding.path.Substring(RESCAN.Length).Trim('/');
                    var targets = ResolveMaterialTargets(baseProp, scope);
                    AnimationUtility.SetEditorCurve(clip, binding, null);
                    if (targets.Count == 0)
                    {
                        if (DebugEnabled)
                            Warn($"[rescan_] No avatar material has property '{baseProp}'" +
                                 (scope.Length > 0 ? $" under '{scope}'; skipped." : "; skipped."));
                        Report($"SKIP: Clip '{clip.name}' rescan_ placeholder for '{baseProp}' had no targets and was removed.");
                        continue;
                    }

                    foreach (var target in targets)
                    {
                        var newBinding = binding;
                        newBinding.path = target.path;
                        newBinding.type = target.type;
                        AnimationUtility.SetEditorCurve(clip, newBinding, curve);
                    }
                    Report($"SUCCESS: Clip '{clip.name}' expanded rescan_ material curve '{baseProp}' to {targets.Count} renderer target(s).");
                }
                else if (binding.path.StartsWith(REPATH))
                {
                    var targets = ResolvePaths(binding.path.Substring(REPATH.Length));
                    AnimationUtility.SetEditorCurve(clip, binding, null);
                    foreach (var target in targets)
                    {
                        var newBinding = binding;
                        newBinding.path = target;
                        if (IsAnimatorParameterBinding(newBinding))
                            newBinding.propertyName = MapParam(newBinding.propertyName, inst);
                        AnimationUtility.SetEditorCurve(clip, newBinding, curve);
                    }
                    continue;
                }

                if (IsAnimatorParameterBinding(binding))
                {
                    var mapped = MapParam(binding.propertyName, inst);
                    if (mapped != binding.propertyName)
                    {
                        AnimationUtility.SetEditorCurve(clip, binding, null);
                        if (!string.IsNullOrEmpty(mapped))
                        {
                            var newBinding = binding;
                            newBinding.propertyName = mapped;
                            AnimationUtility.SetEditorCurve(clip, newBinding, curve);
                        }
                    }
                }
            }

            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (binding.path.StartsWith(RESCAN))
                {
                    AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
                    if (DebugEnabled)
                    {
                        Warn("[rescan_] rescan_ only supports material-property (float) curves; " +
                             $"skipped object-reference binding '{binding.propertyName}'.");
                    }
                    Report($"FAILED: Clip '{clip.name}' has rescan_ object-reference binding '{binding.propertyName}'; removed placeholder.");
                    continue;
                }

                if (!binding.path.StartsWith(REPATH)) continue;

                var targets = ResolvePaths(binding.path.Substring(REPATH.Length));
                var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
                foreach (var target in targets)
                {
                    var newBinding = binding;
                    newBinding.path = target;
                    AnimationUtility.SetObjectReferenceCurve(clip, newBinding, keys);
                }
            }

            PatchLengthAAP(clip, null);
        }

        private int PatchLengthAAP(AnimationClip clip, IEnumerable<EditorCurveBinding> bindings)
        {
            var curvesChanged = 0;
            var keysChanged = 0;

            foreach (var binding in bindings ?? GetClipAnalysis(clip).lengthBindings)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.length == 0) continue;

                var keys = curve.keys;
                var changed = false;
                for (int i = 0; i < keys.Length; i++)
                {
                    if (Mathf.Abs(keys[i].value - TYPE_LENGTH_FROM) > FLOAT_MATCH_EPSILON)
                        continue;

                    keys[i].value = _lengthAAPValue;
                    changed = true;
                    keysChanged++;
                }

                if (!changed) continue;

                curve.keys = keys;
                AnimationUtility.SetEditorCurve(clip, binding, curve);
                curvesChanged++;
            }

            if (curvesChanged <= 0) return 0;

            EditorUtility.SetDirty(clip);
            Report($"Patched {keysChanged} key(s) on {curvesChanged} '{TYPE_LENGTH_FROM}' VF##_.../Length AAP curve(s) in clip '{clip.name}' to '{_lengthAAPValue}'.");
            return keysChanged;
        }

        // ---------- parameter mapping ----------

        private Dictionary<string, string> BuildVfMarkerMap(AnimatorController source)
        {
            var map = new Dictionary<string, string>();
            foreach (var param in source.parameters)
            {
                if (!IsMarker(param.name, M1)) continue;
                var suffix = param.name.Substring(M1.Length).TrimStart('/');
                var target = ResolveOne(suffix);
                if (!string.IsNullOrEmpty(target))
                    map[param.name] = target;
            }
            Report($"Mapped {map.Count} vf??_ source marker parameter(s).");
            return map;
        }

        private string MapParam(string parameter, PlugInstance inst)
        {
            if (string.IsNullOrEmpty(parameter)) return parameter;

            if (IsMarker(parameter, M1))
            {
                var suffix = parameter.Substring(M1.Length).TrimStart('/');
                if (suffix.StartsWith(SPS_PLUG_BASE, System.StringComparison.Ordinal))
                    return inst != null ? inst.vfParam : ResolveOne(suffix) ?? "";

                if (_renameMap != null && _renameMap.TryGetValue(parameter, out var mapped))
                    return mapped;

                return ResolveOne(suffix) ?? "";
            }

            if (IsShortSpsPlugParam(parameter))
                return parameter;

            if (inst == null || inst.index <= 1 || IsSharedParameter(parameter))
                return parameter;

            return parameter + inst.index;
        }

        private string ResolveOne(string suffix)
        {
            suffix = suffix.TrimStart('/');
            return _targetParamByVfSuffix.TryGetValue(suffix, out var target) ? target : null;
        }

        private static Dictionary<string, string> BuildTargetParamSuffixMap(AnimatorControllerParameter[] parameters)
        {
            var map = new Dictionary<string, string>();
            foreach (var param in parameters)
            {
                if (!TryVfSuffix(param.name, out var suffix)) continue;
                if (!map.ContainsKey(suffix))
                    map.Add(suffix, param.name);
            }
            return map;
        }

        private static bool TryVfSuffix(string name, out string suffix)
        {
            var match = VfRegex.Match(name ?? "");
            suffix = match.Success ? match.Groups[1].Value : null;
            return match.Success;
        }

        private static bool IsMarker(string value, string prefix)
        {
            return value != null && value.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSharedParameter(string parameter)
        {
            return parameter == SHARED_SCALE_FACTOR;
        }

        private static bool IsShortSpsPlugParam(string parameter)
        {
            return !string.IsNullOrEmpty(parameter)
                && parameter.StartsWith(SPS_PLUG_BASE, System.StringComparison.Ordinal);
        }

        private static bool IsVfLengthParameter(string parameter)
        {
            if (string.IsNullOrEmpty(parameter)) return false;
            if (!parameter.EndsWith("/Length", System.StringComparison.OrdinalIgnoreCase)) return false;
            if (parameter.Length < 5) return false;
            if (parameter[0] != 'V' && parameter[0] != 'v') return false;
            if (parameter[1] != 'F' && parameter[1] != 'f') return false;

            var digitCount = 0;
            var index = 2;
            while (index < parameter.Length && char.IsDigit(parameter[index]))
            {
                digitCount++;
                index++;
            }

            return digitCount > 0 && index < parameter.Length && parameter[index] == '_';
        }

        private static string NameForInstance(string name, PlugInstance inst)
        {
            if (inst == null || inst.index <= 1) return name;
            return $"{name} {inst.index}";
        }

        private static bool IsAapConvertMotion(Motion motion)
        {
            return motion != null
                && (string.Equals(motion.name.Trim(), AAP_CONVERT, System.StringComparison.OrdinalIgnoreCase)
                    || MotionLooksLikeAapConvertTemplate(motion));
        }

        private static bool MotionLooksLikeAapConvertTemplate(Motion motion)
        {
            if (!(motion is BlendTree tree)) return false;
            foreach (var child in tree.children)
            {
                if (child.motion is AnimationClip clip && ClipContainsSpsMarkerCurve(clip))
                    return true;
            }
            return false;
        }

        private static bool ClipContainsSpsMarkerCurve(AnimationClip clip)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (!IsAnimatorParameterBinding(binding)) continue;
                if (IsMarker(binding.propertyName, M1)
                    && binding.propertyName.Substring(M1.Length).TrimStart('/')
                        .StartsWith(SPS_PLUG_BASE, System.StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // ---------- source driver remap ----------

        private bool RemapSourceDriverBehaviours(StateMachineBehaviour[] behaviours, PlugInstance inst, ref int patchedRefs)
        {
            if (behaviours == null) return false;

            var changed = false;
            foreach (var behaviour in behaviours)
            {
                if (!(behaviour is AvatarParameterDriver driver) || driver.parameters == null) continue;

                var newList = new List<DriverParameter>();
                var driverChanged = false;
                foreach (var sourceParam in driver.parameters)
                {
                    var copy = JsonUtility.FromJson<DriverParameter>(JsonUtility.ToJson(sourceParam));

                    var mappedName = MapParam(copy.name, inst);
                    var mappedSource = MapParam(copy.source, inst);

                    if (mappedName != copy.name)
                    {
                        copy.name = mappedName;
                        patchedRefs++;
                        driverChanged = true;
                    }
                    if (mappedSource != copy.source)
                    {
                        copy.source = mappedSource;
                        patchedRefs++;
                        driverChanged = true;
                    }

                    if (!string.IsNullOrEmpty(copy.name))
                        newList.Add(copy);
                }

                if (!driverChanged) continue;
                driver.parameters = newList;
                EditorUtility.SetDirty(driver);
                changed = true;
            }

            return changed;
        }

        // ---------- repath_ ----------

        private List<string> ResolvePaths(string query)
        {
            query = (query ?? "").Trim('/');
            if (_pathCache.TryGetValue(query, out var cached)) return cached;

            var matches = new List<string>();
            foreach (var path in GetAvatarPaths())
                if (path == query || path.EndsWith("/" + query))
                    matches.Add(path);

            if (matches.Count == 0)
            {
                if (DebugEnabled)
                    Warn($"[repath_] No object on the avatar matched '{query}'; skipped.");
            }
            else
                Report($"SUCCESS: repath_ matched '{query}' to {matches.Count} path(s).");

            _pathCache[query] = matches;
            return matches;
        }

        // ---------- rescan_ ----------

        private List<(string path, System.Type type)> ResolveMaterialTargets(string baseProp, string scope)
        {
            var cacheKey = baseProp + "\n" + (scope ?? "");
            if (_materialTargetCache.TryGetValue(cacheKey, out var cached)) return cached;

            var matches = new List<(string path, System.Type type)>();
            foreach (var record in GetRendererRecords())
            {
                if (!InScope(record.path, scope)) continue;

                foreach (var material in record.materials)
                {
                    if (material == null || !material.HasProperty(baseProp)) continue;

                    matches.Add((record.path, record.type));
                    break;
                }
            }

            Report(matches.Count > 0
                ? $"SUCCESS: rescan_ found {matches.Count} renderer target(s) for material property '{baseProp}'" +
                  (string.IsNullOrEmpty(scope) ? "." : $" under scope '{scope}'.")
                : $"FAILED: rescan_ found no renderer targets for material property '{baseProp}'" +
                  (string.IsNullOrEmpty(scope) ? "." : $" under scope '{scope}'."));

            _materialTargetCache[cacheKey] = matches;
            return matches;
        }

        private List<string> GetAvatarPaths()
        {
            if (_avatarPaths != null) return _avatarPaths;

            _avatarPaths = new List<string>();
            foreach (var transform in _avatar.GetComponentsInChildren<Transform>(true))
            {
                if (transform == _avatar.transform) continue;
                _avatarPaths.Add(GetTransformPath(transform));
            }

            return _avatarPaths;
        }

        private List<(string path, System.Type type, Material[] materials)> GetRendererRecords()
        {
            if (_rendererRecords != null) return _rendererRecords;

            _rendererRecords = new List<(string path, System.Type type, Material[] materials)>();
            foreach (var renderer in _avatar.GetComponentsInChildren<Renderer>(true))
            {
                var path = GetTransformPath(renderer.transform);
                _rendererRecords.Add((path, renderer.GetType(), renderer.sharedMaterials));
            }

            Report($"Cached {_rendererRecords.Count} renderer record(s) for rescan_.");
            return _rendererRecords;
        }

        private static bool InScope(string path, string scope)
        {
            if (string.IsNullOrEmpty(scope)) return true;
            return path == scope
                || path.EndsWith("/" + scope)
                || path.StartsWith(scope + "/")
                || path.Contains("/" + scope + "/");
        }

        private static string MaterialPropBaseName(string propertyName)
        {
            const string prefix = "material.";
            if (propertyName == null || !propertyName.StartsWith(prefix)) return null;

            var prop = propertyName.Substring(prefix.Length);
            var dot = prop.LastIndexOf('.');
            if (dot > 0)
            {
                var channel = prop.Substring(dot + 1);
                if (channel.Length == 1 && "rgbaxyzw".IndexOf(char.ToLower(channel[0])) >= 0)
                    prop = prop.Substring(0, dot);
            }

            return prop;
        }

        // ---------- scale helpers ----------

        private static bool IsAnimatorParameterBinding(EditorCurveBinding binding)
        {
            return binding.type == typeof(Animator) && string.IsNullOrEmpty(binding.path);
        }

        private static bool IsScaleBinding(EditorCurveBinding binding)
        {
            if (string.IsNullOrEmpty(binding.propertyName)) return false;

            var prop = binding.propertyName.ToLowerInvariant();
            return prop == "m_localscale.x"
                || prop == "m_localscale.y"
                || prop == "m_localscale.z"
                || prop == "localscale.x"
                || prop == "localscale.y"
                || prop == "localscale.z"
                || prop.EndsWith(".m_localscale.x")
                || prop.EndsWith(".m_localscale.y")
                || prop.EndsWith(".m_localscale.z")
                || prop.EndsWith(".localscale.x")
                || prop.EndsWith(".localscale.y")
                || prop.EndsWith(".localscale.z");
        }

        private static int GetScaleAxis(EditorCurveBinding binding)
        {
            var prop = (binding.propertyName ?? "").ToLowerInvariant();
            if (prop.EndsWith(".y")) return 1;
            if (prop.EndsWith(".z")) return 2;
            return 0;
        }

        private static float GetDefaultScaleForAxis(Vector3 scale, int axis)
        {
            if (axis == 1) return scale.y;
            if (axis == 2) return scale.z;
            return scale.x;
        }

        private static bool HasNonZeroKey(AnimationCurve curve)
        {
            foreach (var key in curve.keys)
                if (Mathf.Abs(key.value) > 0.0001f)
                    return true;
            return false;
        }

        private static bool CurveHasValue(AnimationCurve curve, float value)
        {
            if (curve == null) return false;
            foreach (var key in curve.keys)
                if (Mathf.Abs(key.value - value) <= FLOAT_MATCH_EPSILON)
                    return true;
            return false;
        }

        // ---------- misc ----------

        private bool DebugEnabled => _report != null;

        private string GetTransformPath(Transform transform)
        {
            if (transform == null) return "";
            if (_transformPathCache.TryGetValue(transform, out var cached)) return cached;

            var path = AnimationUtility.CalculateTransformPath(transform, _avatar.transform);
            _transformPathCache[transform] = path;
            return path;
        }

        private ClipAnalysis GetClipAnalysis(AnimationClip clip)
        {
            if (clip == null) return ClipAnalysis.Empty;
            if (_clipAnalysisCache.TryGetValue(clip, out var cached)) return cached;

            var allBindings = AnimationUtility.GetCurveBindings(clip);
            var scaleBindings = allBindings.Where(IsScaleBinding).ToArray();
            var lengthBindings = allBindings
                .Where(x => IsAnimatorParameterBinding(x) && IsVfLengthParameter(x.propertyName))
                .ToArray();
            var byPath = new Dictionary<string, List<EditorCurveBinding>>();
            foreach (var binding in scaleBindings)
            {
                if (!byPath.TryGetValue(binding.path, out var list))
                {
                    list = new List<EditorCurveBinding>();
                    byPath[binding.path] = list;
                }
                list.Add(binding);
            }

            var analysis = new ClipAnalysis
            {
                scaleBindings = scaleBindings,
                lengthBindings = lengthBindings,
                scaleBindingsByPath = byPath
            };
            _clipAnalysisCache[clip] = analysis;
            return analysis;
        }

        private void AddSub(Object obj)
        {
            if (obj == null) return;
            obj.hideFlags = HideFlags.HideInHierarchy;
            if (_fxIsAsset && !AssetDatabase.Contains(obj))
                AssetDatabase.AddObjectToAsset(obj, _fx);
        }

        private void Warn(string message)
        {
            if (!DebugEnabled) return;
            Debug.LogWarning("[ContactFixMerger] " + message);
        }

        private void Report(string message)
        {
            _report?.Add(message);
        }

        private class ClipAnalysis
        {
            public static readonly ClipAnalysis Empty = new ClipAnalysis
            {
                scaleBindings = new EditorCurveBinding[0],
                lengthBindings = new EditorCurveBinding[0],
                scaleBindingsByPath = new Dictionary<string, List<EditorCurveBinding>>()
            };

            public EditorCurveBinding[] scaleBindings;
            public EditorCurveBinding[] lengthBindings;
            public Dictionary<string, List<EditorCurveBinding>> scaleBindingsByPath;
        }

        private class PlugInstance
        {
            public int index;
            public string suffix;
            public string vfParam;
            public string scaleParam;
            public Transform contact;
            public List<ScaleSource> scaleSources;
        }

        private class ScaleSource
        {
            public Transform transform;
            public string path;
            public Vector3 defaultScale;
        }

        private class ScalePatchTarget
        {
            public PlugInstance instance;
            public ScaleSource source;
            public int sourceOrder;
            public bool used;
        }
    }
}
