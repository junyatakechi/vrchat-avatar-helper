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
    /// アバターごとに個別のマッピング上書きとリミッターが設定できます。
    /// </summary>
    public class IFacialMocapBlendShapeApplier : MonoBehaviour
    {
        /// <summary>iFacialMocapパラメーター名 → アバター側BlendShape名 の1エントリ</summary>
        [Serializable]
        public struct MappingEntry
        {
            [Tooltip("iFacialMocapが送るパラメーター名 (ドロップダウンで選択)")]
            public string ifmName;
            [Tooltip("対応させるアバター側のBlendShape名")]
            public string blendShapeName;
        }

        /// <summary>
        /// iFacialMocapパラメーターの値を [min, max] でクランプするリミッター。
        /// weightTrackingScale 適用後の値に対してクランプします。
        /// </summary>
        [Serializable]
        public struct LimitEntry
        {
            [Tooltip("リミッターを適用するiFacialMocapパラメーター名 (ドロップダウンで選択)")]
            public string ifmName;
            [Tooltip("クランプ最小値 (デフォルト: 0)")]
            public float min;
            [Tooltip("クランプ最大値 (デフォルト: 100)")]
            public float max;
            // Inspector上での初期化済みフラグ (デフォルト値の自動設定に使用)
            [HideInInspector] public bool initialized;
        }

        /// <summary>アバター1体分の設定</summary>
        [Serializable]
        public class AvatarTarget
        {
            [Tooltip("BlendShapeを適用するSkinnedMeshRenderer")]
            public SkinnedMeshRenderer renderer;

            [Tooltip("このアバター固有のマッピング上書き。固定テーブルより優先されます。\n" +
                     "左: iFacialMocap名 / 右: アバター側BlendShape名")]
            public MappingEntry[] mappingOverrides;

            [Tooltip("このアバター固有のリミッター。再生中も即時反映されます。\n" +
                     "値は weightTrackingScale 適用後にクランプされます。\n" +
                     "左: iFacialMocap名 / 中: 最小値 / 右: 最大値")]
            public LimitEntry[] limiters;
        }

        [Header("Source")]
        [Tooltip("データを受け取るIFacialMocapReceiverコンポーネント")]
        public IFacialMocapReceiver receiver;

        [Header("Targets")]
        [Tooltip("アバターごとの設定 (複数アバターに同時適用可)")]
        public AvatarTarget[] avatarTargets;

        [Header("Settings")]
        [Tooltip("トラッキング生値 (0–100) に最前段で掛ける倍率。\n" +
                 "例: 0.5 で動きを抑制、1.5 で大げさに。リミッターはこの後に適用されます。")]
        [Range(0f, 4f)]
        public float weightTrackingScale = 1f;

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

        // アバターごとのランタイムキャッシュ (BlendShapeインデックスとOverrideのみ。Limiterは毎回 avatarTargets から直接読む)
        private class AvatarCache
        {
            public int             targetIdx;       // avatarTargets 配列内のインデックス
            public SkinnedMeshRenderer smr;
            public Dictionary<string, int>    blendShapeIndex;
            public Dictionary<string, string> overrideMap;
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

            for (int i = 0; i < avatarTargets.Length; i++)
            {
                var target = avatarTargets[i];
                if (target == null || target.renderer == null || target.renderer.sharedMesh == null) continue;

                var cache = new AvatarCache
                {
                    targetIdx       = i,
                    smr             = target.renderer,
                    blendShapeIndex = new Dictionary<string, int>(StringComparer.Ordinal),
                    overrideMap     = new Dictionary<string, string>(StringComparer.Ordinal),
                };

                var mesh = target.renderer.sharedMesh;
                for (int b = 0; b < mesh.blendShapeCount; b++)
                    cache.blendShapeIndex[mesh.GetBlendShapeName(b)] = b;

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

                // 最前段: トラッキング生値に weightTrackingScale を乗算
                val *= weightTrackingScale;

                foreach (var cache in avatarCaches)
                    ApplyToAvatar(cache, ifmName, val);
            }
        }

        private void ApplyToAvatar(AvatarCache cache, string ifmName, float val)
        {
            // リミッターを avatarTargets から直接読む (再生中の変更が即時反映される)
            var limiters = avatarTargets[cache.targetIdx].limiters;
            if (limiters != null)
            {
                for (int i = 0; i < limiters.Length; i++)
                {
                    if (limiters[i].ifmName == ifmName)
                    {
                        val = Mathf.Clamp(val, limiters[i].min, limiters[i].max);
                        break;
                    }
                }
            }

            // Override マッピング (最優先)
            if (cache.overrideMap.TryGetValue(ifmName, out string overrideName))
            {
                TryApply(cache, overrideName, val);
                return;
            }

            // 直接マッチ
            if (TryApply(cache, ifmName, val)) return;

            // 固定マッピングテーブル
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
    // iFacialMocapが送りうる全パラメーター名 (PropertyDrawer 間で共有)
    internal static class IFacialMocapIfmNames
    {
        internal static readonly string[] All = new string[]
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

        internal static void DrawIfmNamePopup(Rect rect, SerializedProperty ifmProp)
        {
            string current    = ifmProp.stringValue ?? "";
            int    currentIdx = Array.IndexOf(All, current);

            string[] displayNames;
            int      displayIdx;
            if (currentIdx >= 0)
            {
                displayNames = All;
                displayIdx   = currentIdx;
            }
            else
            {
                displayNames    = new string[All.Length + 1];
                displayNames[0] = string.IsNullOrEmpty(current) ? "(選択してください)" : $"(カスタム: {current})";
                Array.Copy(All, 0, displayNames, 1, All.Length);
                displayIdx = 0;
            }

            int selected = EditorGUI.Popup(rect, displayIdx, displayNames);

            if (currentIdx >= 0)
                ifmProp.stringValue = All[selected];
            else if (selected > 0)
                ifmProp.stringValue = All[selected - 1];
        }
    }

    [CustomPropertyDrawer(typeof(IFacialMocapBlendShapeApplier.MappingEntry))]
    public class MappingEntryDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var ifmProp = property.FindPropertyRelative("ifmName");
            var bsProp  = property.FindPropertyRelative("blendShapeName");

            float half    = (position.width - 4f) * 0.5f;
            var leftRect  = new Rect(position.x,             position.y, half, position.height);
            var rightRect = new Rect(position.x + half + 4f, position.y, half, position.height);

            IFacialMocapIfmNames.DrawIfmNamePopup(leftRect, ifmProp);
            bsProp.stringValue = EditorGUI.TextField(rightRect, bsProp.stringValue ?? "");

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
            => EditorGUIUtility.singleLineHeight;
    }

    [CustomPropertyDrawer(typeof(IFacialMocapBlendShapeApplier.LimitEntry))]
    public class LimitEntryDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var ifmProp  = property.FindPropertyRelative("ifmName");
            var minProp  = property.FindPropertyRelative("min");
            var maxProp  = property.FindPropertyRelative("max");
            var initProp = property.FindPropertyRelative("initialized");

            // 新規追加時のデフォルト値設定 (min=0, max=100)
            if (!initProp.boolValue)
            {
                initProp.boolValue  = true;
                minProp.floatValue  = 0f;
                maxProp.floatValue  = 100f;
            }

            float w      = position.width;
            float labelW = 26f;
            float numW   = (w * 0.5f - labelW * 2f) * 0.5f;
            float popupW = w * 0.5f - 4f;
            float x      = position.x;
            float y      = position.y;
            float h      = position.height;

            IFacialMocapIfmNames.DrawIfmNamePopup(new Rect(x, y, popupW, h), ifmProp);
            x += popupW + 4f;

            EditorGUI.LabelField(new Rect(x, y, labelW, h), "min");
            x += labelW;
            minProp.floatValue = EditorGUI.FloatField(new Rect(x, y, numW, h), minProp.floatValue);
            x += numW + 2f;

            EditorGUI.LabelField(new Rect(x, y, labelW, h), "max");
            x += labelW;
            maxProp.floatValue = EditorGUI.FloatField(new Rect(x, y, numW, h), maxProp.floatValue);

            // min <= max を保証
            if (minProp.floatValue > maxProp.floatValue)
                maxProp.floatValue = minProp.floatValue;

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
            => EditorGUIUtility.singleLineHeight;
    }
#endif
}
