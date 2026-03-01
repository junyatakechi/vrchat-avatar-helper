using UnityEngine;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace JayT.VRChatAvatarHelper.Facial
{
    /// <summary>
    /// iFacialMocapから受信したデータをアバターのBlendShapeに適用します。
    /// 同じGameObjectまたは任意のGameObjectにある IFacialMocapReceiver を参照して使用します。
    /// アバターのBlendShape名はARKit標準名を前提としています。
    /// </summary>
    public class IFacialMocapBlendShapeApplier : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("データを受け取るIFacialMocapReceiverコンポーネント")]
        public IFacialMocapReceiver receiver;

        [Header("Target")]
        [Tooltip("BlendShapeを適用するSkinnedMeshRenderer (複数可)")]
        public SkinnedMeshRenderer[] targetRenderers;

        [Header("Settings")]
        [Tooltip("BlendShape値の倍率 (iFacialMocap は 0–100 スケール)")]
        [Range(0f, 2f)]
        public float weightScale = 1f;

        // iFacialMocap が送る省略名 (_L/_R) → ARKit標準名 (Left/Right) のマッピング
        private static readonly Dictionary<string, string> NameMapping = new Dictionary<string, string>
        {
            // 目 — 瞬き
            { "eyeBlink_L",     "eyeBlinkLeft" },
            { "eyeBlink_R",     "eyeBlinkRight" },
            // 目 — 見開き
            { "eyeWide_L",      "eyeWideLeft" },
            { "eyeWide_R",      "eyeWideRight" },
            // 目 — 細め
            { "eyeSquint_L",    "eyeSquintLeft" },
            { "eyeSquint_R",    "eyeSquintRight" },
            // 目 — 視線
            { "eyeLookUp_L",    "eyeLookUpLeft" },
            { "eyeLookUp_R",    "eyeLookUpRight" },
            { "eyeLookDown_L",  "eyeLookDownLeft" },
            { "eyeLookDown_R",  "eyeLookDownRight" },
            { "eyeLookIn_L",    "eyeLookInLeft" },
            { "eyeLookIn_R",    "eyeLookInRight" },
            { "eyeLookOut_L",   "eyeLookOutLeft" },
            { "eyeLookOut_R",   "eyeLookOutRight" },
            // 眉
            { "browDown_L",     "browDownLeft" },
            { "browDown_R",     "browDownRight" },
            { "browOuterUp_L",  "browOuterUpLeft" },
            { "browOuterUp_R",  "browOuterUpRight" },
            // 頬
            { "cheekSquint_L",  "cheekSquintLeft" },
            { "cheekSquint_R",  "cheekSquintRight" },
            // 鼻
            { "noseSneer_L",    "noseSneerLeft" },
            { "noseSneer_R",    "noseSneerRight" },
            // 口 — 笑顔・しかめ
            { "mouthSmile_L",   "mouthSmileLeft" },
            { "mouthSmile_R",   "mouthSmileRight" },
            { "mouthFrown_L",   "mouthFrownLeft" },
            { "mouthFrown_R",   "mouthFrownRight" },
            // 口 — えくぼ
            { "mouthDimple_L",  "mouthDimpleLeft" },
            { "mouthDimple_R",  "mouthDimpleRight" },
            // 口 — 上唇・下唇
            { "mouthUpperUp_L",    "mouthUpperUpLeft" },
            { "mouthUpperUp_R",    "mouthUpperUpRight" },
            { "mouthLowerDown_L",  "mouthLowerDownLeft" },
            { "mouthLowerDown_R",  "mouthLowerDownRight" },
            // 口 — プレス・ストレッチ
            { "mouthPress_L",   "mouthPressLeft" },
            { "mouthPress_R",   "mouthPressRight" },
            { "mouthStretch_L", "mouthStretchLeft" },
            { "mouthStretch_R", "mouthStretchRight" },
        };

        // BlendShape名 → (SkinnedMeshRenderer, インデックス) のキャッシュ
        private Dictionary<string, (SkinnedMeshRenderer smr, int idx)> blendShapeCache;

        void OnEnable()
        {
            BuildCache();

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

        private void BuildCache()
        {
            blendShapeCache = new Dictionary<string, (SkinnedMeshRenderer, int)>(StringComparer.Ordinal);

            if (targetRenderers == null) return;

            foreach (var smr in targetRenderers)
            {
                if (smr == null || smr.sharedMesh == null) continue;

                var mesh = smr.sharedMesh;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string name = mesh.GetBlendShapeName(i);
                    if (!blendShapeCache.ContainsKey(name))
                        blendShapeCache[name] = (smr, i);
                }
            }

            Debug.Log($"[IFacialMocapBlendShapeApplier] BlendShapeキャッシュ構築完了: {blendShapeCache.Count} 個", this);
        }

        // メインスレッドから呼ばれる (IFacialMocapReceiver.ParseAndApplyData → OnDataReceived)
        private void HandleData(string data)
        {
            if (string.IsNullOrEmpty(data)) return;

            var tokens = data.Split('|');
            foreach (var token in tokens)
            {
                // '#' を含むトークンはボーン回転データなのでスキップ
                if (token.IndexOf('#') >= 0) continue;

                int sep = token.LastIndexOf('-');
                if (sep <= 0) continue;

                string ifmName = token.Substring(0, sep);
                string valueStr = token.Substring(sep + 1);

                if (!float.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                    continue;

                val *= weightScale;

                // 1. 直接マッチ
                if (TryApply(ifmName, val)) continue;

                // 2. マッピングテーブルで変換してマッチ
                if (NameMapping.TryGetValue(ifmName, out string arKitName))
                    TryApply(arKitName, val);
            }
        }

        private bool TryApply(string blendShapeName, float val)
        {
            if (!blendShapeCache.TryGetValue(blendShapeName, out var entry))
                return false;

            entry.smr.SetBlendShapeWeight(entry.idx, val);
            return true;
        }
    }
}
