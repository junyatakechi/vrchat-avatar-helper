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

        /// <summary>iFacialMocapパラメーターの値を [min, max] でクランプするリミッター</summary>
        [Serializable]
        public struct LimitEntry
        {
            [Tooltip("リミッターを適用するiFacialMocapパラメーター名 (ドロップダウンで選択)")]
            public string ifmName;
            [Tooltip("値の最小値 (iFacialMocapの生値は0–100)")]
            public float min;
            [Tooltip("値の最大値 (iFacialMocapの生値は0–100)")]
            public float max;
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

            [Tooltip("このアバター固有のリミッター。weightScale適用前の生値(0–100)をクランプします。\n" +
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
        [Tooltip("BlendShape値のグローバル倍率。リミッター適用後に掛け算されます。")]
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
            public Dictionary<string, int>              blendShapeIndex; // BlendShape名 → インデックス
            public Dictionary<string, string>           overrideMap;     // ifmName → BlendShape名
            public Dictionary<string, (float min, float max)> limiterMap; // ifmName → (min, max)
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
                    limiterMap      = new Dictionary<string, (float, float)>(StringComparer.Ordinal),
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

                if (target.limiters != null)
                {
                    foreach (var entry in target.limiters)
                    {
                        if (!string.IsNullOrEmpty(entry.ifmName))
                            cache.limiterMap[entry.ifmName] = (entry.min, entry.max);
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

                // weightScale はアバターごとのリミッター適用後に掛けるため、ここでは生値を渡す
                foreach (var cache in avatarCaches)
                    ApplyToAvatar(cache, ifmName, val);
            }
        }

        private void ApplyToAvatar(AvatarCache cache, string ifmName, float val)
        {
            // 1. アバター固有のリミッター (生値 0–100 に対してクランプ)
            if (cache.limiterMap.TryGetValue(ifmName, out var limit))
                val = Mathf.Clamp(val, limit.min, limit.max);

            // 2. グローバル倍率
            val *= weightScale;

            // 3. アバター固有のOverride (最優先)
            if (cache.overrideMap.TryGetValue(ifmName, out string overrideName))
            {
                TryApply(cache, overrideName, val);
                return;
            }

            // 4. 直接マッチ
            if (TryApply(cache, ifmName, val)) return;

            // 5. 固定マッピングテーブル
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
    // iFacialMocapが送りうる全パラメーター名 (MappingEntryDrawer と LimitEntryDrawer で共有)
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

        /// <summary>
        /// ifmName プロパティをポップアップで描画する共通処理。
        /// リストにない値は先頭に "(カスタム: xxx)" として表示し、選択変更まで値を維持する。
        /// </summary>
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
            // selected == 0 (カスタム値) のときは値を維持
        }
    }

    /// <summary>MappingEntry の ifmName をドロップダウンで描画する PropertyDrawer</summary>
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

    /// <summary>LimitEntry の ifmName をドロップダウン、min/max をフィールドで描画する PropertyDrawer</summary>
    [CustomPropertyDrawer(typeof(IFacialMocapBlendShapeApplier.LimitEntry))]
    public class LimitEntryDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var ifmProp = property.FindPropertyRelative("ifmName");
            var minProp = property.FindPropertyRelative("min");
            var maxProp = property.FindPropertyRelative("max");

            float w         = position.width;
            float sepW      = 6f;
            float labelW    = 24f;
            float numW      = (w * 0.5f - sepW - labelW * 2f) * 0.5f;
            float popupW    = w * 0.5f;

            float x = position.x;
            float y = position.y;
            float h = position.height;

            // [ifmName popup (50%)] [" min" label] [min float] [" ~ "] [max float]
            var popupRect = new Rect(x, y, popupW - sepW, h);
            x += popupW;

            var minLabelRect = new Rect(x, y, labelW, h);
            x += labelW;
            var minRect = new Rect(x, y, numW, h);
            x += numW + 2f;

            var sepRect = new Rect(x, y, labelW, h);
            x += labelW;
            var maxRect = new Rect(x, y, numW, h);

            IFacialMocapIfmNames.DrawIfmNamePopup(popupRect, ifmProp);

            EditorGUI.LabelField(minLabelRect, "min");
            minProp.floatValue = EditorGUI.FloatField(minRect, minProp.floatValue);

            EditorGUI.LabelField(sepRect, "~");
            maxProp.floatValue = EditorGUI.FloatField(maxRect, maxProp.floatValue);

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
