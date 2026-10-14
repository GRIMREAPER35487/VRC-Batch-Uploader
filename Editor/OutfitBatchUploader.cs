// ============================================================
//  Synthos Batch Uploader
//  Window: Tools > Synthos > Batch Uploader
//
//  Automates uploading multiple VRChat avatar outfits that live
//  as children of a single "Outfits" parent in your scene.
//  For each outfit the tool switches tags (Untagged / EditorOnly),
//  sets the PipelineManager blueprintId, applies per-outfit
//  blendshape and material overrides, and triggers the VRC SDK
//  build + upload.
//
//  Per-outfit settings are saved as JSON in
//  ProjectSettings/VRC_Batch_Uploader; window options in EditorPrefs.
// ============================================================

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Core;
using VRC.SDK3A.Editor;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;   // VRCApi, VRCAvatar

namespace Synthos.BatchUploader
{
    public partial class OutfitBatchUploader : EditorWindow
    {
        // ---- Enums ----
        public enum UploadMode
        {
            SingleAvatar,
            SameProject
        }

        // ---- Constants ----
        private const string PREFS_PREFIX            = "ShiroOutfitUploader_";
        private const string PREFS_PARENT_NAME       = "ShiroOutfitUploader_OutfitsParentName";
        private const string PREFS_SOUND_ENABLED     = "ShiroOutfitUploader_SoundEnabled";
        private const string PREFS_ERROR_SOUND_ENABLED = "ShiroOutfitUploader_ErrorSoundEnabled";
        private const string PREFS_PROCEED_ON_ERROR  = "ShiroOutfitUploader_ProceedOnError";
        private const string PREFS_RESET_TO_FIRST    = "ShiroOutfitUploader_ResetToFirst";
        private const string PREFS_UPLOAD_MODE       = "ShiroOutfitUploader_UploadMode";
        private const string PREFS_LINK_ANDROID_IOS  = "ShiroOutfitUploader_LinkAndroidIOS";
        private const string DEFAULT_PARENT_NAME     = "Outfits";

        private const string SESSION_BATCH_ACTIVE     = "Shiro_BatchActive";
        private const string SESSION_BATCH_QUEUE      = "Shiro_BatchQueue";
        private const string SESSION_BATCH_TOTAL      = "Shiro_BatchTotal";
        private const string SESSION_BATCH_INDEX      = "Shiro_BatchIndex";
        private const string SESSION_SKIPPED          = "Shiro_BatchSkipped";
        private const string SESSION_INITIAL_PLATFORM = "Shiro_InitialPlatform";
        private const string SESSION_FINAL_STATUS_MSG = "Shiro_FinalStatusMsg";
        private const string SESSION_FINAL_STATUS_TYPE = "Shiro_FinalStatusType";
        private const string SESSION_PLAY_SOUND_ON_WAKE = "Shiro_PlaySoundOnWake";
        private const string SESSION_PLAY_ERROR_SOUND_ON_WAKE = "Shiro_PlayErrorSoundOnWake";
        private const string SESSION_PROCEED_ON_ERROR   = "Shiro_ProceedOnError";
        private const string SESSION_RESET_ON_WAKE      = "Shiro_ResetToFirstOnWake";
        private const string SESSION_BATCH_VERSION     = "Shiro_BatchVersion";
        private const string SESSION_IS_DIRECT_UPLOAD   = "Shiro_IsDirectUpload";

        // ---- State ----
        [SerializeField] private UploadMode _uploadMode = UploadMode.SingleAvatar;
        [SerializeField] private GameObject _avatarRoot;
        [SerializeField] private GameObject _pcAvatarRoot;
        [SerializeField] private GameObject _androidAvatarRoot;
        [SerializeField] private GameObject _iosAvatarRoot;
        [SerializeField] private bool       _linkAndroidAndIOS = true;
        [SerializeField] private bool       _resetToFirstOutfit = true;

        private List<GameObject>     _avatarsInScene   = new List<GameObject>();
        [SerializeField] private SkinnedMeshRenderer _skinRenderer;
        [SerializeField] private SkinnedMeshRenderer _pcSkinRenderer;
        [SerializeField] private SkinnedMeshRenderer _androidSkinRenderer;
        [SerializeField] private SkinnedMeshRenderer _iosSkinRenderer;

        private GameObject           _outfitsParent;
        private List<OutfitEntry>    _outfits          = new List<OutfitEntry>();
        private string               _outfitsParentName = DEFAULT_PARENT_NAME;
        private Vector2              _scroll;
        private bool               _soundEnabled;
        private bool               _errorSoundEnabled = true;
        private bool               _proceedOnError;
        private bool               _isBatchUploading;
        private int                _batchIndex;
        private int                _batchTotal;
        private float              _batchSubProgress;
        private string             _avatarVersion    = "";
        private string             _statusMessage    = "";
        private MessageType        _statusType       = MessageType.Info;
        private CancellationTokenSource _cts;
        private bool               _queueLoopRunning;
        private int                _queueRunId;
        private double             _resumeNotBefore;

        // ---- Styles (lazy init) ----
        private GUIStyle _headerStyle;
        private GUIStyle _activeRowStyle;
        private GUIStyle _inactiveRowStyle;
        private bool     _stylesInited;

        // ============================================================
        [MenuItem("Window/Synthos/Batch Uploader", priority = 40)]
        [MenuItem("Tools/Synthos/Batch Uploader", priority = 40)]
        public static void ShowWindow()
        {
            var w = GetWindow<OutfitBatchUploader>("Batch Uploader");
            w.minSize = new Vector2(440, 340);
        }

        // ============================================================
        private void OnEnable()
        {
            titleContent = new GUIContent("Batch Uploader");
            _outfitsParentName  = EditorPrefs.GetString(PREFS_PARENT_NAME, DEFAULT_PARENT_NAME);
            _soundEnabled       = EditorPrefs.GetBool(PREFS_SOUND_ENABLED, true);
            _errorSoundEnabled  = EditorPrefs.GetBool(PREFS_ERROR_SOUND_ENABLED, true);
            _proceedOnError     = EditorPrefs.GetBool(PREFS_PROCEED_ON_ERROR, false);
            _resetToFirstOutfit = EditorPrefs.GetBool(PREFS_RESET_TO_FIRST, true);
            _uploadMode         = (UploadMode)EditorPrefs.GetInt(PREFS_UPLOAD_MODE, (int)_uploadMode);
            _linkAndroidAndIOS  = EditorPrefs.GetBool(PREFS_LINK_ANDROID_IOS, _linkAndroidAndIOS);
            ScanScene();
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;

            // Resume batch if we just woke up from a Domain Reload (e.g. after a platform switch)
            if (SessionState.GetBool(SESSION_BATCH_ACTIVE, false))
            {
                _isBatchUploading = true;
                EditorApplication.update += HandleResumeBatch;
            }
            // Check for express setup resume
            else if (SessionState.GetBool(SESSION_EXPRESS_PENDING, false))
            {
                TryResumeExpress();
            }
            // Check for a finished batch status after a domain reload
            else if (SessionState.GetBool(SESSION_PLAY_SOUND_ON_WAKE, false) ||
                     SessionState.GetBool(SESSION_PLAY_ERROR_SOUND_ON_WAKE, false) ||
                     !string.IsNullOrEmpty(SessionState.GetString(SESSION_FINAL_STATUS_MSG, "")) ||
                     SessionState.GetBool(SESSION_RESET_ON_WAKE, false))
            {
                EditorApplication.update += HandleFinishedBatch;
            }
        }

        private void OnDisable()
        {
            StopConsentWatcher();
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
        }

        private void OnHierarchyChanged()
        {
            if (GetActivePlatformAvatars().Count > 0)
            {
                RebuildOutfitList();
            }
            else
            {
                ScanScene();
            }
            Repaint();
        }

        private void OnInspectorUpdate()
        {
            // Force periodic UI redraws to instantly reflect tag or active state toggles done in the Inspector
            Repaint();
        }

        private void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            ScanScene();
            Repaint();
        }

        private void HandleResumeBatch()
        {
            // Wait until Unity is fully settled after the Domain Reload
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            // Give a pending platform-switch reload time to start before resuming in this domain
            if (EditorApplication.timeSinceStartup < _resumeNotBefore) return;

            // Wait for VRC SDK Builder to re-initialize
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out _)) return;

            // NEW: Wait for user to be logged in before resuming
            if (!APIUser.IsLoggedIn)
            {
                SetStatus("Waiting for VRChat SDK login...", MessageType.Info);
                Repaint();
                return;
            }

            EditorApplication.update -= HandleResumeBatch;
            _cts = new CancellationTokenSource();
            _ = ProcessBatchQueueAsync();
        }

        private void HandleFinishedBatch()
        {
            // Wait until Unity is fully settled after the Domain Reload
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            EditorApplication.update -= HandleFinishedBatch;

            if (SessionState.GetBool(SESSION_RESET_ON_WAKE, false))
            {
                SessionState.EraseBool(SESSION_RESET_ON_WAKE);
                ResetToFirstOutfit(false);
            }

            string finalStatus = SessionState.GetString(SESSION_FINAL_STATUS_MSG, "");
            if (!string.IsNullOrEmpty(finalStatus))
            {
                MessageType finalType = (MessageType)SessionState.GetInt(SESSION_FINAL_STATUS_TYPE, (int)MessageType.Info);
                SetStatus(finalStatus, finalType);
                SessionState.EraseString(SESSION_FINAL_STATUS_MSG);
                SessionState.EraseInt(SESSION_FINAL_STATUS_TYPE);
            }

            if (SessionState.GetBool(SESSION_PLAY_SOUND_ON_WAKE, false))
            {
                PlayConfirmSound();
                SessionState.EraseBool(SESSION_PLAY_SOUND_ON_WAKE);
            }

            if (SessionState.GetBool(SESSION_PLAY_ERROR_SOUND_ON_WAKE, false))
            {
                PlayErrorSound();
                SessionState.EraseBool(SESSION_PLAY_ERROR_SOUND_ON_WAKE);
            }
    
            Repaint();
        }

        // ============================================================
        //  Platform Avatar Helpers
        // ============================================================

        /// <summary>Fallback that respects Unity's destroyed-object null check (`??` does not).</summary>
        private static T Or<T>(T a, T b) where T : UnityEngine.Object => a != null ? a : b;

        public GameObject GetTargetAvatarForPlatform(VRCPlatform platform)
        {
            if (_uploadMode == UploadMode.SingleAvatar)
            {
                return _avatarRoot;
            }

            switch (platform)
            {
                case VRCPlatform.Windows:
                    return _pcAvatarRoot != null ? _pcAvatarRoot : _avatarRoot;
                case VRCPlatform.Android:
                    return _androidAvatarRoot != null ? _androidAvatarRoot : _avatarRoot;
                case VRCPlatform.iOS:
                    return Or(_linkAndroidAndIOS ? _androidAvatarRoot : _iosAvatarRoot, _avatarRoot);
                default:
                    return _avatarRoot;
            }
        }

        public List<GameObject> GetActivePlatformAvatars()
        {
            var list = new List<GameObject>();
            if (_uploadMode == UploadMode.SingleAvatar)
            {
                if (_avatarRoot != null) list.Add(_avatarRoot);
            }
            else
            {
                if (_pcAvatarRoot != null && !list.Contains(_pcAvatarRoot)) list.Add(_pcAvatarRoot);
                if (_androidAvatarRoot != null && !list.Contains(_androidAvatarRoot)) list.Add(_androidAvatarRoot);
                if (!_linkAndroidAndIOS && _iosAvatarRoot != null && !list.Contains(_iosAvatarRoot)) list.Add(_iosAvatarRoot);
            }
            return list;
        }

        private SkinnedMeshRenderer AutoDetectSkinFor(GameObject root)
        {
            if (root == null) return null;

            foreach (Transform child in root.transform)
            {
                var smr = child.GetComponent<SkinnedMeshRenderer>();
                if (smr != null && smr.sharedMesh != null && smr.sharedMesh.blendShapeCount > 0)
                {
                    return smr;
                }
            }
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr.sharedMesh != null && smr.sharedMesh.blendShapeCount > 0)
                {
                    return smr;
                }
            }
            return null;
        }

        private SkinnedMeshRenderer GetSkinRendererForAvatar(GameObject root)
        {
            if (root == null) return null;
            if (root == _avatarRoot && _skinRenderer != null) return _skinRenderer;
            if (root == _pcAvatarRoot && _pcSkinRenderer != null) return _pcSkinRenderer;
            if (root == _androidAvatarRoot && _androidSkinRenderer != null) return _androidSkinRenderer;
            if (root == _iosAvatarRoot && _iosSkinRenderer != null) return _iosSkinRenderer;

            return AutoDetectSkinFor(root);
        }

        // ============================================================
        //  Scene scanning
        // ============================================================

        /// <summary>
        /// Full scene scan: refreshes the avatar list.
        /// In Single Avatar mode: auto-selects if unique avatar in scene.
        /// In Same Project mode: auto-detects PC and Mobile avatars based on scene objects.
        /// </summary>
        private void ScanScene()
        {
            _outfitsParent = null;
            _outfits.Clear();
            ResetNewSetupUiState();

            _avatarsInScene = FindObjectsOfType<VRCAvatarDescriptor>()
                .Select(d => d.gameObject)
                .ToList();

            if (_uploadMode == UploadMode.SingleAvatar)
            {
                if (_avatarRoot == null || !_avatarsInScene.Contains(_avatarRoot))
                    _avatarRoot = _avatarsInScene.Count == 1 ? _avatarsInScene[0] : null;
            }
            else
            {
                if (_pcAvatarRoot == null || !_avatarsInScene.Contains(_pcAvatarRoot))
                {
                    _pcAvatarRoot = _avatarsInScene.FirstOrDefault(a => 
                        a.name.IndexOf("PC", StringComparison.OrdinalIgnoreCase) >= 0 || 
                        a.name.IndexOf("Standalone", StringComparison.OrdinalIgnoreCase) >= 0) 
                        ?? (_avatarsInScene.Count > 0 ? _avatarsInScene[0] : null);
                }

                if (_androidAvatarRoot == null || !_avatarsInScene.Contains(_androidAvatarRoot))
                {
                    _androidAvatarRoot = _avatarsInScene.FirstOrDefault(a => 
                        a.name.IndexOf("Quest", StringComparison.OrdinalIgnoreCase) >= 0 || 
                        a.name.IndexOf("Android", StringComparison.OrdinalIgnoreCase) >= 0 || 
                        a.name.IndexOf("Mobile", StringComparison.OrdinalIgnoreCase) >= 0) 
                        ?? (_avatarsInScene.Count > 1 ? _avatarsInScene[1] : null);
                }

                if (_iosAvatarRoot == null || !_avatarsInScene.Contains(_iosAvatarRoot))
                {
                    if (_linkAndroidAndIOS)
                    {
                        _iosAvatarRoot = _androidAvatarRoot;
                    }
                    else
                    {
                        _iosAvatarRoot = _avatarsInScene.FirstOrDefault(a => 
                            a.name.IndexOf("iOS", StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                }

                _avatarRoot = Or(Or(_pcAvatarRoot, _androidAvatarRoot), _avatarsInScene.Count > 0 ? _avatarsInScene[0] : null);
            }

            AutoDetectSkin();
            RebuildOutfitList();
            LoadAvatarVersion();
        }

        /// <summary>Finds skin mesh renderers (SkinnedMeshRenderer with blendshapes) for active avatars.</summary>
        private void AutoDetectSkin()
        {
            if (_skinRenderer == null && _avatarRoot != null)
                _skinRenderer = AutoDetectSkinFor(_avatarRoot);
            if (_pcSkinRenderer == null && _pcAvatarRoot != null)
                _pcSkinRenderer = AutoDetectSkinFor(_pcAvatarRoot);
            if (_androidSkinRenderer == null && _androidAvatarRoot != null)
                _androidSkinRenderer = AutoDetectSkinFor(_androidAvatarRoot);
            if (_iosSkinRenderer == null && _iosAvatarRoot != null)
                _iosSkinRenderer = AutoDetectSkinFor(_iosAvatarRoot);
        }

        /// <summary>
        /// Rebuilds the outfit list from all active platform avatar root(s).
        /// Call this whenever the avatar selection or outfits-parent-name changes.
        /// </summary>
        private void RebuildOutfitList()
        {
            var activeAvatars = GetActivePlatformAvatars();
            if (activeAvatars.Count == 0)
            {
                _outfitsParent = null;
                _outfits.Clear();
                return;
            }

            Transform mainOutfitsTransform = null;
            foreach (var av in activeAvatars)
            {
                var found = FindDeepChild(av.transform, _outfitsParentName);
                if (found != null)
                {
                    mainOutfitsTransform = found;
                    break;
                }
            }

            if (mainOutfitsTransform == null)
            {
                _outfitsParent = null;
                _outfits.Clear();
                return;
            }
            _outfitsParent = mainOutfitsTransform.gameObject;

            string avatarKey = activeAvatars[0].name;
            var existingEntries = _outfits.Where(o => o != null)
                .GroupBy(o => o.Name)
                .ToDictionary(g => g.Key, g => g.First());

            var newOutfits = new List<OutfitEntry>();
            var processedNames = new HashSet<string>();

            foreach (var av in activeAvatars)
            {
                var outfitsT = FindDeepChild(av.transform, _outfitsParentName);
                if (outfitsT == null) continue;

                foreach (Transform child in outfitsT)
                {
                    string outfitName = child.gameObject.name;
                    if (processedNames.Contains(outfitName)) continue;
                    processedNames.Add(outfitName);

                    string expectedPrefKey = PREFS_PREFIX + avatarKey + "_" + outfitName;

                    if (existingEntries.TryGetValue(outfitName, out var existing))
                    {
                        existing.Go = child.gameObject;
                        existing.Name = outfitName;
                        existing.PrefsKey = expectedPrefKey;
                        newOutfits.Add(existing);
                    }
                    else
                    {
                        var entry = new OutfitEntry
                        {
                            Go       = child.gameObject,
                            Name     = outfitName,
                            PrefsKey = expectedPrefKey
                        };
                        LoadOutfitSettings(entry);
                        newOutfits.Add(entry);
                    }
                }
            }

            _outfits = newOutfits;
        }

        private static Transform FindDeepChild(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name) return child;
                var found = FindDeepChild(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private void LoadAvatarVersion()
        {
            _avatarVersion = "";
            string mainId = GetMainBlueprintId();
            if (!string.IsNullOrEmpty(mainId))
            {
                _avatarVersion = AvatarVersionManager.GetVersion(mainId);
            }
        }

        private string GetMainBlueprintId()
        {
            var av = GetTargetAvatarForPlatform(VRCPlatform.Windows);
            if (av == null) return null;
            var pm = av.GetComponent<PipelineManager>();
            if (pm != null && !string.IsNullOrWhiteSpace(pm.blueprintId))
            {
                return pm.blueprintId;
            }
            return null;
        }

        // ============================================================
        //  GUI
        // ============================================================
        private void OnGUI()
        {
            InitStyles();

            // ---- Header ----
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("VRC Batch Uploader", _headerStyle);
            EditorGUILayout.Space(4);

            DrawTopBar();
            EditorGUILayout.Space(4);
            DrawSeparator();

            DrawMainPageTabs();
            EditorGUILayout.Space(4);

            if (_mainPage == 2)
            {
                DrawNewOutfitSetupSection(true);
                return;
            }

            if (_outfitsParent == null)
            {
                DrawNoOutfitsMessage();
                return;
            }

            DrawOutfitPage(_mainPage == 1);
        }

        private int _mainPage; // 0 = Outfits, 1 = New Outfits, 2 = Defaults

        private void DrawMainPageTabs()
        {
            int newCount = _outfits.Count(o => o != null && o.Go != null && string.IsNullOrWhiteSpace(o.BlueprintId));
            string newLabel = newCount > 0 ? $"New Outfits ({newCount})" : "New Outfits";
            _mainPage = GUILayout.Toolbar(
                Mathf.Clamp(_mainPage, 0, 2),
                new[] { "Outfits", newLabel, "Defaults" },
                GUILayout.Height(24));
        }

        private void DrawOutfitPage(bool setupOnly)
        {
            var visibleOutfits = setupOnly
                ? _outfits.Where(o => o != null && o.Go != null && string.IsNullOrWhiteSpace(o.BlueprintId)).ToList()
                : _outfits;

            if (setupOnly)
            {
                if (visibleOutfits.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "All detected outfits already have a Blueprint ID. New outfits will appear here until their first upload is configured.",
                        MessageType.Info);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "These outfits still need their first upload. Use Express for the saved defaults or Advanced for a one-time override.",
                        MessageType.Info);
                }
            }

            // ---- Outfit list header ----
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    setupOnly
                        ? $"New outfits ({visibleOutfits.Count})  —  parent: \"{_outfitsParent.name}\""
                        : $"Outfits ({_outfits.Count})  —  parent: \"{_outfitsParent.name}\"",
                    EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(setupOnly || _isBatchUploading || _isExpressBusy))
                {
                    EditorGUILayout.LabelField("Include in batch:", EditorStyles.miniLabel, GUILayout.Width(96));
                    if (GUILayout.Button("All", EditorStyles.miniButton, GUILayout.Width(40)))
                        SetAllOutfitsIncluded(true);
                    if (GUILayout.Button("None", EditorStyles.miniButton, GUILayout.Width(44)))
                        SetAllOutfitsIncluded(false);
                }
            }
            EditorGUILayout.Space(4);

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            for (int i = 0; i < visibleOutfits.Count; i++)
                DrawOutfitRow(visibleOutfits[i]);
            EditorGUILayout.EndScrollView();

            DrawSeparator();
            DrawBatchSection();
            EditorGUILayout.Space(4);
        }

        private void SetAllOutfitsIncluded(bool included)
        {
            foreach (var o in _outfits)
            {
                if (o == null) continue;
                o.IncludeInBatch = included;
                SaveOutfitSettings(o);
            }
        }

        // ---- Top bar ----
        private void DrawTopBar()
        {
            // Mode selection Toolbar
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Mode:", GUILayout.Width(82));
                EditorGUI.BeginChangeCheck();
                _uploadMode = (UploadMode)GUILayout.Toolbar((int)_uploadMode, new string[] { "Single Avatar Root", "Same Project Mode" });
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetInt(PREFS_UPLOAD_MODE, (int)_uploadMode);
                    ScanScene();
                }

                if (GUILayout.Button("↺", EditorStyles.miniButton, GUILayout.Width(24)))
                    ScanScene();

                var activeAvs = GetActivePlatformAvatars();
                if (activeAvs.Count > 0)
                {
                    var restoreContent = new GUIContent("Restore Base",
                        "Restore the base materials and blendshapes that the active outfit's overrides replaced.");
                    if (GUILayout.Button(restoreContent, EditorStyles.miniButton, GUILayout.Width(90)))
                    {
                        RevertActiveOverrides(true);
                        SetStatus("Restored base materials and blendshapes.", MessageType.Info);
                        Repaint();
                    }
                }
            }

            EditorGUILayout.Space(4);

            if (_uploadMode == UploadMode.SingleAvatar)
            {
                // Single Avatar Root mode UI
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Avatar root:", GUILayout.Width(82));
                    EditorGUI.BeginChangeCheck();
                    var picked = (GameObject)EditorGUILayout.ObjectField(
                        _avatarRoot, typeof(GameObject), true);
                    if (EditorGUI.EndChangeCheck())
                    {
                        if (picked != null && picked.GetComponentInChildren<VRCAvatarDescriptor>() == null)
                        {
                            Debug.LogWarning("[OutfitBatchUploader] Selected object has no VRCAvatarDescriptor.");
                        }
                        else
                        {
                            _avatarRoot = picked;
                            AutoDetectSkin();
                            RebuildOutfitList();
                            LoadAvatarVersion();
                        }
                    }
                }

                if (_avatarsInScene.Count > 1)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Quick pick:", GUILayout.Width(82));
                        foreach (var av in _avatarsInScene)
                        {
                            bool isCurrent = av == _avatarRoot;
                            using (new EditorGUI.DisabledScope(isCurrent))
                            {
                                if (GUILayout.Button(av.name, EditorStyles.miniButton))
                                {
                                    _avatarRoot = av;
                                    AutoDetectSkin();
                                    RebuildOutfitList();
                                    LoadAvatarVersion();
                                }
                            }
                        }
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Avatar skin:", GUILayout.Width(82));
                    EditorGUI.BeginChangeCheck();
                    _skinRenderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                        _skinRenderer, typeof(SkinnedMeshRenderer), true);
                    if (EditorGUI.EndChangeCheck() && _skinRenderer != null)
                    {
                        int bsCount = _skinRenderer.sharedMesh != null ? _skinRenderer.sharedMesh.blendShapeCount : 0;
                        SetStatus($"Skin: {_skinRenderer.name}  ({bsCount} blendshapes)", MessageType.Info);
                    }
                }
            }
            else
            {
                // Same Project Mode UI
                EditorGUILayout.HelpBox("Same Project Mode: Select which scene avatar is PC and Mobile (Android / iOS).", MessageType.None);

                // PC Avatar
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("PC Avatar:", GUILayout.Width(82));
                    EditorGUI.BeginChangeCheck();
                    _pcAvatarRoot = (GameObject)EditorGUILayout.ObjectField(_pcAvatarRoot, typeof(GameObject), true);
                    if (EditorGUI.EndChangeCheck())
                    {
                        AutoDetectSkin();
                        RebuildOutfitList();
                        LoadAvatarVersion();
                    }
                }

                // Mobile Avatar (Android / iOS)
                using (new EditorGUILayout.HorizontalScope())
                {
                    string mobileLabel = _linkAndroidAndIOS ? "Mobile Avatar:" : "Android Avi:";
                    EditorGUILayout.LabelField(mobileLabel, GUILayout.Width(82));
                    EditorGUI.BeginChangeCheck();
                    _androidAvatarRoot = (GameObject)EditorGUILayout.ObjectField(_androidAvatarRoot, typeof(GameObject), true);
                    if (EditorGUI.EndChangeCheck())
                    {
                        if (_linkAndroidAndIOS) _iosAvatarRoot = _androidAvatarRoot;
                        AutoDetectSkin();
                        RebuildOutfitList();
                    }
                }

                // Link toggle
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("", GUILayout.Width(82));
                    EditorGUI.BeginChangeCheck();
                    _linkAndroidAndIOS = EditorGUILayout.ToggleLeft("Android & iOS use same Mobile Avatar", _linkAndroidAndIOS);
                    if (EditorGUI.EndChangeCheck())
                    {
                        EditorPrefs.SetBool(PREFS_LINK_ANDROID_IOS, _linkAndroidAndIOS);
                        if (_linkAndroidAndIOS) _iosAvatarRoot = _androidAvatarRoot;
                        RebuildOutfitList();
                    }
                }

                // iOS Avatar (if not linked)
                if (!_linkAndroidAndIOS)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("iOS Avatar:", GUILayout.Width(82));
                        EditorGUI.BeginChangeCheck();
                        _iosAvatarRoot = (GameObject)EditorGUILayout.ObjectField(_iosAvatarRoot, typeof(GameObject), true);
                        if (EditorGUI.EndChangeCheck())
                        {
                            AutoDetectSkin();
                            RebuildOutfitList();
                        }
                    }
                }

                // Quick Pick Auto-detect button
                if (_avatarsInScene.Count > 0)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Auto-assign:", GUILayout.Width(82));
                        if (GUILayout.Button("Auto-Detect Scene Avatars", EditorStyles.miniButton))
                        {
                            _pcAvatarRoot = null;
                            _androidAvatarRoot = null;
                            _iosAvatarRoot = null;
                            ScanScene();
                        }
                    }
                }
            }

            // Outfits Parent Name
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Outfits parent:", GUILayout.Width(82));
                EditorGUI.BeginChangeCheck();
                _outfitsParentName = EditorGUILayout.TextField(_outfitsParentName);
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetString(PREFS_PARENT_NAME, _outfitsParentName);
                    RebuildOutfitList();
                }
            }

            // Base Version
            string mainId = GetMainBlueprintId();
            if (!string.IsNullOrEmpty(mainId))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Base Version:", GUILayout.Width(82));
                    EditorGUI.BeginChangeCheck();
                    _avatarVersion = EditorGUILayout.TextField(_avatarVersion);
                    if (EditorGUI.EndChangeCheck())
                    {
                        AvatarVersionManager.SetVersion(mainId, _avatarVersion);
                    }

                    int targets = GetDescriptionTargets().Count;
                    using (new EditorGUI.DisabledScope(_isBatchUploading || _isUpdatingDescriptions || _isExpressBusy ||
                                                       string.IsNullOrWhiteSpace(_avatarVersion) || targets == 0))
                    {
                        var content = new GUIContent(
                            _isUpdatingDescriptions ? "Updating…" : $"Update Descriptions ({targets})",
                            "Write the Base Version into the description of every outfit with \"Include in batch\" checked, " +
                            "without building or uploading anything.");
                        if (GUILayout.Button(content, EditorStyles.miniButtonLeft, GUILayout.Width(150)))
                            _ = UpdateDescriptionsOnlyAsync();
                    }

                    using (new EditorGUI.DisabledScope(_isBatchUploading || _isUpdatingDescriptions || _isExpressBusy || targets == 0))
                    {
                        if (GUILayout.Button(new GUIContent("▾", "More description options"), EditorStyles.miniButtonRight, GUILayout.Width(20)))
                        {
                            var menu = new GenericMenu();
                            if (string.IsNullOrWhiteSpace(_avatarVersion))
                                menu.AddDisabledItem(new GUIContent("Update version only (set a Base Version first)"));
                            else
                                menu.AddItem(new GUIContent("Update version only"), false, () => _ = UpdateDescriptionsOnlyAsync());
                            menu.AddItem(new GUIContent("Replace full description…"), false, () =>
                                DescriptionOverrideWindow.Open(this, GetDescriptionTargets()));
                            menu.ShowAsContext();
                        }
                    }
                }
            }
        }

        private bool _isUpdatingDescriptions;

        /// <summary>Lets the user type one description that replaces the whole description on every included outfit.</summary>
        internal class DescriptionOverrideWindow : EditorWindow
        {
            private const string PREFS_LAST_TEXT = "Synthos_BatchUploader_DescriptionOverride";

            private OutfitBatchUploader _owner;
            private List<OutfitEntry> _targets;
            private string _text = "";
            private bool _loading;
            private Vector2 _scroll;

            internal static void Open(OutfitBatchUploader owner, List<OutfitEntry> targets)
            {
                var w = GetWindow<DescriptionOverrideWindow>(true, "Replace full description");
                w._owner = owner;
                w._targets = targets;
                w._text = EditorPrefs.GetString(PREFS_LAST_TEXT, "");
                w.minSize = new Vector2(420, 300);
                w.Show();

                // Nothing typed before: start from the first outfit's current description
                if (string.IsNullOrEmpty(w._text) && targets.Count > 0 && APIUser.IsLoggedIn)
                    _ = w.LoadCurrentAsync(targets[0]);
            }

            private async Task LoadCurrentAsync(OutfitEntry outfit)
            {
                _loading = true;
                Repaint();
                try
                {
                    var avatar = await VRCApi.GetAvatar(outfit.BlueprintId, forceRefresh: true);
                    _text = avatar.Description ?? "";
                    GUI.FocusControl(null);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[OutfitBatchUploader] Could not load the description for '{outfit.Name}': {GetFailureReason(ex)}");
                }
                finally
                {
                    _loading = false;
                    Repaint();
                }
            }

            private void OnGUI()
            {
                if (_owner == null || _targets == null) { Close(); return; }

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(
                    $"This text replaces the whole description on {_targets.Count} avatar(s): " +
                    string.Join(", ", _targets.Select(t => t.Name)),
                    EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.LabelField(
                    "Type {version} where the Base Version should go, so later version updates can find it.",
                    EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.Space(2);

                using (new EditorGUI.DisabledScope(_loading))
                {
                    _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
                    if (_loading)
                        EditorGUILayout.TextArea("Loading current description…", GUILayout.ExpandHeight(true));
                    else
                        _text = EditorGUILayout.TextArea(_text, GUILayout.ExpandHeight(true));
                    EditorGUILayout.EndScrollView();
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(_loading || _targets.Count == 0 || !APIUser.IsLoggedIn))
                    {
                        if (GUILayout.Button($"Load from {(_targets.Count > 0 ? _targets[0].Name : "first outfit")}", GUILayout.Height(24)))
                            _ = LoadCurrentAsync(_targets[0]);
                    }

                    GUILayout.FlexibleSpace();

                    using (new EditorGUI.DisabledScope(_loading || string.IsNullOrWhiteSpace(_text)))
                    {
                        if (GUILayout.Button($"Replace on {_targets.Count} avatar(s)", GUILayout.Height(24), GUILayout.Width(170)))
                        {
                            bool ok = EditorUtility.DisplayDialog(
                                "Replace descriptions",
                                $"Replace the whole description on {_targets.Count} avatar(s) with this text?\n\nThis can't be undone from here.",
                                "Replace", "Cancel");
                            if (ok)
                            {
                                EditorPrefs.SetString(PREFS_LAST_TEXT, _text);
                                var owner = _owner;
                                string text = _text;
                                Close();
                                _ = owner.UpdateDescriptionsOnlyAsync(text);
                                return;
                            }
                        }
                    }

                    if (GUILayout.Button("Cancel", GUILayout.Height(24), GUILayout.Width(70))) Close();
                }
                EditorGUILayout.Space(4);
            }
        }

        /// <summary>One entry per blueprint ID (platforms share it) among the outfits included in the batch.</summary>
        private List<OutfitEntry> GetDescriptionTargets()
        {
            return _outfits
                .Where(o => o != null && o.IncludeInBatch && !string.IsNullOrWhiteSpace(o.BlueprintId))
                .GroupBy(o => o.BlueprintId)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>
        /// Swaps the old version for the Base Version in each included outfit's description through the VRChat API,
        /// the same way an upload does, but without building. Failures are collected and listed like batch skips.
        /// With fullDescription set, every description is replaced with that text instead ({version} = Base Version).
        /// </summary>
        private async Task UpdateDescriptionsOnlyAsync(string fullDescription = null)
        {
            if (!APIUser.IsLoggedIn)
            {
                PlayErrorSound();
                SetStatus("Not logged in. Please open the VRChat SDK Control Panel and log in first.", MessageType.Error);
                return;
            }

            bool replaceFully = fullDescription != null;
            string versionToSet = (_avatarVersion ?? "").Trim();
            var targets = GetDescriptionTargets();
            if ((!replaceFully && string.IsNullOrWhiteSpace(versionToSet)) || targets.Count == 0) return;

            if (replaceFully)
                fullDescription = fullDescription.Replace("{version}", versionToSet);

            // Only remember a version as written when the new text actually contains it, so later updates can find it
            bool writesVersion = !string.IsNullOrWhiteSpace(versionToSet) &&
                                 (!replaceFully || fullDescription.Contains(versionToSet));

            _isUpdatingDescriptions = true;
            int updated = 0, unchanged = 0;
            var failed = new List<string>();

            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    var outfit = targets[i];
                    SetStatus($"[{i + 1}/{targets.Count}] Updating description for {outfit.Name}...", MessageType.Info);
                    Repaint();

                    try
                    {
                        var avatar = await VRCApi.GetAvatar(outfit.BlueprintId, forceRefresh: true);
                        string previousVersion = AvatarVersionManager.GetUploadedVersion(outfit.BlueprintId);
                        string newDesc = replaceFully
                            ? fullDescription
                            : AvatarVersionManager.UpdateVersionInDescription(avatar.Description, previousVersion, versionToSet);

                        if (newDesc == null)
                        {
                            string reason = "could not find the old version " +
                                            (string.IsNullOrEmpty(previousVersion) ? "" : $"\"{previousVersion}\" ") +
                                            "in the description";
                            Debug.LogWarning($"[OutfitBatchUploader] '{outfit.Name}': {reason} - left it unchanged.");
                            failed.Add($"{outfit.Name} - {reason}");
                            continue;
                        }

                        if (newDesc == avatar.Description)
                        {
                            unchanged++;
                        }
                        else
                        {
                            avatar.Description = newDesc;
                            await VRCApi.UpdateAvatarInfo(outfit.BlueprintId, avatar);
                            Debug.Log(replaceFully
                                ? $"[OutfitBatchUploader] Replaced '{outfit.Name}' description."
                                : $"[OutfitBatchUploader] Updated '{outfit.Name}' description to version: {versionToSet}");
                            updated++;
                        }

                        if (writesVersion)
                        {
                            AvatarVersionManager.SetVersion(outfit.BlueprintId, versionToSet);
                            AvatarVersionManager.SetUploadedVersion(outfit.BlueprintId, versionToSet);
                        }
                    }
                    catch (Exception ex)
                    {
                        string reason = GetFailureReason(ex);
                        Debug.LogWarning($"[OutfitBatchUploader] Could not update the description for '{outfit.Name}': {reason}\n\n{ex}");
                        failed.Add($"{outfit.Name} - {reason}");
                    }
                }
            }
            finally
            {
                _isUpdatingDescriptions = false;
            }

            string summary = (replaceFully ? "Descriptions replaced" : $"Descriptions updated to {versionToSet}") +
                             $" — {updated} changed, {unchanged} already up to date";
            if (failed.Count > 0)
            {
                summary += $", {failed.Count} failed:\n• " + string.Join("\n• ", failed);
                PlayErrorSound();
                Debug.LogWarning($"[OutfitBatchUploader] {summary}");
            }
            else
            {
                summary += ".";
                Debug.Log($"[OutfitBatchUploader] {summary}");
            }

            SetStatus(summary, failed.Count > 0 ? MessageType.Warning : MessageType.Info);
            Repaint();
        }

        // ---- No outfits message ----
        private void DrawNoOutfitsMessage()
        {
            EditorGUILayout.Space(8);
            var activeAvs = GetActivePlatformAvatars();
            if (activeAvs.Count == 0)
                EditorGUILayout.HelpBox(
                    _avatarsInScene.Count == 0
                        ? "No avatar with a VRCAvatarDescriptor found in the scene.\nOpen your avatar scene and click ↺."
                        : "Select avatar root(s) in the field(s) above.",
                    MessageType.Warning);
            else
                EditorGUILayout.HelpBox(
                    $"No child named \"{_outfitsParentName}\" found under active avatar root(s).\n" +
                    "Check the outfits parent name above, or drag the correct parent object directly into the field.",
                    MessageType.Warning);
        }

        // ---- Per-outfit row ----
        private void DrawOutfitRow(OutfitEntry entry)
        {
            if (entry.Go == null) return;
            bool isActive = entry.Go.CompareTag("Untagged");

            var rowStyle = isActive ? _activeRowStyle : _inactiveRowStyle;
            using (new EditorGUILayout.VerticalScope(rowStyle))
            {
                // Row 1: name + status + buttons
                using (new EditorGUILayout.HorizontalScope())
                {
                    string icon = isActive ? "●" : "○";
                    EditorGUILayout.LabelField($"{icon}  {entry.Name}", EditorStyles.boldLabel, GUILayout.ExpandWidth(true));

                    string tagText = entry.Go.tag;
                    var tagColor   = isActive ? Color.green : Color.gray;
                    var oldColor   = GUI.color;
                    GUI.color      = tagColor;
                    EditorGUILayout.LabelField(tagText, GUILayout.Width(80));
                    GUI.color      = oldColor;

                    using (new EditorGUI.DisabledScope(_isBatchUploading))
                    {
                        if (GUILayout.Button("Select", GUILayout.Width(60)))
                            ActivateOutfit(entry);

                        if (GUILayout.Button("Ping", GUILayout.Width(44)))
                        {
                            EditorGUIUtility.PingObject(entry.Go);
                            Selection.activeGameObject = entry.Go;
                        }
                    }
                }

                // Row 2: Blueprint ID + Upload button
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Blueprint ID:", GUILayout.Width(82));
                    EditorGUI.BeginChangeCheck();
                    entry.BlueprintId = EditorGUILayout.TextField(entry.BlueprintId ?? "", GUILayout.ExpandWidth(true));
                    if (EditorGUI.EndChangeCheck())
                        SaveOutfitSettings(entry);

                    using (new EditorGUI.DisabledScope(_isBatchUploading || string.IsNullOrWhiteSpace(entry.BlueprintId)))
                    {
                        if (GUILayout.Button("Upload", GUILayout.Width(56)))
                        {
                            ActivateOutfit(entry);
                            _ = StartBatchAsync(new List<OutfitEntry> { entry }, isDirectUpload: true);
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(entry.BlueprintId))
                {
                    DrawInlineNewOutfitButtons(entry);
                }

                // Row 3: batch include toggle
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    entry.IncludeInBatch = EditorGUILayout.ToggleLeft("Include in batch upload", entry.IncludeInBatch, GUILayout.Width(160));
                    if (EditorGUI.EndChangeCheck())
                        SaveOutfitSettings(entry);
                    
                    GUILayout.FlexibleSpace();
                    
                    EditorGUI.BeginChangeCheck();
                    entry.BuildWindows = EditorGUILayout.ToggleLeft("Win", entry.BuildWindows, GUILayout.Width(45));
                    entry.BuildAndroid = EditorGUILayout.ToggleLeft("And", entry.BuildAndroid, GUILayout.Width(45));
                    entry.BuildIOS     = EditorGUILayout.ToggleLeft("iOS", entry.BuildIOS, GUILayout.Width(40));
                    if (EditorGUI.EndChangeCheck())
                    {
                        SaveOutfitSettings(entry);
                    }
                }

                // Row 4: blendshape foldout
                DrawBlendShapeFoldout(entry);
                DrawMaterialFoldout(entry);
            }
            EditorGUILayout.Space(2);
        }

        // ---- Material foldout ----
        private void DrawMaterialFoldout(OutfitEntry entry)
        {
            int count = entry.MaterialOverrides.Count;
            string foldoutLabel = count > 0
                ? $"Material Swaps  ({count} overrides)"
                : "Material Swaps";

            entry.MaterialExpanded = EditorGUILayout.Foldout(
                entry.MaterialExpanded, foldoutLabel, true, EditorStyles.foldout);

            if (!entry.MaterialExpanded) return;

            bool dirty = false;

            for (int i = 0; i < entry.MaterialOverrides.Count; i++)
            {
                var mo = entry.MaterialOverrides[i];

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField($"Override {i + 1}", EditorStyles.boldLabel, GUILayout.Width(80));
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22)))
                        {
                            entry.MaterialOverrides.RemoveAt(i);
                            dirty = true;
                            break;
                        }
                    }

                    if (!mo.Resolved) ResolveMaterialOverride(mo);

                    if (_uploadMode == UploadMode.SameProject)
                    {
                        EditorGUILayout.LabelField("PC Avatar", EditorStyles.boldLabel);
                        dirty |= DrawOverrideTarget("PC Renderer", "PC Slot", "PC Material",
                            Or(_pcAvatarRoot, _avatarRoot), "PC avatar root",
                            ref mo.TargetRenderer, ref mo.RendererPath, ref mo.MaterialSlot,
                            ref mo.OverrideMat, ref mo.OverrideMatGuid);

                        EditorGUILayout.Space(4);

                        EditorGUILayout.LabelField("Mobile Avatar (Android / iOS)", EditorStyles.boldLabel);
                        dirty |= DrawOverrideTarget("Mobile Renderer", "Mobile Slot", "Mobile Material",
                            Or(Or(_androidAvatarRoot, _iosAvatarRoot), _avatarRoot), "Mobile avatar root",
                            ref mo.AndroidTargetRenderer, ref mo.AndroidRendererPath, ref mo.AndroidMaterialSlot,
                            ref mo.AndroidOverrideMat, ref mo.AndroidOverrideMatGuid);
                    }
                    else
                    {
                        dirty |= DrawOverrideTarget("Renderer", "Slot", "Override Material",
                            _avatarRoot, "avatar root",
                            ref mo.TargetRenderer, ref mo.RendererPath, ref mo.MaterialSlot,
                            ref mo.OverrideMat, ref mo.OverrideMatGuid);
                    }
                }
            }

            EditorGUILayout.Space(2);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Add Material Override", GUILayout.Width(150)))
                {
                    entry.MaterialOverrides.Add(new MaterialOverride());
                    dirty = true;
                }
            }

            if (dirty)
            {
                SaveMaterialOverrides(entry);
                Repaint();
            }
        }

        /// <summary>Resolves the saved renderer paths and material GUIDs of an override into live references.</summary>
        private void ResolveMaterialOverride(MaterialOverride mo)
        {
            // PC Renderer
            if (!string.IsNullOrEmpty(mo.RendererPath))
            {
                var pcRoot = _uploadMode == UploadMode.SameProject ? Or(_pcAvatarRoot, _avatarRoot) : _avatarRoot;
                if (pcRoot != null)
                {
                    Transform t = pcRoot.transform.Find(mo.RendererPath);
                    if (t != null) mo.TargetRenderer = t.GetComponent<Renderer>();
                }
                if (mo.TargetRenderer == null)
                {
                    foreach (var av in GetActivePlatformAvatars())
                    {
                        Transform t = av.transform.Find(mo.RendererPath);
                        if (t != null) { mo.TargetRenderer = t.GetComponent<Renderer>(); break; }
                    }
                }
            }

            // Mobile Renderer
            var mobileRoot = Or(_androidAvatarRoot, _iosAvatarRoot);
            string mobilePath = !string.IsNullOrEmpty(mo.AndroidRendererPath) ? mo.AndroidRendererPath : mo.RendererPath;
            if (!string.IsNullOrEmpty(mobilePath) && mobileRoot != null)
            {
                Transform t = mobileRoot.transform.Find(mobilePath);
                if (t != null) mo.AndroidTargetRenderer = t.GetComponent<Renderer>();
            }

            mo.OverrideMat        = LoadMaterialByGuid(mo.OverrideMatGuid);
            mo.AndroidOverrideMat = LoadMaterialByGuid(mo.AndroidOverrideMatGuid);
            mo.Resolved = true;
        }

        private static Material LoadMaterialByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        /// <summary>Draws the renderer / slot / material fields of one side of a material override.
        /// Returns true when anything changed.</summary>
        private static bool DrawOverrideTarget(string rendererLabel, string slotLabel, string materialLabel,
            GameObject root, string rootDescription,
            ref Renderer renderer, ref string rendererPath, ref int slot,
            ref Material mat, ref string matGuid)
        {
            bool dirty = false;

            EditorGUI.BeginChangeCheck();
            renderer = (Renderer)EditorGUILayout.ObjectField(rendererLabel, renderer, typeof(Renderer), true);
            if (EditorGUI.EndChangeCheck())
            {
                if (renderer != null)
                {
                    if (root != null && renderer.transform.IsChildOf(root.transform))
                    {
                        rendererPath = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform);
                    }
                    else
                    {
                        Debug.LogWarning($"[VRC_Batch_Uploader] {rendererLabel} must be a child of the {rootDescription}.");
                        renderer = null;
                        rendererPath = "";
                    }
                }
                else
                {
                    rendererPath = "";
                }
                dirty = true;
            }

            if (renderer != null && renderer.sharedMaterials.Length > 0)
            {
                int maxSlot = renderer.sharedMaterials.Length - 1;
                slot = Mathf.Clamp(slot, 0, maxSlot);

                Material currentMat = renderer.sharedMaterials[slot];
                string currentMatName = currentMat != null ? currentMat.name : "None";

                EditorGUI.BeginChangeCheck();
                slot = EditorGUILayout.IntSlider($"{slotLabel} ({currentMatName})", slot, 0, maxSlot);
                if (EditorGUI.EndChangeCheck()) dirty = true;
            }
            else
            {
                EditorGUILayout.LabelField(slotLabel, "No materials found");
            }

            EditorGUI.BeginChangeCheck();
            mat = (Material)EditorGUILayout.ObjectField(materialLabel, mat, typeof(Material), false);
            if (EditorGUI.EndChangeCheck())
            {
                matGuid = "";
                if (mat != null)
                {
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mat, out string guid, out long _);
                    matGuid = guid;
                }
                dirty = true;
            }

            return dirty;
        }

        // ---- Blendshape foldout ----
        private void DrawBlendShapeFoldout(OutfitEntry entry)
        {
            int configuredCount = entry.BlendShapes.Count;
            string foldoutLabel = configuredCount > 0
                ? $"Blendshapes  ({configuredCount} overrides)"
                : "Blendshapes";

            entry.BlendShapeExpanded = EditorGUILayout.Foldout(
                entry.BlendShapeExpanded, foldoutLabel, true, EditorStyles.foldout);

            if (!entry.BlendShapeExpanded) return;

            var activeSkin = GetSkinRendererForAvatar(_avatarRoot);
            if (activeSkin == null || activeSkin.sharedMesh == null)
            {
                EditorGUILayout.HelpBox(
                    "No skin mesh selected. Pick a SkinnedMeshRenderer in the 'Avatar skin' field above.",
                    MessageType.Info);
                return;
            }

            var mesh    = activeSkin.sharedMesh;
            int bsCount = mesh.blendShapeCount;

            // Search bar
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Search:", GUILayout.Width(58));
                entry.BlendShapeSearch = EditorGUILayout.TextField(entry.BlendShapeSearch ?? "");
                if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22)))
                {
                    entry.BlendShapeSearch = "";
                    GUI.FocusControl(null);
                }
            }

            // "Capture" convenience button
            if (GUILayout.Button("Capture current skin values as overrides", EditorStyles.miniButton))
            {
                for (int i = 0; i < bsCount; i++)
                {
                    float w = activeSkin.GetBlendShapeWeight(i);
                    if (w > 0f)
                        entry.BlendShapes[mesh.GetBlendShapeName(i)] = w;
                }
                SaveBlendShapes(entry);
                Repaint();
            }

            EditorGUILayout.Space(2);

            string filter = (entry.BlendShapeSearch ?? "").ToLower();
            bool   dirty  = false;

            for (int i = 0; i < bsCount; i++)
            {
                string bsName = mesh.GetBlendShapeName(i);

                if (!string.IsNullOrEmpty(filter) && !bsName.ToLower().Contains(filter))
                    continue;

                bool  isPinned   = entry.BlendShapes.TryGetValue(bsName, out float storedVal);
                float displayVal = isPinned ? storedVal : activeSkin.GetBlendShapeWeight(i);

                // Draw toggle + slider on the same row without IndentLevelScope
                // (IndentLevelScope shifts visuals but not click rects, causing misses)
                Rect rowRect = EditorGUILayout.GetControlRect(false, 18f);

                // Checkbox — 20px on the left
                Rect toggleRect = new Rect(rowRect.x, rowRect.y, 20f, rowRect.height);
                bool nowPinned  = GUI.Toggle(toggleRect, isPinned, GUIContent.none);
                if (nowPinned != isPinned)
                {
                    if (nowPinned) entry.BlendShapes[bsName] = displayVal;
                    else           entry.BlendShapes.Remove(bsName);
                    dirty = true;
                }

                // Slider fills the rest of the row
                Rect sliderRect = new Rect(rowRect.x + 22f, rowRect.y, rowRect.width - 22f, rowRect.height);
                using (new EditorGUI.DisabledScope(!nowPinned))
                {
                    float newVal = GUI.HorizontalSlider(
                        new Rect(sliderRect.x + sliderRect.width - 120f, sliderRect.y + 2f, 100f, sliderRect.height - 4f),
                        displayVal, 0f, 100f);

                    // Label with name + value
                    GUI.Label(new Rect(sliderRect.x, sliderRect.y, sliderRect.width - 124f, sliderRect.height),
                        bsName, EditorStyles.label);
                    GUI.Label(new Rect(sliderRect.x + sliderRect.width - 18f, sliderRect.y, 18f, sliderRect.height),
                        Mathf.RoundToInt(displayVal).ToString(), EditorStyles.miniLabel);

                    if (nowPinned && Math.Abs(newVal - displayVal) > 0.001f)
                    {
                        entry.BlendShapes[bsName] = newVal;
                        dirty = true;
                    }
                }
            }

            if (dirty) { SaveBlendShapes(entry); Repaint(); }

            // Clear all button
            if (configuredCount > 0)
            {
                EditorGUILayout.Space(2);
                if (GUILayout.Button("Clear all overrides", EditorStyles.miniButton))
                {
                    entry.BlendShapes.Clear();
                    SaveBlendShapes(entry);
                    Repaint();
                }
            }
        }

        // ---- Batch section ----
        private void DrawBatchSection()
        {
            int ready = _outfits.Count(o => o.IncludeInBatch && !string.IsNullOrWhiteSpace(o.BlueprintId));

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Batch Upload", EditorStyles.boldLabel, GUILayout.Width(95));
                GUILayout.FlexibleSpace();

                EditorGUI.BeginChangeCheck();
                var resetContent = new GUIContent("Reset to 1st outfit", "Resets avatar to the first outfit in the list when batch uploads complete.");
                _resetToFirstOutfit = EditorGUILayout.ToggleLeft(resetContent, _resetToFirstOutfit, GUILayout.Width(125));
                if (EditorGUI.EndChangeCheck())
                    EditorPrefs.SetBool(PREFS_RESET_TO_FIRST, _resetToFirstOutfit);

                EditorGUI.BeginChangeCheck();
                var proceedContent = new GUIContent("Proceed on error", "If an outfit fails, log a warning and continue uploading the rest of the queue instead of halting the batch.");
                _proceedOnError = EditorGUILayout.ToggleLeft(proceedContent, _proceedOnError, GUILayout.Width(120));
                if (EditorGUI.EndChangeCheck())
                    EditorPrefs.SetBool(PREFS_PROCEED_ON_ERROR, _proceedOnError);

                EditorGUI.BeginChangeCheck();
                var soundContent = new GUIContent("Play Sound on Finish", "Play completion chime when uploads finish.");
                _soundEnabled = EditorGUILayout.ToggleLeft(soundContent, _soundEnabled, GUILayout.Width(145));
                if (EditorGUI.EndChangeCheck())
                    EditorPrefs.SetBool(PREFS_SOUND_ENABLED, _soundEnabled);

                EditorGUI.BeginChangeCheck();
                var errSoundContent = new GUIContent("Play Sound on Error", "Play an audible alert tone immediately if an avatar encounters a build or upload error.");
                _errorSoundEnabled = EditorGUILayout.ToggleLeft(errSoundContent, _errorSoundEnabled, GUILayout.Width(140));
                if (EditorGUI.EndChangeCheck())
                    EditorPrefs.SetBool(PREFS_ERROR_SOUND_ENABLED, _errorSoundEnabled);
            }
            EditorGUILayout.LabelField(
                $"{ready} outfit(s) ready  (have a Blueprint ID + \"Include in batch\" checked)",
                EditorStyles.miniLabel);
            EditorGUILayout.Space(4);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (!_isBatchUploading)
                {
                    using (new EditorGUI.DisabledScope(ready == 0))
                    {
                        Color oldColor = GUI.backgroundColor;
                        bool isSuccess = (_statusMessage.StartsWith("Queue complete") || _statusMessage.StartsWith("Upload complete")) && _statusType == MessageType.Info;
                        if (isSuccess) GUI.backgroundColor = new Color(0.2f, 0.8f, 0.2f);

                        if (GUILayout.Button($"Batch Upload All ({ready})", GUILayout.Height(30)))
                        {
                            var batch = _outfits.Where(o => o.IncludeInBatch && !string.IsNullOrWhiteSpace(o.BlueprintId)).ToList();
                            _ = StartBatchAsync(batch, isDirectUpload: false);
                        }

                        GUI.backgroundColor = oldColor;
                    }
                }
                else
                {
                    float progress = _batchTotal > 0 ? ((float)_batchIndex + _batchSubProgress) / _batchTotal : 0f;
                    Rect r = EditorGUILayout.GetControlRect(GUILayout.Height(30), GUILayout.ExpandWidth(true));

                    // Determine current platform from queue to tint progress bar
                    VRCPlatform currentPlat = GetCurrentPlatform();
                    string queueStr = SessionState.GetString(SESSION_BATCH_QUEUE, "");
                    if (!string.IsNullOrEmpty(queueStr))
                    {
                        string[] parts = queueStr.Split('\n')[0].Split('|');
                        if (parts.Length >= 3 && Enum.TryParse(parts[2], out VRCPlatform parsedPlat))
                            currentPlat = parsedPlat;
                    }

                    Color oldColor = GUI.color;
                    if (currentPlat == VRCPlatform.Android) GUI.color = new Color(0.65f, 1.0f, 0.65f); // Brighter Green
                    if (currentPlat == VRCPlatform.iOS)     GUI.color = new Color(0.8f, 0.85f, 0.9f);   // Light Silver-Blue
                    if (currentPlat == VRCPlatform.Windows) GUI.color = new Color(0.65f, 0.85f, 1.0f); // Brighter Blue

                    EditorGUI.ProgressBar(r, progress, $"Uploading {_batchIndex + 1} / {_batchTotal} ({currentPlat})…");
                    GUI.color      = oldColor;

                    if (GUILayout.Button("Cancel", GUILayout.Width(66), GUILayout.Height(30)))
                    {
                        RequestCancelBatch();
                    }
                }
            }

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.HelpBox(_statusMessage, _statusType);
            }
        }

        // ============================================================
        //  Core logic
        // ============================================================

        private string GetActiveMatOverridesFilePath(GameObject avRoot)
        {
            string safeName = avRoot != null ? string.Join("_", avRoot.name.Split(Path.GetInvalidFileNameChars())) : "Global";

            // Check legacy path first in case an interrupted upload left a backup
            string legacyPath = Path.Combine("Assets/VRC_Batch_Uploader/Data", $"{safeName}_BaseMaterials.json");
            if (File.Exists(legacyPath)) return legacyPath;

            string dir = "ProjectSettings/VRC_Batch_Uploader/Data";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"{safeName}_BaseMaterials.json");
        }

        /// <summary>Restores the base materials and blendshapes that the active outfit's overrides replaced.</summary>
        private void RevertActiveOverrides(bool clearAfter = true)
        {
            foreach (var avRoot in GetActivePlatformAvatars())
            {
                RevertActiveOverridesFor(avRoot, clearAfter);
            }
        }

        private void RevertActiveOverridesFor(GameObject avRoot, bool clearAfter)
        {
            if (avRoot == null) return;

            string sessionKey = "Shiro_ActiveMatOverrides_" + avRoot.name;
            string json = SessionState.GetString(sessionKey, "");
            string path = GetActiveMatOverridesFilePath(avRoot);

            if (File.Exists(path))
            {
                json = File.ReadAllText(path);
            }

            if (string.IsNullOrEmpty(json)) return;

            var backup = JsonUtility.FromJson<ActiveOverridesBackup>(json);
            if (backup != null)
            {
                // Reverse order so a slot overridden twice ends up with its true original
                if (backup.Overrides != null)
                    for (int i = backup.Overrides.Count - 1; i >= 0; i--)
                        RestoreMaterial(avRoot, backup.Overrides[i]);

                if (backup.BlendShapes != null)
                    RestoreBlendShapes(avRoot, backup.BlendShapes);
            }

            if (clearAfter)
            {
                SessionState.SetString(sessionKey, "");
                if (File.Exists(path))
                {
                    try
                    {
                        File.Delete(path);
                        if (path.StartsWith("Assets"))
                        {
                            File.Delete(path + ".meta");
                            AssetDatabase.Refresh();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[OutfitBatchUploader] Could not delete base materials file: {ex.Message}");
                    }
                }
            }
        }

        private static void RestoreMaterial(GameObject avRoot, ActiveMatOverride ov)
        {
            Transform t = avRoot.transform.Find(ov.RendererPath);
            if (t == null) return;
            Renderer r = t.GetComponent<Renderer>();
            if (r == null) return;

            var mats = r.sharedMaterials;
            if (ov.Slot < 0 || ov.Slot >= mats.Length) return;

            if (!TryResolveOriginalMaterial(ov, out Material originalMat))
            {
                Debug.LogWarning($"[OutfitBatchUploader] Could not find the original material for '{ov.RendererPath}' slot {ov.Slot} - leaving the slot unchanged.");
                return;
            }

            Undo.RecordObject(r, "Revert material override");
            mats[ov.Slot] = originalMat;
            r.sharedMaterials = mats;
            EditorUtility.SetDirty(r);
        }

        /// <summary>Resolves the material a slot held before an override.
        /// Returns true with a null material when the slot was originally empty.</summary>
        private static bool TryResolveOriginalMaterial(ActiveMatOverride ov, out Material mat)
        {
            mat = null;

            // Exact object id - also covers sub-assets (e.g. FBX-embedded materials) and scene-only materials
            if (!string.IsNullOrEmpty(ov.OriginalMatId))
            {
                if (GlobalObjectId.TryParse(ov.OriginalMatId, out var id))
                    mat = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as Material;
                return mat != null;
            }

            // Backups written before 1.1.0-preview.3 only stored the asset GUID
            if (!string.IsNullOrEmpty(ov.OriginalMatGuid))
            {
                mat = LoadMaterialByGuid(ov.OriginalMatGuid);
                return mat != null;
            }

            return true;
        }

        private static void RestoreBlendShapes(GameObject avRoot, List<ActiveBlendShape> shapes)
        {
            foreach (var group in shapes.GroupBy(s => s.SkinPath ?? ""))
            {
                Transform t = group.Key.Length == 0 ? avRoot.transform : avRoot.transform.Find(group.Key);
                var skin = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
                if (skin == null || skin.sharedMesh == null) continue;

                Undo.RecordObject(skin, "Revert blendshape overrides");
                foreach (var bs in group)
                {
                    int idx = skin.sharedMesh.GetBlendShapeIndex(bs.Name);
                    if (idx >= 0) skin.SetBlendShapeWeight(idx, bs.Value);
                }
                EditorUtility.SetDirty(skin);
            }
        }

        /// <summary>Applies the outfit's blendshape and material overrides to one avatar root and backs up
        /// the values they replace, so the next activation (or Restore Base) can put them back.</summary>
        private void ApplyOverridesFor(OutfitEntry target, GameObject avRoot)
        {
            if (avRoot == null) return;

            var backup = new ActiveOverridesBackup();
            ApplyBlendShapeOverridesFor(target, avRoot, backup);
            ApplyMaterialOverridesFor(target, avRoot, backup);

            if (backup.Overrides.Count == 0 && backup.BlendShapes.Count == 0) return;

            string json = JsonUtility.ToJson(backup);
            SessionState.SetString("Shiro_ActiveMatOverrides_" + avRoot.name, json);

            try
            {
                File.WriteAllText(GetActiveMatOverridesFilePath(avRoot), json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VRC_Batch_Uploader] Failed to save base materials/blendshapes backup: {ex.Message}");
            }
        }

        private void ApplyBlendShapeOverridesFor(OutfitEntry target, GameObject avRoot, ActiveOverridesBackup backup)
        {
            if (target.BlendShapes.Count == 0) return;

            var skin = GetSkinRendererForAvatar(avRoot);
            if (skin == null || skin.sharedMesh == null) return;

            string skinPath = skin.transform.IsChildOf(avRoot.transform)
                ? AnimationUtility.CalculateTransformPath(skin.transform, avRoot.transform)
                : null;

            Undo.RecordObject(skin, "Set blendshapes for outfit");
            var mesh = skin.sharedMesh;
            foreach (var kv in target.BlendShapes)
            {
                int idx = mesh.GetBlendShapeIndex(kv.Key);
                if (idx < 0) continue;

                if (skinPath != null)
                {
                    backup.BlendShapes.Add(new ActiveBlendShape
                    {
                        SkinPath = skinPath,
                        Name     = kv.Key,
                        Value    = skin.GetBlendShapeWeight(idx)
                    });
                }
                skin.SetBlendShapeWeight(idx, kv.Value);
            }
            EditorUtility.SetDirty(skin);
        }

        private void ApplyMaterialOverridesFor(OutfitEntry target, GameObject avRoot, ActiveOverridesBackup backup)
        {
            if (target.MaterialOverrides.Count == 0) return;

            bool isMobileRoot = (_uploadMode == UploadMode.SameProject) &&
                                (avRoot == _androidAvatarRoot || avRoot == _iosAvatarRoot);

            foreach (var mo in target.MaterialOverrides)
            {
                string rendererPath = isMobileRoot
                    ? (!string.IsNullOrEmpty(mo.AndroidRendererPath) ? mo.AndroidRendererPath : mo.RendererPath)
                    : mo.RendererPath;

                int slot = isMobileRoot
                    ? (!string.IsNullOrEmpty(mo.AndroidRendererPath) ? mo.AndroidMaterialSlot : mo.MaterialSlot)
                    : mo.MaterialSlot;

                string targetGuid = isMobileRoot ? mo.AndroidOverrideMatGuid : mo.OverrideMatGuid;

                if (string.IsNullOrEmpty(rendererPath) || string.IsNullOrEmpty(targetGuid)) continue;

                Transform t = avRoot.transform.Find(rendererPath);
                if (t == null) continue;
                Renderer r = t.GetComponent<Renderer>();
                if (r == null) continue;

                var mats = r.sharedMaterials;
                if (slot < 0 || slot >= mats.Length) continue;

                Material overrideMat = LoadMaterialByGuid(targetGuid);
                if (overrideMat == null) continue;

                // Capture original
                Material currentMat = mats[slot];
                string currentGuid = "";
                string currentId = "";
                if (currentMat != null)
                {
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(currentMat, out currentGuid, out long _);
                    currentId = GlobalObjectId.GetGlobalObjectIdSlow(currentMat).ToString();
                }

                backup.Overrides.Add(new ActiveMatOverride
                {
                    RendererPath    = rendererPath,
                    Slot            = slot,
                    OriginalMatGuid = currentGuid,
                    OriginalMatId   = currentId
                });

                Undo.RecordObject(r, "Apply material override");
                mats[slot] = overrideMat;
                r.sharedMaterials = mats;
                EditorUtility.SetDirty(r);
            }
        }

        /// <summary>Sets the chosen outfit to Untagged and all others to EditorOnly across target avatar root(s).
        /// Also switches the PipelineManager blueprintId if one is configured.</summary>
        public void ActivateOutfit(OutfitEntry target, VRCPlatform? targetPlatform = null)
        {
            List<GameObject> targetAvatars;

            if (targetPlatform.HasValue)
            {
                var avatarForPlat = GetTargetAvatarForPlatform(targetPlatform.Value);
                targetAvatars = avatarForPlat != null ? new List<GameObject> { avatarForPlat } : new List<GameObject>();
            }
            else
            {
                targetAvatars = GetActivePlatformAvatars();
            }

            if (targetAvatars.Count == 0) return;

            Undo.SetCurrentGroupName($"Activate Outfit: {target.Name}");
            int group = Undo.GetCurrentGroup();

            foreach (var avRoot in targetAvatars)
            {
                RevertActiveOverridesFor(avRoot, true);

                var outfitsTransform = FindDeepChild(avRoot.transform, _outfitsParentName);
                if (outfitsTransform != null)
                {
                    foreach (Transform child in outfitsTransform)
                    {
                        bool wantActive = (child.gameObject.name == target.Name);
                        string wantTag = wantActive ? "Untagged" : "EditorOnly";

                        bool tagNeedsChange = child.gameObject.tag != wantTag;
                        bool activeNeedsChange = child.gameObject.activeSelf != wantActive;

                        if (!tagNeedsChange && !activeNeedsChange) continue;

                        Undo.RecordObject(child.gameObject, "Set outfit active/tag");
                        if (tagNeedsChange) child.gameObject.tag = wantTag;
                        if (activeNeedsChange) child.gameObject.SetActive(wantActive);
                        EditorUtility.SetDirty(child.gameObject);
                    }
                }

                // Switch PipelineManager blueprintId
                if (!string.IsNullOrWhiteSpace(target.BlueprintId))
                {
                    var pm = avRoot.GetComponentInChildren<PipelineManager>();
                    if (pm != null && pm.blueprintId != target.BlueprintId)
                    {
                        Undo.RecordObject(pm, "Set Blueprint ID");
                        pm.blueprintId = target.BlueprintId;
                        EditorUtility.SetDirty(pm);
                    }
                }

                ApplyOverridesFor(target, avRoot);
            }

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

            SetStatus($"Activated: {target.Name}", MessageType.Info);
            Repaint();
        }

        /// <summary>
        /// Resets the avatar to the first outfit in the list across active platform avatars.
        /// </summary>
        public void ResetToFirstOutfit(bool flushScene = true)
        {
            if (_outfits == null || _outfits.Count == 0) return;
            var firstOutfit = _outfits.FirstOrDefault(o => o != null && o.Go != null);
            if (firstOutfit == null) return;

            ActivateOutfit(firstOutfit);
            if (flushScene)
            {
                FlushScene();
            }
        }

        // ---- Settings persistence ----

        [Serializable]
        public class OutfitSettings
        {
            public string BlueprintId = "";
            public bool IncludeInBatch = true;
            public bool BuildWindows = true;
            public bool BuildAndroid = false;
            public bool BuildIOS = false;
            public List<BlendShapeSaveEntry> BlendShapes = new List<BlendShapeSaveEntry>();
            public List<MaterialOverride> MaterialOverrides = new List<MaterialOverride>();
        }

        [Serializable]
        public class BlendShapeSaveEntry
        {
            public string Name;
            public float Value;
        }

        private static string GetSettingsFilePath(OutfitEntry entry)
        {
            string dir = "ProjectSettings/VRC_Batch_Uploader";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string safeKey = string.Join("_", entry.PrefsKey.Split(Path.GetInvalidFileNameChars()));
            string newPath = Path.Combine(dir, $"{safeKey}_Settings.json");
            if (File.Exists(newPath)) return newPath;

            string oldPath = Path.Combine("ProjectSettings/ShiroTools", $"{safeKey}_Settings.json");
            if (File.Exists(oldPath)) return oldPath;

            return newPath;
        }

        private static string GetLegacyMaterialOverridesFilePath(OutfitEntry entry)
        {
            string safeKey = string.Join("_", entry.PrefsKey.Split(Path.GetInvalidFileNameChars()));
            
            string settingsDir = "ProjectSettings/VRC_Batch_Uploader";
            string settingsPath = Path.Combine(settingsDir, $"{safeKey}_MatOverrides.json");
            if (File.Exists(settingsPath)) return settingsPath;

            string assetsPath = Path.Combine("Assets/VRC_Batch_Uploader/Data", $"{safeKey}_MatOverrides.json");
            if (File.Exists(assetsPath)) return assetsPath;

            string oldPath = Path.Combine("Assets/ShiroTools/Data", $"{safeKey}_MatOverrides.json");
            if (File.Exists(oldPath)) return oldPath;

            if (!Directory.Exists(settingsDir)) Directory.CreateDirectory(settingsDir);
            return settingsPath;
        }

        [Serializable]
        private class MaterialOverrideListWrapper
        {
            public List<MaterialOverride> Overrides = new List<MaterialOverride>();
        }

        [Serializable]
        private class MatGuidListWrapper
        {
            public List<string> Guids = new List<string>();
        }

        private static void SaveOutfitSettings(OutfitEntry entry)
        {
            try
            {
                var settings = new OutfitSettings
                {
                    BlueprintId = entry.BlueprintId,
                    IncludeInBatch = entry.IncludeInBatch,
                    BuildWindows = entry.BuildWindows,
                    BuildAndroid = entry.BuildAndroid,
                    BuildIOS = entry.BuildIOS,
                    MaterialOverrides = entry.MaterialOverrides
                };

                foreach (var kv in entry.BlendShapes)
                {
                    settings.BlendShapes.Add(new BlendShapeSaveEntry { Name = kv.Key, Value = kv.Value });
                }

                string json = JsonUtility.ToJson(settings, true);
                string path = GetSettingsFilePath(entry);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OutfitBatchUploader] Failed to save settings to file: {ex.Message}");
            }
        }

        private static void LoadOutfitSettings(OutfitEntry entry)
        {
            entry.BlendShapes.Clear();
            entry.MaterialOverrides.Clear();

            string settingsPath = GetSettingsFilePath(entry);
            bool loadedFromSettings = false;

            if (File.Exists(settingsPath))
            {
                try
                {
                    string json = File.ReadAllText(settingsPath);
                    if (!string.IsNullOrEmpty(json) && json.TrimStart().StartsWith("{"))
                    {
                        var settings = JsonUtility.FromJson<OutfitSettings>(json);
                        if (settings != null)
                        {
                            entry.BlueprintId = settings.BlueprintId;
                            entry.IncludeInBatch = settings.IncludeInBatch;
                            entry.BuildWindows = settings.BuildWindows;
                            entry.BuildAndroid = settings.BuildAndroid;
                            entry.BuildIOS = settings.BuildIOS;

                            if (settings.BlendShapes != null)
                            {
                                foreach (var bs in settings.BlendShapes)
                                {
                                    if (!string.IsNullOrEmpty(bs.Name))
                                        entry.BlendShapes[bs.Name] = bs.Value;
                                }
                            }

                            if (settings.MaterialOverrides != null)
                            {
                                entry.MaterialOverrides = settings.MaterialOverrides;
                            }

                            loadedFromSettings = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[OutfitBatchUploader] Failed to parse settings for {entry.Name} from file: {ex.Message}");
                }
            }

            if (!loadedFromSettings)
            {
                // Fallback to legacy EditorPrefs and legacy Assets/VRC_Batch_Uploader/Data/_MatOverrides.json
                string projKey = Hash128.Compute(Application.dataPath).ToString();
                
                entry.BlueprintId = EditorPrefs.GetString(entry.PrefsKey, "");
                entry.IncludeInBatch = EditorPrefs.GetBool(entry.PrefsKey + "_batch", true);
                entry.BuildWindows = EditorPrefs.GetBool(entry.PrefsKey + "_" + projKey + "_Win", true);
                entry.BuildAndroid = EditorPrefs.GetBool(entry.PrefsKey + "_" + projKey + "_And", false);
                entry.BuildIOS = EditorPrefs.GetBool(entry.PrefsKey + "_" + projKey + "_iOS", false);

                // Load legacy BlendShapes
                string keys = EditorPrefs.GetString(entry.PrefsKey + "_BS_keys", "");
                if (!string.IsNullOrEmpty(keys))
                {
                    foreach (var k in keys.Split(';'))
                    {
                        if (string.IsNullOrEmpty(k)) continue;
                        entry.BlendShapes[k] = EditorPrefs.GetFloat(entry.PrefsKey + "_BS_" + k, 0f);
                    }
                }

                // Load legacy MaterialOverrides from _MatOverrides.json
                string legacyPath = GetLegacyMaterialOverridesFilePath(entry);
                if (File.Exists(legacyPath))
                {
                    try
                    {
                        string json = File.ReadAllText(legacyPath);
                        if (!string.IsNullOrEmpty(json) && json.TrimStart().StartsWith("{"))
                        {
                            var wrapper = JsonUtility.FromJson<MaterialOverrideListWrapper>(json);
                            if (wrapper != null && wrapper.Overrides != null)
                            {
                                entry.MaterialOverrides = wrapper.Overrides;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[OutfitBatchUploader] Failed to parse legacy material overrides for {entry.Name} from file: {ex.Message}");
                    }
                }

                // Load legacy GUIDs from EditorPrefs
                string guidJson = EditorPrefs.GetString(entry.PrefsKey + "_MatGUIDs_" + projKey, "");
                if (!string.IsNullOrEmpty(guidJson) && guidJson.TrimStart().StartsWith("{"))
                {
                    try
                    {
                        var guidWrapper = JsonUtility.FromJson<MatGuidListWrapper>(guidJson);
                        if (guidWrapper != null && guidWrapper.Guids != null)
                        {
                            for (int i = 0; i < entry.MaterialOverrides.Count && i < guidWrapper.Guids.Count; i++)
                            {
                                entry.MaterialOverrides[i].OverrideMatGuid = guidWrapper.Guids[i];
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[OutfitBatchUploader] Failed to parse legacy material GUIDs for {entry.Name}: {ex.Message}");
                    }
                }

                // Auto-detect blueprint ID from PipelineManager on outfit or avatar root if available and blank
                if (string.IsNullOrEmpty(entry.BlueprintId) && entry.Go != null)
                {
                    var pm = entry.Go.GetComponent<PipelineManager>();
                    if (pm != null && !string.IsNullOrWhiteSpace(pm.blueprintId))
                    {
                        entry.BlueprintId = pm.blueprintId;
                    }
                }

                // Save to new settings file immediately so it persists across reloads
                SaveOutfitSettings(entry);
            }
        }

        private static void SaveBlendShapes(OutfitEntry entry)
        {
            SaveOutfitSettings(entry);
        }

        private static void SaveMaterialOverrides(OutfitEntry entry)
        {
            SaveOutfitSettings(entry);
        }

        // ---- Sound playback ----
        private static bool PlayClipViaAudioUtil(AudioClip clip)
        {
            if (clip == null) return false;
            try
            {
                var audioUtil  = typeof(AudioImporter).Assembly.GetType("UnityEditor.AudioUtil");
                var playMethod = audioUtil?.GetMethod(
                    "PlayPreviewClip",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { typeof(AudioClip), typeof(int), typeof(bool) },
                    null);

                if (playMethod != null)
                {
                    playMethod.Invoke(null, new object[] { clip, 0, false });
                    return true;
                }
                else
                {
                    Debug.LogWarning("[OutfitBatchUploader] PlayPreviewClip not found — Unity may have renamed it.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[OutfitBatchUploader] Could not play audio clip: " + ex.Message);
            }
            return false;
        }

        private static AudioClip GetConfirmSoundClip()
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>("Packages/com.synthos.batch-uploader/Editor/Sounds/UI Confirm Sound.mp3");
            if (clip != null) return clip;

            clip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/VRC_Batch_Uploader/Editor/Sounds/UI Confirm Sound.mp3");
            if (clip != null) return clip;

            string[] guids = AssetDatabase.FindAssets("UI Confirm Sound t:AudioClip");
            if (guids != null && guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            }

            return null;
        }

        private void PlayConfirmSound()
        {
            if (!_soundEnabled) return;

            var clip = GetConfirmSoundClip();
            if (clip == null)
            {
                Debug.LogWarning("[OutfitBatchUploader] Could not load confirm sound.");
                return;
            }

            PlayClipViaAudioUtil(clip);
        }

        private static AudioClip GetErrorSoundClip()
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>("Packages/com.synthos.batch-uploader/Editor/Sounds/UI Error Sound.wav");
            if (clip != null) return clip;

            clip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/VRC_Batch_Uploader/Editor/Sounds/UI Error Sound.wav");
            if (clip != null) return clip;

            string[] guids = AssetDatabase.FindAssets("UI Error Sound t:AudioClip");
            if (guids != null && guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            }

            return null;
        }

        private async void PlayErrorSound(int repeatCount = 2)
        {
            if (!_errorSoundEnabled) return;

            var clip = GetErrorSoundClip();
            int delayMs = clip != null ? Mathf.RoundToInt(clip.length * 1000f) + 50 : 350;

            for (int i = 0; i < repeatCount; i++)
            {
                if (i > 0)
                {
                    await Task.Delay(delayMs);
                    if (!_errorSoundEnabled) return;
                }

                bool played = false;
                if (clip != null)
                {
                    played = PlayClipViaAudioUtil(clip);
                }

                if (!played)
                {
                    try
                    {
                        EditorApplication.Beep();
                    }
                    catch { }
                }
            }
        }

        // ---- Copyright pre-consent ----

        /// <summary>
        /// Shows ONE confirmation dialog to Shiro, then calls VRCCopyrightAgreement.Agree()
        /// (via reflection, since it's internal) for each blueprint ID.
        /// After this the SDK's own consent check finds everything already agreed and stays silent.
        /// </summary>
        private static async Task<bool> PreConsentAllAsync(IEnumerable<string> blueprintIds)
        {
            bool confirmed = EditorUtility.DisplayDialog(
                "Ownership confirmation",
                "Do you confirm that all content you are about to upload belongs to you and that " +
                "you have the necessary rights to upload it?\n\n" +
                "This covers every outfit in the current batch.",
                "Yes, it's all mine",
                "Cancel");

            if (!confirmed) return false;

            // VRCCopyrightAgreement.Agree() is internal — reached via reflection
            var agreeMethod = typeof(VRCCopyrightAgreement).GetMethod(
                "Agree",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (agreeMethod == null)
            {
                Debug.LogWarning("[OutfitBatchUploader] Could not find VRCCopyrightAgreement.Agree via reflection. " +
                                 "The SDK consent dialog will appear normally instead.");
                return true;   // still proceed — SDK dialog will handle it
            }

            foreach (var id in blueprintIds.Where(id => !string.IsNullOrWhiteSpace(id)))
            {
                try
                {
                    var task = (Task<bool>)agreeMethod.Invoke(null, new object[] { id });
                    bool ok = await task;
                    if (!ok)
                        Debug.LogWarning($"[OutfitBatchUploader] Pre-consent API call returned false for {id}. " +
                                         "SDK may still show its own dialog for this outfit.");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[OutfitBatchUploader] Pre-consent failed for {id}: {ex.Message}");
                }
            }

            return true;
        }

        // ---- Flush scene so the SDK builder sees the tag change ----
        private static void FlushScene()
        {
            // Mark all dirty objects, save assets and scene, then let the editor process events
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            // Pump the editor loop so Unity registers the tag changes before the build starts
            EditorApplication.Step();
        }

        // ---- Platform switching helpers ----
        private VRCPlatform GetCurrentPlatform()
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            if (target == BuildTarget.Android) return VRCPlatform.Android;
            if (target == BuildTarget.iOS) return VRCPlatform.iOS;
            return VRCPlatform.Windows;
        }

        private bool SwitchPlatform(VRCPlatform plat)
        {
            BuildTargetGroup group = BuildTargetGroup.Standalone;
            BuildTarget target = BuildTarget.StandaloneWindows64;
            
            switch (plat)
            {
                case VRCPlatform.Android:
                    group = BuildTargetGroup.Android;
                    target = BuildTarget.Android;
                    break;
                case VRCPlatform.iOS:
                    group = BuildTargetGroup.iOS;
                    target = BuildTarget.iOS;
                    break;
                case VRCPlatform.Windows:
                    group = BuildTargetGroup.Standalone;
                    target = BuildTarget.StandaloneWindows64;
                    break;
            }
            
            if (!BuildPipeline.IsBuildTargetSupported(group, target))
            {
                Debug.LogError($"[OutfitBatchUploader] Build target {target} is not supported or not installed.");
                return false;
            }

            if (EditorUserBuildSettings.activeBuildTarget != target)
            {
                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(group, target) ||
                    EditorUserBuildSettings.activeBuildTarget != target)
                {
                    Debug.LogError($"[OutfitBatchUploader] Switching the build target to {target} failed.");
                    return false;
                }
            }
            return true;
        }

        // ---- Cross-Domain Batch Queue System ----
        private async Task StartBatchAsync(List<OutfitEntry> targetOutfits, bool isDirectUpload = false)
        {
            if (targetOutfits.Count == 0) return;

            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder))
            {
                PlayErrorSound();
                SetStatus("VRC SDK builder not available — open the VRChat SDK window first.", MessageType.Error);
                return;
            }

            // NEW: Check for login before starting
            if (!APIUser.IsLoggedIn)
            {
                PlayErrorSound();
                SetStatus("Not logged in. Please open the VRChat SDK Control Panel and log in first.", MessageType.Error);
                return;
            }

            var ids = targetOutfits.Select(o => o.BlueprintId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct();
            bool consented = await PreConsentAllAsync(ids);
            if (!consented)
            {
                SetStatus("Upload cancelled — ownership not confirmed.", MessageType.Warning);
                return;
            }

            // Build a flat queue of operations grouped by platform
            var queue = new List<string>();
            var platformOrder = new List<VRCPlatform> { VRCPlatform.Windows, VRCPlatform.Android, VRCPlatform.iOS };
            
            VRCPlatform currentPlatform = GetCurrentPlatform();
            if (platformOrder.Contains(currentPlatform))
            {
                platformOrder.Remove(currentPlatform);
                platformOrder.Insert(0, currentPlatform); // Start with current platform to minimize switching
            }

            foreach (var plat in platformOrder)
            {
                foreach (var outfit in targetOutfits)
                {
                    bool buildsForPlat = 
                        (plat == VRCPlatform.Windows && outfit.BuildWindows) ||
                        (plat == VRCPlatform.Android && outfit.BuildAndroid) ||
                        (plat == VRCPlatform.iOS && outfit.BuildIOS);

                    // Fallback: if no platforms selected for this outfit, build on the current active platform
                    bool hasAny = outfit.BuildWindows || outfit.BuildAndroid || outfit.BuildIOS;
                    if (!hasAny && plat == currentPlatform) buildsForPlat = true;

                    if (buildsForPlat)
                    {
                        queue.Add($"{outfit.Name}|{outfit.BlueprintId}|{plat}");
                    }
                }
            }

            if (queue.Count == 0)
            {
                SetStatus("No platforms configured for the target outfits.", MessageType.Warning);
                return;
            }

            // Save queue into Domain-Reload-proof SessionState
            SessionState.SetString(SESSION_BATCH_QUEUE, string.Join("\n", queue));
            SessionState.SetInt(SESSION_BATCH_TOTAL, queue.Count);
            SessionState.SetInt(SESSION_BATCH_INDEX, 0);
            SessionState.SetBool(SESSION_BATCH_ACTIVE, true);
            SessionState.SetBool(SESSION_IS_DIRECT_UPLOAD, isDirectUpload);
            SessionState.SetBool(SESSION_PROCEED_ON_ERROR, _proceedOnError);
            SessionState.SetString(SESSION_SKIPPED, "");
            SessionState.SetString(SESSION_INITIAL_PLATFORM, currentPlatform.ToString());
            SessionState.SetString(SESSION_BATCH_VERSION, _avatarVersion); // Capture the version from UI

            _isBatchUploading = true;
            _cts = new CancellationTokenSource();
            
            _ = ProcessBatchQueueAsync();
        }

        private async Task ProcessBatchQueueAsync()
        {
            if (!SessionState.GetBool(SESSION_BATCH_ACTIVE, false)) return;
            _isBatchUploading = true;
            Repaint();

            // Error recovery starts a fresh run from inside the old run's catch, so only the latest run clears the flag
            int runId = ++_queueRunId;
            _queueLoopRunning = true;

            try
            {
                while (true)
                {
                    if (_cts != null && _cts.IsCancellationRequested)
                    {
                        CancelBatch();
                        return;
                    }

                    string queueStr = SessionState.GetString(SESSION_BATCH_QUEUE, "");
                    if (string.IsNullOrWhiteSpace(queueStr))
                    {
                        FinishBatch();
                        return;
                    }

                    var queue = queueStr.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    int total = SessionState.GetInt(SESSION_BATCH_TOTAL, 0);
                    int currentIndex = SessionState.GetInt(SESSION_BATCH_INDEX, 0);

                    _batchIndex = currentIndex;
                    _batchTotal = total;
                    _batchSubProgress = 0.0f;

                    string[] parts = queue[0].Split('|');
                    string outfitName = parts[0];
                    string blueprintId = parts[1];
                    VRCPlatform platform = (VRCPlatform)Enum.Parse(typeof(VRCPlatform), parts[2]);

                    if (platform != GetCurrentPlatform())
                    {
                        SetStatus($"Switching to {platform} for {outfitName}...", MessageType.Info);
                        _batchSubProgress = 0.1f;
                        Repaint();
                        
                        if (!SwitchPlatform(platform))
                            throw new Exception($"Could not switch to {platform} - is the build support module installed?");

                        // IMPORTANT: The Platform Switch forces a Unity Domain Reload here.
                        // All code execution is about to die. We wire up a backup hook to resume just in case
                        // the switch finishes without a reload (delayed so it can't race a pending reload), then intentionally exit.
                        _resumeNotBefore = EditorApplication.timeSinceStartup + 3.0;
                        EditorApplication.update += HandleResumeBatch;
                        return; 
                    }

                    SetStatus($"[{currentIndex + 1}/{total}] Activating {outfitName} ({platform})...", MessageType.Info);
                    _batchSubProgress = 0.2f;
                    Repaint();

                    var outfit = _outfits.FirstOrDefault(o => o.Name == outfitName);
                    if (outfit == null)
                    {
                        // If outfit was deleted from scene mid-batch, skip and continue
                        AddSkipped(outfitName, platform, "no longer in the scene");
                        queue.RemoveAt(0);
                        SessionState.SetString(SESSION_BATCH_QUEUE, string.Join("\n", queue));
                        SessionState.SetInt(SESSION_BATCH_INDEX, currentIndex + 1);
                        continue;
                    }

                    ActivateOutfit(outfit, platform);
                    FlushScene();
                    _batchSubProgress = 0.3f;
                    Repaint();
                    await Task.Delay(1500, _cts.Token);

                    // Double-check platform before upload safeguard
                    if (GetCurrentPlatform() != platform)
                    {
                        throw new Exception($"Critical Safety Check Failed: Queue expected {platform}, but Unity is currently on {GetCurrentPlatform()}.");
                    }

                    GameObject targetAvatarRoot = GetTargetAvatarForPlatform(platform);
                    if (targetAvatarRoot == null)
                        throw new Exception($"Target avatar root for {platform} is not set.");

                    // --- Build & Upload Phase ---
                    SetStatus($"[{currentIndex + 1}/{total}] Building & Uploading {outfitName} ({platform}) using {targetAvatarRoot.name}...", MessageType.Info);
                    _batchSubProgress = 0.4f;
                    Repaint();
                    
                    if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder))
                        throw new Exception("SDK Builder not available.");
                    
                    var avatar = await VRCApi.GetAvatar(blueprintId, cancellationToken: _cts.Token);

                    // Swap the previously uploaded version for the new one inside the description
                    string versionToSet = SessionState.GetString(SESSION_BATCH_VERSION, ""); // Use the version captured at batch start

                    // Only attempt to change the description if a version was actually typed in (not blank)
                    if (!string.IsNullOrWhiteSpace(versionToSet))
                    {
                        string previousVersion = AvatarVersionManager.GetUploadedVersion(blueprintId);
                        string newDesc = AvatarVersionManager.UpdateVersionInDescription(avatar.Description, previousVersion, versionToSet);
                        if (newDesc == null)
                        {
                            Debug.LogWarning($"[OutfitBatchUploader] '{outfitName}': could not find the old version " +
                                             $"{(string.IsNullOrEmpty(previousVersion) ? "" : $"\"{previousVersion}\" ")}in the description - left it unchanged.");
                        }
                        else if (newDesc != avatar.Description)
                        {
                            avatar.Description = newDesc;
                            Debug.Log($"[OutfitBatchUploader] Updating '{outfitName}' description to version: {versionToSet}");
                        }

                        // Save it for this specific outfit's blueprint ID so it's remembered across projects!
                        AvatarVersionManager.SetVersion(blueprintId, versionToSet);
                    }

                    await builder.BuildAndUpload(targetAvatarRoot, avatar, cancellationToken: _cts.Token);

                    if (!string.IsNullOrWhiteSpace(versionToSet))
                        AvatarVersionManager.SetUploadedVersion(blueprintId, versionToSet);

                    // Successful upload! Pop from queue
                    _batchSubProgress = 1.0f;
                    Repaint();
                    queue.RemoveAt(0);
                    SessionState.SetString(SESSION_BATCH_QUEUE, string.Join("\n", queue));
                    SessionState.SetInt(SESSION_BATCH_INDEX, currentIndex + 1);

                    if (queue.Count > 0)
                        await Task.Delay(2000, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                CancelBatch();
            }
            catch (Exception ex)
            {
                HandleBatchError(ex);
            }
            finally
            {
                if (runId == _queueRunId) _queueLoopRunning = false;
            }
        }

        private void HandleBatchError(Exception ex)
        {
            // An error caused by (or racing) a user cancel is just a cancel
            if (_cts != null && _cts.IsCancellationRequested) { CancelBatch(); return; }

            string queueStr = SessionState.GetString(SESSION_BATCH_QUEUE, "");
            if (string.IsNullOrWhiteSpace(queueStr)) { FinishBatch(); return; }

            var queue = queueStr.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            string[] parts = queue[0].Split('|');
            string outfitName = parts[0];
            VRCPlatform platform = (VRCPlatform)Enum.Parse(typeof(VRCPlatform), parts[2]);

            // Only the SDK's own validation / build-blocked exceptions count as content problems to auto-skip
            string exType = ex.GetType().Name;
            bool isValidation = exType == "ValidationException" || exType == "BuildBlockedException";

            string reason   = GetFailureReason(ex);
            string shortMsg = reason.Length > 120 ? reason.Substring(0, 120) + "…" : reason;
            string logMsg   = $"[OutfitBatchUploader] '{outfitName}' ({platform}) failed: {reason}\n\n{ex}";

            PlayErrorSound();

            bool isDirectUpload = SessionState.GetBool(SESSION_IS_DIRECT_UPLOAD, false);
            bool proceedOnError = SessionState.GetBool(SESSION_PROCEED_ON_ERROR, _proceedOnError);

            if (isValidation)
            {
                Debug.LogWarning(logMsg);
                SetStatus($"Skipped {outfitName} — validation error: {shortMsg}", MessageType.Warning);
                AddSkipped(outfitName, platform, $"validation error: {reason}");
                PopQueueAndContinue(queue);
            }
            else if (proceedOnError && !isDirectUpload)
            {
                Debug.LogWarning(logMsg);
                SetStatus($"Skipped {outfitName} ({platform}) — error: {shortMsg}", MessageType.Warning);
                AddSkipped(outfitName, platform, reason);
                PopQueueAndContinue(queue);
            }
            else
            {
                Debug.LogError(logMsg);

                if (isDirectUpload)
                {
                    // Keep the outfit as the user selected it; just stop and show the error
                    CancelBatch($"Error on {outfitName}: {shortMsg}", MessageType.Error, restoreBase: false);
                    return;
                }

                SetStatus($"Error on {outfitName}: {shortMsg}", MessageType.Error);

                bool cont = EditorUtility.DisplayDialog(
                    "Upload Failed",
                    $"Upload failed for '{outfitName}' on {platform}:\n{shortMsg}\n\nContinue with remaining queue?",
                    "Continue", "Stop");

                if (cont)
                {
                    AddSkipped(outfitName, platform, reason);
                    PopQueueAndContinue(queue);
                }
                else
                {
                    CancelBatch($"Batch stopped after an error on {outfitName}: {shortMsg}", MessageType.Error);
                }
            }
        }

        private static void AddSkipped(string outfitName, VRCPlatform platform, string reason)
        {
            // The skipped list is newline-separated, so keep each entry on one line
            reason = Regex.Replace(reason ?? "", @"\s+", " ").Trim();
            if (reason.Length == 0) reason = "unknown error";
            if (reason.Length > 300) reason = reason.Substring(0, 300) + "…";

            Debug.LogWarning($"[OutfitBatchUploader] Skipped '{outfitName}' ({platform}) - reason: {reason}");
            // Stored as outfit<TAB>platform<TAB>reason so the summary can group by outfit (the reason has no tabs left)
            SessionState.SetString(SESSION_SKIPPED, SessionState.GetString(SESSION_SKIPPED, "") +
                                                    $"{outfitName}\t{platform}\t{reason}\n");
        }

        /// <summary>
        /// Builds the Skipped / Failed section, one bullet per outfit. Platforms that failed for the same reason share
        /// a line; otherwise each platform gets its own line.
        /// </summary>
        private static string FormatSkippedSummary(string[] skippedLines)
        {
            var groups = skippedLines
                .Select(line => line.Split(new[] { '\t' }, 3))
                .Select(p => (outfit: p[0], platform: p.Length > 1 ? p[1] : "", reason: p.Length > 2 ? p[2] : ""))
                .GroupBy(e => e.outfit)
                .ToList();

            var sb = new System.Text.StringBuilder();
            sb.Append($"Skipped / Failed ({skippedLines.Length} upload{(skippedLines.Length == 1 ? "" : "s")}, " +
                      $"{groups.Count} outfit{(groups.Count == 1 ? "" : "s")}):");

            foreach (var g in groups)
            {
                var entries = g.ToList();
                bool sameReason = entries.All(e => e.reason == entries[0].reason);

                if (sameReason)
                {
                    string platforms = string.Join(", ", entries.Select(e => e.platform).Where(p => p.Length > 0));
                    sb.Append($"\n• {g.Key}{(platforms.Length > 0 ? " — " + platforms : "")}");
                    if (entries[0].reason.Length > 0) sb.Append($"\n    {entries[0].reason}");
                }
                else
                {
                    sb.Append($"\n• {g.Key}");
                    foreach (var e in entries)
                        sb.Append($"\n    {e.platform}: {(e.reason.Length > 0 ? e.reason : "unknown error")}");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Digs the real cause out of an upload exception: unwraps wrapper exceptions and collects the SDK's
        /// validation error list (ValidationException.Errors), which its Message alone doesn't include.
        /// </summary>
        private static string GetFailureReason(Exception ex)
        {
            while ((ex is AggregateException || ex is TargetInvocationException) && ex.InnerException != null)
                ex = ex.InnerException;

            var errors = new List<string>();
            try
            {
                var type = ex.GetType();
                object value = type.GetProperty("Errors", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ex)
                            ?? type.GetField("Errors", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ex);
                if (value is System.Collections.IEnumerable list && !(value is string))
                {
                    foreach (var item in list)
                    {
                        string s = item?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(s) && !errors.Contains(s)) errors.Add(s);
                    }
                }
            }
            catch { }

            string message = ex.Message?.Trim() ?? "";
            if (errors.Count == 0)
                return string.IsNullOrEmpty(message) ? ex.GetType().Name : message;

            string joined = string.Join("; ", errors);
            return string.IsNullOrEmpty(message) || joined.Contains(message) ? joined : $"{message}: {joined}";
        }

        private void PopQueueAndContinue(List<string> queue)
        {
            queue.RemoveAt(0);
            SessionState.SetString(SESSION_BATCH_QUEUE, string.Join("\n", queue));
            int currentIndex = SessionState.GetInt(SESSION_BATCH_INDEX, 0);
            SessionState.SetInt(SESSION_BATCH_INDEX, currentIndex + 1);

            _ = ProcessBatchQueueAsync();
        }

        private void FinishBatch()
        {
            int total = SessionState.GetInt(SESSION_BATCH_TOTAL, 0);
            string skippedStr = SessionState.GetString(SESSION_SKIPPED, "");
            var skippedList = skippedStr.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            int succeeded = total - skippedList.Length;
            if (succeeded < 0) succeeded = 0;

            bool isDirectUpload = SessionState.GetBool(SESSION_IS_DIRECT_UPLOAD, false);
            SessionState.EraseBool(SESSION_IS_DIRECT_UPLOAD);

            string summary = isDirectUpload
                ? $"Upload complete — {succeeded}/{total} uploads finished."
                : $"Queue complete — {succeeded}/{total} uploads finished.";
            if (skippedList.Length > 0)
            {
                summary += "\n\n" + FormatSkippedSummary(skippedList) +
                           "\n\nFix the issues on those outfits and upload them separately.";
            }

            var finalType = skippedList.Length > 0 || succeeded < total ? MessageType.Warning : MessageType.Info;
            SessionState.SetString(SESSION_FINAL_STATUS_MSG, summary);
            SessionState.SetInt(SESSION_FINAL_STATUS_TYPE, (int)finalType);

            if (succeeded > 0 && succeeded == total)
            {
                SessionState.SetBool(SESSION_PLAY_SOUND_ON_WAKE, true);
            }
            else if (succeeded < total)
            {
                SessionState.SetBool(SESSION_PLAY_ERROR_SOUND_ON_WAKE, true);
            }

            bool shouldReset = !isDirectUpload && _resetToFirstOutfit;
            if (shouldReset)
            {
                ResetToFirstOutfit(true);
            }

            SessionState.SetBool(SESSION_BATCH_ACTIVE, false);
            _isBatchUploading = false;
            _batchIndex = _batchTotal;
            
            if (skippedList.Length > 0 || succeeded < total)
                Debug.LogWarning($"[OutfitBatchUploader] {summary}");

            if (!RestoreInitialPlatform())
            {
                // No domain reload is coming, so we can fire the handler logic immediately.
                HandleFinishedBatch();
            }
            else
            {
                if (shouldReset)
                {
                    SessionState.SetBool(SESSION_RESET_ON_WAKE, true);
                }

                // A switch is coming. Clear the current status so it doesn't show a stale message before reload.
                SetStatus("", MessageType.None);
                Repaint();
            }
        }

        /// <summary>Cancel button: a running queue loop is only signalled, so it cleans up once the
        /// current build has actually stopped. A queue waiting to resume is cleaned up right away.</summary>
        private void RequestCancelBatch()
        {
            if (_queueLoopRunning && _cts != null)
            {
                _cts.Cancel();
                SetStatus("Cancelling — waiting for the current step to stop...", MessageType.Warning);
                Repaint();
            }
            else
            {
                CancelBatch();
            }
        }

        private void CancelBatch(string message = "Batch upload cancelled.", MessageType type = MessageType.Warning, bool restoreBase = true)
        {
            EditorApplication.update -= HandleResumeBatch;
            SessionState.SetBool(SESSION_BATCH_ACTIVE, false);
            SessionState.EraseBool(SESSION_IS_DIRECT_UPLOAD);
            SessionState.EraseBool(SESSION_RESET_ON_WAKE);
            _isBatchUploading = false;

            if (restoreBase)
            {
                // Don't leave the last outfit's material / blendshape overrides in the saved scene
                RevertActiveOverrides(true);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            }

            if (RestoreInitialPlatform())
            {
                // A domain reload is coming; show the message once it has finished
                SessionState.SetString(SESSION_FINAL_STATUS_MSG, message);
                SessionState.SetInt(SESSION_FINAL_STATUS_TYPE, (int)type);
            }
            else
            {
                SetStatus(message, type);
            }
            Repaint();
        }

        private bool RestoreInitialPlatform()
        {
            string initialPlatStr = SessionState.GetString(SESSION_INITIAL_PLATFORM, "");
            if (string.IsNullOrEmpty(initialPlatStr)) return false;

            bool switched = false;
            if (Enum.TryParse(initialPlatStr, out VRCPlatform initialPlat) && initialPlat != GetCurrentPlatform())
            {
                SetStatus($"Restoring initial platform to {initialPlat}...", MessageType.Info);
                Repaint();
                switched = SwitchPlatform(initialPlat);
            }

            SessionState.EraseString(SESSION_INITIAL_PLATFORM); // Clean up regardless
            return switched;
        }

        // ============================================================
        //  Helpers
        // ============================================================
        private void SetStatus(string msg, MessageType type)
        {
            _statusMessage = msg;
            _statusType    = type;
        }

        private static void DrawSeparator()
        {
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        }

        private void InitStyles()
        {
            if (_stylesInited) return;
            _stylesInited = true;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize  = 15,
                alignment = TextAnchor.MiddleLeft
            };

            _activeRowStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(6, 6, 4, 4),
                margin  = new RectOffset(0, 0, 0, 0),
                normal  = { background = MakeTex(2, 2, new Color(0.15f, 0.45f, 0.15f, 0.35f)) }
            };

            _inactiveRowStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(6, 6, 4, 4),
                margin  = new RectOffset(0, 0, 0, 0)
            };
        }

        private static Texture2D MakeTex(int w, int h, Color col)
        {
            var pix = new Color[w * h];
            for (int i = 0; i < pix.Length; i++) pix[i] = col;
            var t = new Texture2D(w, h);
            t.SetPixels(pix);
            t.Apply();
            return t;
        }

        // ============================================================
        //  Data
        // ============================================================
        [Serializable]
        public class OutfitEntry
        {
            public GameObject                  Go;
            public string                      Name;
            public string                      BlueprintId      = "";
            public bool                        IncludeInBatch   = true;
            public bool                        BuildWindows     = true;
            public bool                        BuildAndroid     = false;
            public bool                        BuildIOS         = false;
            public string                      PrefsKey         = "";   // scoped by avatar name
            // Blendshape overrides: name → value (0-100). Only entries present here are applied.
            public Dictionary<string, float>   BlendShapes      = new Dictionary<string, float>();
            public bool                        BlendShapeExpanded = false;
            public string                      BlendShapeSearch   = "";
            
            // Material Overrides
            public List<MaterialOverride>      MaterialOverrides  = new List<MaterialOverride>();
            public bool                        MaterialExpanded   = false;
        }

        [Serializable]
        public class MaterialOverride
        {
            public string RendererPath = "";
            public int MaterialSlot = 0;
            public string OverrideMatGuid = "";

            public string AndroidRendererPath = "";
            public int AndroidMaterialSlot = 0;
            public string AndroidOverrideMatGuid = "";

            [NonSerialized] public Renderer TargetRenderer;
            [NonSerialized] public Material OverrideMat;

            [NonSerialized] public Renderer AndroidTargetRenderer;
            [NonSerialized] public Material AndroidOverrideMat;

            [NonSerialized] public bool Resolved;
        }

        [Serializable]
        private class ActiveMatOverride
        {
            public string RendererPath;
            public int Slot;
            public string OriginalMatGuid;   // kept so backups stay readable by older versions
            public string OriginalMatId;     // GlobalObjectId - exact for sub-assets and scene-only materials
        }

        [Serializable]
        private class ActiveBlendShape
        {
            public string SkinPath;          // relative to the avatar root ("" = the root itself)
            public string Name;
            public float Value;
        }

        /// <summary>Base values replaced by the active outfit's overrides, per avatar root
        /// (stored in the *_BaseMaterials.json file; the name predates blendshape support).</summary>
        [Serializable]
        private class ActiveOverridesBackup
        {
            public List<ActiveMatOverride> Overrides = new List<ActiveMatOverride>();
            public List<ActiveBlendShape> BlendShapes = new List<ActiveBlendShape>();
        }

        public enum VRCPlatform
        {
            Windows,
            Android,
            iOS
        }
    }

    public static class AvatarVersionManager
    {
        private static readonly string ConfigPath;
        private static Dictionary<string, string> _versions;
        private static Dictionary<string, string> _uploaded;

        [Serializable]
        private class VersionData
        {
            public List<VersionEntry> versions = new List<VersionEntry>();
            public List<VersionEntry> uploaded = new List<VersionEntry>();   // last version actually uploaded
        }

        [Serializable]
        private class VersionEntry
        {
            public string blueprintId;
            public string version;
        }

        // VRChat stores the dots in descriptions as look-alikes (U+2024 "one dot leader"), so any of these counts as a dot
        private const string Dot = @"[.․．]";

        // A version-looking token: "v1", "V2.3", "1.0.2" (bare whole numbers are too ambiguous)
        private static readonly Regex VersionTokenRegex =
            new Regex(@"(?<![\w.․．])(?:[vV]\d+(?:" + Dot + @"\d+)*|\d+(?:" + Dot + @"\d+)+)(?![\w]|" + Dot + @"\d)");

        static AvatarVersionManager()
        {
            // Save locally to this specific Unity project in the ProjectSettings folder
            ConfigPath = Path.Combine("ProjectSettings", "ShiroOutfit_versions.json");
            LoadVersions();
        }

        private static void LoadVersions()
        {
            _versions = new Dictionary<string, string>();
            _uploaded = new Dictionary<string, string>();
            if (!File.Exists(ConfigPath)) return;

            try
            {
                string json = File.ReadAllText(ConfigPath);
                var data = JsonUtility.FromJson<VersionData>(json);
                if (data?.versions != null)
                {
                    foreach (var entry in data.versions)
                        if (!string.IsNullOrWhiteSpace(entry.blueprintId))
                            _versions[entry.blueprintId] = entry.version;
                }
                if (data?.uploaded != null)
                {
                    foreach (var entry in data.uploaded)
                        if (!string.IsNullOrWhiteSpace(entry.blueprintId))
                            _uploaded[entry.blueprintId] = entry.version;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AvatarVersionManager] Failed to load versions: {ex.Message}");
            }
        }

        private static void SaveVersions()
        {
            try
            {
                var data = new VersionData();
                foreach (var kvp in _versions)
                    data.versions.Add(new VersionEntry { blueprintId = kvp.Key, version = kvp.Value });
                foreach (var kvp in _uploaded)
                    data.uploaded.Add(new VersionEntry { blueprintId = kvp.Key, version = kvp.Value });

                string json = JsonUtility.ToJson(data, true);
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AvatarVersionManager] Failed to save versions: {ex.Message}");
            }
        }

        public static string GetVersion(string blueprintId)
        {
            if (string.IsNullOrWhiteSpace(blueprintId)) return "";
            _versions.TryGetValue(blueprintId, out string version);
            return version ?? "";
        }

        public static void SetVersion(string blueprintId, string version)
        {
            if (string.IsNullOrWhiteSpace(blueprintId)) return;
            _versions[blueprintId] = version;
            SaveVersions();
        }

        /// <summary>The version last written into this avatar's description by a successful upload.</summary>
        public static string GetUploadedVersion(string blueprintId)
        {
            if (string.IsNullOrWhiteSpace(blueprintId)) return "";
            _uploaded.TryGetValue(blueprintId, out string version);
            return version ?? "";
        }

        public static void SetUploadedVersion(string blueprintId, string version)
        {
            if (string.IsNullOrWhiteSpace(blueprintId)) return;
            _uploaded[blueprintId] = version;
            SaveVersions();
        }

        /// <summary>
        /// Replaces the old version inside a description with the new one, leaving the rest of the text alone.
        /// Returns null when the old version can't be found (the caller leaves the description unchanged).
        /// </summary>
        public static string UpdateVersionInDescription(string description, string oldVersion, string newVersion)
        {
            if (string.IsNullOrWhiteSpace(description)) return newVersion;

            if (!string.IsNullOrWhiteSpace(oldVersion))
            {
                if (oldVersion == newVersion) return description;

                // Whole-token match: updating "1" must not touch "10", and "v1" must not touch "v1.1"
                string oldPattern = Regex.Escape(oldVersion.Replace('․', '.').Replace('．', '.')).Replace(@"\.", Dot);
                var oldRegex = new Regex(@"(?<![\w.․．])" + oldPattern + @"(?![\w]|" + Dot + @"\d)");
                if (!oldRegex.IsMatch(description)) return null;
                return oldRegex.Replace(description, newVersion.Replace("$", "$$"));
            }

            // No upload recorded yet: only replace when there's exactly one version-looking token
            var matches = VersionTokenRegex.Matches(description);
            if (matches.Count != 1) return null;
            var m = matches[0];
            return description.Substring(0, m.Index) + newVersion + description.Substring(m.Index + m.Length);
        }
    }

    [InitializeOnLoad]
    public static class BatchUploaderLegacyMigration
    {
        private const string MigrationPromptedKey = "Synthos_BatchUploader_Migrated_v1_0_0";
        private const string LegacyDir = "Assets/VRC_Batch_Uploader";
        private const string SettingsDir = "ProjectSettings/VRC_Batch_Uploader";

        static BatchUploaderLegacyMigration()
        {
            EditorApplication.delayCall += CheckLegacyInstallation;
        }

        private static void CheckLegacyInstallation()
        {
            if (!Directory.Exists(LegacyDir)) return;

            // Copy legacy data out first; never offer to delete the folder unless that worked
            if (!MigrateLegacyData()) return;

            // Only prompt once per project session
            if (SessionState.GetBool(MigrationPromptedKey, false)) return;
            SessionState.SetBool(MigrationPromptedKey, true);

            bool remove = EditorUtility.DisplayDialog(
                "Synthos Batch Uploader",
                "Synthos Batch Uploader is now active as a modern package.\n\n" +
                "A legacy script folder was detected at 'Assets/VRC_Batch_Uploader'.\n\n" +
                "Its outfit data (material overrides and any in-progress base material backups) has been copied to 'ProjectSettings/VRC_Batch_Uploader'.\n\n" +
                "Would you like to move the redundant 'Assets/VRC_Batch_Uploader' folder to the trash now?",
                "Move to Trash",
                "Keep for Now"
            );

            if (remove)
            {
                if (AssetDatabase.MoveAssetToTrash(LegacyDir))
                    Debug.Log("[SYNTHOS BATCH UPLOADER] Moved obsolete legacy folder to the trash: Assets/VRC_Batch_Uploader");
                else
                    Debug.LogWarning("[SYNTHOS BATCH UPLOADER] Could not move Assets/VRC_Batch_Uploader to the trash - remove it manually.");
            }
        }

        /// <summary>
        /// Copies legacy *_MatOverrides.json files to ProjectSettings (where the loader already looks first) and moves
        /// in-progress *_BaseMaterials.json backups there. Returns false if anything failed.
        /// </summary>
        private static bool MigrateLegacyData()
        {
            string legacyData = Path.Combine(LegacyDir, "Data");
            if (!Directory.Exists(legacyData)) return true;

            bool ok = true;
            bool movedAny = false;

            foreach (string src in Directory.GetFiles(legacyData, "*_MatOverrides.json"))
            {
                try
                {
                    Directory.CreateDirectory(SettingsDir);
                    string dst = Path.Combine(SettingsDir, Path.GetFileName(src));
                    if (!File.Exists(dst)) File.Copy(src, dst);
                }
                catch (Exception ex)
                {
                    ok = false;
                    Debug.LogError($"[SYNTHOS BATCH UPLOADER] Could not migrate {src}: {ex.Message}");
                }
            }

            // Moved rather than copied: a stale copy left behind would later "restore" the wrong materials
            foreach (string src in Directory.GetFiles(legacyData, "*_BaseMaterials.json"))
            {
                try
                {
                    string dstDir = Path.Combine(SettingsDir, "Data");
                    Directory.CreateDirectory(dstDir);
                    string dst = Path.Combine(dstDir, Path.GetFileName(src));
                    if (File.Exists(dst)) File.Delete(dst);   // the legacy file is the one the tool was using
                    File.Move(src, dst);
                    if (File.Exists(src + ".meta")) File.Delete(src + ".meta");
                    movedAny = true;
                }
                catch (Exception ex)
                {
                    ok = false;
                    Debug.LogError($"[SYNTHOS BATCH UPLOADER] Could not migrate {src}: {ex.Message}");
                }
            }

            if (movedAny) AssetDatabase.Refresh();
            return ok;
        }
    }
}
