using UnityEngine;
using System;
using System.Collections.Generic;
using System.Globalization;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace JayT.VRChatAvatarHelper.Facial
{
    /// <summary>
    /// iFacialMocapから受信したデータをアバターのBlendShapeに適用します。
    /// アバターごとに個別のマッピング上書きが設定できます。
    /// </summary>
    public class IFacialMocapBlendShapeApplier : MonoBehaviour
    {
        /// <summary>
        /// iFacialMocapパラメーター名 → アバター側BlendShape名 の1エントリ。
        /// ifmName はInspectorのドロップダウンで選択できます。
        /// </summary>
        [Serializable]
        public struct MappingEntry
        {
            [Tooltip("iFacialMocapが送るパラメーター名 (ドロップダウンで選択)")]
            public string ifmName;
            [Tooltip("対応させるアバター側のBlendShape名")]
            public string blendShapeName;
        }

        /// <summary>アバター1体分の設定</summary>
        [Serializable]
        public class AvatarTarget
        {
            [Tooltip("BlendShapeを適用するSkinnedMeshRenderer")]
            public SkinnedMeshRenderer renderer;

            [Tooltip("このアバター固有のマッピング上書き。\n" +
                     "固定テーブルより優先されます。\n" +
                     "左: iFacialMocap名 / 右: アバター側BlendShape名")]
            public MappingEntry[] mappingOverrides;
        }

        [Header("Source")]
        [Tooltip("データを受け取るIFacialMocapReceiverコンポーネント")]
        public IFacialMocapReceiver receiver;

        [Header("Targets")]
        [Tooltip("アバターごとの設定 (複数アバターに同時適用可)")]
        public AvatarTarget[] avatarTargets;

        [Header("Settings")]
        [Tooltip("BlendShape値の倍率 (iFacialMocapは0–100スケール)")]
        [Range(0f, 2f)]
        public float weightScale = 1f;

        // iFacialMocap省略名 (_L/_R) → ARKit標準名 (Left/Right) の固定マッピング
        private static readonly Dictionary<string, string> NameMapping = new Dictionary<string, string>
        {
            { "eyeBlink_L",        "eyeBlinkLeft" },
            { "eyeBlink_R",        "eyeBlinkRight" },
            { "eyeWide_L",         "eyeWideLeft" },
            { "eyeWide_R",         "eyeWideRight" },
            { "eyeSquint_L",       "eyeSquintLeft" },
            { "eyeSquint_R",       "eyeSquintRight" },
            { "eyeLookUp_L",       "eyeLookUpLeft" },
            { "eyeLookUp_R",       "eyeLookUpRight" },
            { "eyeLookDown_L",     "eyeLookDownLeft" },
            { "eyeLookDown_R",     "eyeLookDownRight" },
            { "eyeLookIn_L",       "eyeLookInLeft" },
            { "eyeLookIn_R",       "eyeLookInRight" },
            { "eyeLookOut_L",      "eyeLookOutLeft" },
            { "eyeLookOut_R",      "eyeLookOutRight" },
            { "browDown_L",        "browDownLeft" },
            { "browDown_R",        "browDownRight" },
            { "browOuterUp_L",     "browOuterUpLeft" },
            { "browOuterUp_R",     "browOuterUpRight" },
            { "cheekSquint_L",     "cheekSquintLeft" },
            { "cheekSquint_R",     "cheekSquintRight" },
            { "noseSneer_L",       "noseSneerLeft" },
            { "noseSneer_R",       "noseSneerRight" },
            { "mouthSmile_L",      "mouthSmileLeft" },
            { "mouthSmile_R",      "mouthSmileRight" },
            { "mouthFrown_L",      "mouthFrownLeft" },
            { "mouthFrown_R",      "mouthFrownRight" },
            { "mouthDimple_L",     "mouthDimpleLeft" },
            { "mouthDimple_R",     "mouthDimpleRight" },
            { "mouthUpperUp_L",    "mouthUpperUpLeft" },
            { "mouthUpperUp_R",    "mouthUpperUpRight" },
            { "mouthLowerDown_L",  "mouthLowerDownLeft" },
            { "mouthLowerDown_R",  "mouthLowerDownRight" },
            { "mouthPress_L",      "mouthPressLeft" },
            { "mouthPress_R",      "mouthPressRight" },
            { "mouthStretch_L",    "mouthStretchLeft" },
            { "mouthStretch_R",    "mouthStretchRight" },
        };

        // アバターごとのランタイムキャッシュ
        private class AvatarCache
        {
            public SkinnedMeshRenderer smr;
            public Dictionary<string, int>    blendShapeIndex; // BlendShape名 → インデックス
            public Dictionary<string, string> overrideMap;     // ifmName → BlendShape名 (per-avatar)
        }

        private List<AvatarCache> avatarCaches;

        void OnEnable()
        {
            BuildCaches();

            if (receiver != null)
                receiver.OnDataReceived += HandleData;
            else
                Debug.LogWarning("[IFacialMocapBlendShapeApplier] receiver が設定されていません。", this);
        }

        void OnDisable()
        {
            if (receiver != null)
                receiver.OnDataReceived -= HandleData;
        }

        private void BuildCaches()
        {
            avatarCaches = new List<AvatarCache>();

            if (avatarTargets == null) return;

            foreach (var target in avatarTargets)
            {
                if (target == null || target.renderer == null || target.renderer.sharedMesh == null) continue;

                var cache = new AvatarCache
                {
                    smr             = target.renderer,
                    blendShapeIndex = new Dictionary<string, int>(StringComparer.Ordinal),
                    overrideMap     = new Dictionary<string, string>(StringComparer.Ordinal),
                };

                var mesh = target.renderer.sharedMesh;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                    cache.blendShapeIndex[mesh.GetBlendShapeName(i)] = i;

                if (target.mappingOverrides != null)
                {
                    foreach (var entry in target.mappingOverrides)
                    {
                        if (!string.IsNullOrEmpty(entry.ifmName) && !string.IsNullOrEmpty(entry.blendShapeName))
                            cache.overrideMap[entry.ifmName] = entry.blendShapeName;
                    }
                }

                avatarCaches.Add(cache);
            }

            Debug.Log($"[IFacialMocapBlendShapeApplier] {avatarCaches.Count} アバター分のキャッシュ構築完了", this);
        }

        // メインスレッドから呼ばれる (IFacialMocapReceiver.OnDataReceived)
        private void HandleData(string data)
        {
            if (string.IsNullOrEmpty(data)) return;

            var tokens = data.Split('|');
            foreach (var token in tokens)
            {
                if (token.IndexOf('#') >= 0) continue;

                int sep = token.LastIndexOf('-');
                if (sep <= 0) continue;

                string ifmName  = token.Substring(0, sep);
                string valueStr = token.Substring(sep + 1);

                if (!float.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                    continue;

                val *= weightScale;

                foreach (var cache in avatarCaches)
                    ApplyToAvatar(cache, ifmName, val);
            }
        }

        private void ApplyToAvatar(AvatarCache cache, string ifmName, float val)
        {
            // 1. アバター固有のOverride (最優先)
            if (cache.overrideMap.TryGetValue(ifmName, out string overrideName))
            {
                TryApply(cache, overrideName, val);
                return;
            }

            // 2. 直接マッチ
            if (TryApply(cache, ifmName, val)) return;

            // 3. 固定マッピングテーブル
            if (NameMapping.TryGetValue(ifmName, out string arKitName))
                TryApply(cache, arKitName, val);
        }

        private bool TryApply(AvatarCache cache, string blendShapeName, float val)
        {
            if (!cache.blendShapeIndex.TryGetValue(blendShapeName, out int idx))
                return false;

            cache.smr.SetBlendShapeWeight(idx, val);
            return true;
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// MappingEntry の ifmName をドロップダウンで選択できるようにするPropertyDrawer。
    /// </summary>
    [CustomPropertyDrawer(typeof(IFacialMocapBlendShapeApplier.MappingEntry))]
    public class MappingEntryDrawer : PropertyDrawer
    {
        // iFacialMocapが送りうる全パラメーター名
        private static readonly string[] IfmNames = new string[]
        {
            // iFacialMocap省略名 (_L/_R形式)
            "eyeBlink_L",       "eyeBlink_R",
            "eyeWide_L",        "eyeWide_R",
            "eyeSquint_L",      "eyeSquint_R",
            "eyeLookUp_L",      "eyeLookUp_R",
            "eyeLookDown_L",    "eyeLookDown_R",
            "eyeLookIn_L",      "eyeLookIn_R",
            "eyeLookOut_L",     "eyeLookOut_R",
            "browDown_L",       "browDown_R",
            "browOuterUp_L",    "browOuterUp_R",
            "cheekSquint_L",    "cheekSquint_R",
            "noseSneer_L",      "noseSneer_R",
            "mouthSmile_L",     "mouthSmile_R",
            "mouthFrown_L",     "mouthFrown_R",
            "mouthDimple_L",    "mouthDimple_R",
            "mouthUpperUp_L",   "mouthUpperUp_R",
            "mouthLowerDown_L", "mouthLowerDown_R",
            "mouthPress_L",     "mouthPress_R",
            "mouthStretch_L",   "mouthStretch_R",
            // ARKit標準名 (対称系)
            "browInnerUp",
            "cheekPuff",
            "jawOpen", "jawForward", "jawLeft", "jawRight",
            "mouthFunnel", "mouthPucker", "mouthLeft", "mouthRight",
            "mouthRollUpper", "mouthRollLower",
            "mouthShrugUpper", "mouthShrugLower",
            "mouthClose",
            "tongueOut",
            // ARKit標準名 (Left/Right形式)
            "eyeBlinkLeft",       "eyeBlinkRight",
            "eyeWideLeft",        "eyeWideRight",
            "eyeSquintLeft",      "eyeSquintRight",
            "eyeLookUpLeft",      "eyeLookUpRight",
            "eyeLookDownLeft",    "eyeLookDownRight",
            "eyeLookInLeft",      "eyeLookInRight",
            "eyeLookOutLeft",     "eyeLookOutRight",
            "browDownLeft",       "browDownRight",
            "browOuterUpLeft",    "browOuterUpRight",
            "cheekSquintLeft",    "cheekSquintRight",
            "noseSneerLeft",      "noseSneerRight",
            "mouthSmileLeft",     "mouthSmileRight",
            "mouthFrownLeft",     "mouthFrownRight",
            "mouthDimpleLeft",    "mouthDimpleRight",
            "mouthUpperUpLeft",   "mouthUpperUpRight",
            "mouthLowerDownLeft", "mouthLowerDownRight",
            "mouthPressLeft",     "mouthPressRight",
            "mouthStretchLeft",   "mouthStretchRight",
        };

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var ifmProp = property.FindPropertyRelative("ifmName");
            var bsProp  = property.FindPropertyRelative("blendShapeName");

            float half    = (position.width - 4f) * 0.5f;
            var leftRect  = new Rect(position.x,             position.y, half, position.height);
            var rightRect = new Rect(position.x + half + 4f, position.y, half, position.height);

            // ifmName: Popup。リストにない値は "(カスタム: xxx)" として先頭に表示
            string current    = ifmProp.stringValue ?? "";
            int    currentIdx = Array.IndexOf(IfmNames, current);

            string[] displayNames;
            int      displayIdx;
            if (currentIdx >= 0)
            {
                displayNames = IfmNames;
                displayIdx   = currentIdx;
            }
            else
            {
                displayNames    = new string[IfmNames.Length + 1];
                displayNames[0] = string.IsNullOrEmpty(current) ? "(選択してください)" : $"(カスタム: {current})";
                Array.Copy(IfmNames, 0, displayNames, 1, IfmNames.Length);
                displayIdx = 0;
            }

            int selected = EditorGUI.Popup(leftRect, displayIdx, displayNames);

            if (currentIdx >= 0)
                ifmProp.stringValue = IfmNames[selected];
            else if (selected > 0)
                ifmProp.stringValue = IfmNames[selected - 1];
            // selected == 0 (カスタム値) の場合は値を維持

            // blendShapeName: テキストフィールド
            bsProp.stringValue = EditorGUI.TextField(rightRect, bsProp.stringValue ?? "");

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUIUtility.singleLineHeight;
        }
    }
#endif
}
