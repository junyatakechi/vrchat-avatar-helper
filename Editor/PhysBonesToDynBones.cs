// WIP: まだ、完成度低い
// PhysBones to Dynamic Bones Converter
// Based on FACS01-01/PhysBone-to-DynamicBone (v2)
// Updated for VRChat SDK 3.8+ (VRCPhysBone v1.0 / v1.1, ignoreOtherPhysBones)
//
// Lossless:
//   Pull → Elasticity (+Curve)
//   Immobile → Inert (+Curve)
//   Radius → Radius (+Curve, scale-corrected)
//   Ignore Transforms, Root Transform, Enabled
//   All colliders (Sphere / Capsule / Plane)
//
// Lossy:
//   Spring(v1.0) / Momentum(v1.1)   → Damping (+Curve)       ※ 1:1 対応不可
//   MaxAngleX                        → Stiffness (+Curve)      ※ 角度→割合の近似変換
//   Stiffness(v1.1)                  → Stiffness に加算（近似）※ 概念が異なるため挙動差あり
//   Gravity + GravityFalloff         → Gravity + Force         ※ 三角関数で分解
//   Hinge LimitType + FreezeAxis     → FreezeAxis              ※ 軸が斜めの場合はNone
//
// Not converted (DBに対応項目なし):
//   Squish, Stretch, MaxStretch, MaxSquish
//   Grab/Pose/Collision permissions
//   ImmobileType = World
//   ignoreOtherPhysBones (SDK 3.8+)
//   Multi-Child Type

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace JayT.VRChatAvatarHelper.Editor
{
    public class PhysBonesToDynBones : EditorWindow
    {
        // ------------------------------------------------------------------ //
        //  フィールド
        // ------------------------------------------------------------------ //
        private static GameObject _target;
        private static string     _log = "";

        // Stiffness ↔ MaxAngle 変換テーブル（VRCSDK 公式値）
        private static AnimationCurve _maxAngleToStiff;

        // 変換済みコライダーの対応表
        private static VRCPhysBoneCollider[]           _pbcArray;
        private static List<DynamicBoneColliderBase>   _dbcList;

        // ------------------------------------------------------------------ //
        //  メニュー登録
        // ------------------------------------------------------------------ //
        [MenuItem("Tools/JayT/VRChatAvatarHelper/PhysBones to DynamicBones")]
        public static void ShowWindow()
        {
            _target = null;
            _log    = "";
            GetWindow<PhysBonesToDynBones>(false, "PB → DB Converter", true);
        }

        // ------------------------------------------------------------------ //
        //  GUI
        // ------------------------------------------------------------------ //
        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "VRCPhysBone (v1.0 / v1.1) を DynamicBone に変換します。\n" +
                "SDK 3.8+ の ignoreOtherPhysBones に対応。\n" +
                "変換できない項目は末尾の注記を参照してください。",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            _target = (GameObject)EditorGUILayout.ObjectField(
                "対象 GameObject", _target, typeof(GameObject), true, GUILayout.Height(30));
            if (EditorGUI.EndChangeCheck()) _log = "";

            if (_target != null)
            {
                EditorGUILayout.Space(4);

                if (GUILayout.Button("変換実行", GUILayout.Height(36)))
                    RunConversion();
            }

            if (!string.IsNullOrEmpty(_log))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_log, MessageType.None);
            }
        }

        // ------------------------------------------------------------------ //
        //  変換メイン
        // ------------------------------------------------------------------ //
        private void RunConversion()
        {
            _log = "";
            InitConversionTable();

            _pbcArray = _target.GetComponentsInChildren<VRCPhysBoneCollider>(true);
            var pbArray = _target.GetComponentsInChildren<VRCPhysBone>(true);

            if (_pbcArray.Length == 0 && pbArray.Length == 0)
            {
                _log = "VRCPhysBone / VRCPhysBoneCollider が見つかりませんでした。";
                return;
            }

            // コライダー変換
            _dbcList = new List<DynamicBoneColliderBase>();
            foreach (var col in _pbcArray)
            {
                if (col.shapeType == VRCPhysBoneColliderBase.ShapeType.Plane)
                    ConvertPlaneCollider(col);
                else
                    ConvertCollider(col);
            }

            // ボーン変換
            int warnCount = 0;
            var warnings = new List<string>();
            foreach (var pb in pbArray)
                ConvertBone(pb, warnings, ref warnCount);

            // 元コンポーネント削除
            foreach (var pbc in _pbcArray) DestroyImmediate(pbc);
            foreach (var pb  in pbArray)   DestroyImmediate(pb);

            // ログ生成
            _log = $"変換完了\n" +
                   $"  VRCPhysBone        : {pbArray.Length} 個\n" +
                   $"  VRCPhysBoneCollider: {_dbcList.Count} 個\n";

            if (warnings.Count > 0)
            {
                _log += $"\n⚠ 注意 ({warnings.Count} 件):\n";
                _log += string.Join("\n", warnings.Take(10));
                if (warnings.Count > 10)
                    _log += $"\n  ... 他 {warnings.Count - 10} 件";
            }

            _log += "\n\n変換できない項目 (手動調整が必要):\n" +
                    "  Squish, Stretch, MaxStretch, MaxSquish,\n" +
                    "  Grab/Pose/Collision 権限,\n" +
                    "  ImmobileType=World,\n" +
                    "  ignoreOtherPhysBones,\n" +
                    "  Multi-Child Type";
        }

        // ------------------------------------------------------------------ //
        //  ボーン変換
        // ------------------------------------------------------------------ //
        private void ConvertBone(VRCPhysBone pb, List<string> warnings, ref int warnCount)
        {
            pb.InitTransforms(false);

            // null チェック・クリーンアップ
            if (pb.rootTransform == null) pb.rootTransform = pb.transform;
            pb.ignoreTransforms = pb.ignoreTransforms?.Where(t => t != null).ToList();
            if (pb.ignoreTransforms?.Count == 0) pb.ignoreTransforms = null;
            pb.colliders = pb.colliders?.Where(c => c != null).ToList();
            if (pb.colliders?.Count == 0) pb.colliders = null;

            var db = pb.gameObject.AddComponent<DynamicBone>();

            // ---- ロスレス ----
            db.enabled              = pb.enabled;
            db.m_Root               = pb.rootTransform;
            db.m_Exclusions         = pb.ignoreTransforms;
            db.m_Elasticity         = pb.pull;
            db.m_ElasticityDistrib  = pb.pullCurve;
            db.m_Inert              = pb.immobile;
            db.m_InertDistrib       = pb.immobileCurve;

            float scaleFactor = Mathf.Abs(pb.rootTransform.lossyScale.x)
                              / Mathf.Max(1e-5f, Mathf.Abs(pb.transform.lossyScale.x));
            db.m_Radius        = pb.radius * scaleFactor;
            db.m_RadiusDistrib = pb.radiusCurve;

            // ---- ImmobileType の警告 ----
            if (pb.immobileType == VRCPhysBoneBase.ImmobileType.World)
            {
                warnings.Add($"  [{pb.gameObject.name}] ImmobileType=World は変換不可 → AllMotion として扱われます");
                warnCount++;
            }

            // ---- SDK 3.8+: ignoreOtherPhysBones ----
            if (!pb.ignoreOtherPhysBones)
            {
                warnings.Add($"  [{pb.gameObject.name}] ignoreOtherPhysBones=false は変換不可 (DB に対応項目なし)");
                warnCount++;
            }

            // ---- FreezeAxis (Hinge のみ) ----
            var fa = DynamicBone.FreezeAxis.None;
            if (pb.limitType == VRCPhysBoneBase.LimitType.Hinge)
            {
                if      (pb.staticFreezeAxis == Vector3.right)   fa = DynamicBone.FreezeAxis.X;
                else if (pb.staticFreezeAxis == Vector3.up)      fa = DynamicBone.FreezeAxis.Y;
                else if (pb.staticFreezeAxis == Vector3.forward) fa = DynamicBone.FreezeAxis.Z;
                else
                {
                    warnings.Add($"  [{pb.gameObject.name}] Hinge の斜め軸は変換不可 → FreezeAxis=None");
                    warnCount++;
                }
            }
            db.m_FreezeAxis = fa;

            // ---- Gravity + GravityFalloff → Gravity + Force ----
            float boneLen = Mathf.Max(1e-5f, AverageBoneLength(pb));
            float gVal    = -pb.gravity * boneLen
                          / Mathf.Max(1e-5f, Mathf.Abs(pb.transform.lossyScale.x));

            if      (pb.gravityFalloff >= 1f - 1e-5f) { db.m_Gravity = new Vector3(0, gVal, 0); }
            else if (pb.gravityFalloff <= 1e-5f)      { db.m_Force   = new Vector3(0, gVal, 0); }
            else
            {
                float s = Mathf.Round(1e8f * Mathf.Sin(2f * Mathf.PI * pb.gravityFalloff)) / 1e8f;
                float c = Mathf.Round(1e8f * Mathf.Cos(2f * Mathf.PI * pb.gravityFalloff)) / 1e8f;
                db.m_Gravity = new Vector3(0, gVal * s, 0);
                db.m_Force   = new Vector3(0, gVal * c, 0);
            }

            // ---- Spring / Momentum → Damping ----
            // v1.0: pb.spring  / v1.1: pb.spring (Momentumとして機能)
            // どちらも同一フィールド名 spring を使用するため分岐不要
            ConvertSpringToDamping(pb, db);

            // ---- MaxAngleX → Stiffness ----
            ConvertAngleToStiffness(pb, db);

            // ---- v1.1 専用フィールドの処理 ----
            if (pb.version == VRCPhysBoneBase.Version.Version_1_1)
            {
                // Stiffness(v1.1) → DB Stiffness に加算（近似）
                // PB v1.1 Stiffness は「直前フレームの向きを保持する力」
                // DB Stiffness は「静止位置への復元力」で概念が異なるが最も近い項目
                if (pb.stiffness > 0f)
                {
                    db.m_Stiffness = Mathf.Clamp01(db.m_Stiffness + pb.stiffness);
                    warnings.Add($"  [{pb.gameObject.name}] Stiffness(v1.1)={pb.stiffness:F3} → DB Stiffness に加算（近似）");
                    warnCount++;
                }

                if (pb.maxSquish > 0f || pb.maxStretch > 1f)
                {
                    warnings.Add($"  [{pb.gameObject.name}] Squish/Stretch は変換不可");
                    warnCount++;
                }
            }

            // ---- コライダー参照の引き継ぎ ----
            if (pb.colliders != null && pb.colliders.Count > 0)
            {
                var cols = new List<DynamicBoneColliderBase>();
                foreach (var col in pb.colliders)
                {
                    int idx = Array.IndexOf(_pbcArray, col);
                    if (idx >= 0 && _dbcList[idx] != null)
                        cols.Add(_dbcList[idx]);
                }
                db.m_Colliders = cols;
            }
        }

        // ------------------------------------------------------------------ //
        //  Spring → Damping 変換
        // ------------------------------------------------------------------ //
        private void ConvertSpringToDamping(VRCPhysBone pb, DynamicBone db)
        {
            var curve = pb.springCurve;
            bool isFlatOne = curve == null || curve.length == 0
                          || IsConstantCurve(curve, out float cval) && Mathf.Approximately(cval, 1f);

            if (isFlatOne)
            {
                db.m_Damping        = 1f - pb.spring;
                db.m_DampingDistrib = null;
                return;
            }

            float maxDamp = CurveAbsMax(curve, 1f, -1f);
            db.m_Damping = Mathf.Clamp01(maxDamp);

            var kfs = new Keyframe[curve.length];
            for (int i = 0; i < curve.length; i++)
            {
                float t = curve.keys[i].time;
                float v = (1f - curve.keys[i].value) / Mathf.Max(1e-5f, maxDamp);
                kfs[i] = new Keyframe(t, v);
            }
            var distrib = new AnimationCurve(kfs);
            for (int i = 0; i < distrib.length; i++) distrib.SmoothTangents(i, 0f);
            db.m_DampingDistrib = distrib;
        }

        // ------------------------------------------------------------------ //
        //  MaxAngleX → Stiffness 変換
        // ------------------------------------------------------------------ //
        private void ConvertAngleToStiffness(VRCPhysBone pb, DynamicBone db)
        {
            var curve = pb.maxAngleXCurve;
            bool isFlatOne = curve == null || curve.length == 0
                          || IsConstantCurve(curve, out float cval) && Mathf.Approximately(cval, 1f);

            if (isFlatOne)
            {
                db.m_Stiffness        = _maxAngleToStiff.Evaluate(pb.maxAngleX);
                db.m_StiffnessDistrib = null;
                return;
            }

            var trueCurve = BuildStiffnessCurve(curve);
            float maxStiff = Mathf.Clamp01(CurveAbsMax(trueCurve, 0f, 1f));
            db.m_Stiffness = maxStiff;

            var kfs = new Keyframe[curve.length];
            for (int i = 0; i < curve.length; i++)
            {
                float t = curve.keys[i].time;
                float v = trueCurve.keys[i].value / Mathf.Max(1e-5f, maxStiff);
                kfs[i] = new Keyframe(t, v);
            }
            var distrib = new AnimationCurve(kfs);
            for (int i = 0; i < distrib.length; i++) distrib.SmoothTangents(i, 0f);
            db.m_StiffnessDistrib = distrib;
        }

        // MaxAngle カーブ → Stiffness カーブへの近似変換
        private AnimationCurve BuildStiffnessCurve(AnimationCurve angleCurve)
        {
            var kfs = new Keyframe[angleCurve.length];
            for (int i = 0; i < kfs.Length; i++)
            {
                float t = (float)i / Mathf.Max(1, kfs.Length - 1);
                float v = _maxAngleToStiff.Evaluate(180f * angleCurve.keys[i].value);
                kfs[i] = new Keyframe(t, v);
            }
            var result = new AnimationCurve(kfs);
            for (int i = 0; i < result.length; i++) result.SmoothTangents(i, 0f);
            return result;
        }

        // ------------------------------------------------------------------ //
        //  コライダー変換（Sphere / Capsule）
        // ------------------------------------------------------------------ //
        private void ConvertCollider(VRCPhysBoneCollider src)
        {
            var go     = src.gameObject;
            var bound  = src.insideBounds
                       ? DynamicBoneColliderBase.Bound.Inside
                       : DynamicBoneColliderBase.Bound.Outside;
            float r = src.radius;
            float h = src.shapeType == VRCPhysBoneColliderBase.ShapeType.Capsule ? src.height : 0f;

            ResolveColliderOrientation(src, out GameObject targetGO, out Vector3 pos,
                                       out DynamicBoneColliderBase.Direction dir, "DynBone_Collider");

            var dc = targetGO.AddComponent<DynamicBoneCollider>();
            dc.enabled      = src.enabled;
            dc.m_Center     = pos;
            dc.m_Direction  = dir;
            dc.m_Bound      = bound;
            dc.m_Radius     = r;
            dc.m_Height     = h;
            _dbcList.Add(dc);
        }

        // ------------------------------------------------------------------ //
        //  コライダー変換（Plane）
        // ------------------------------------------------------------------ //
        private void ConvertPlaneCollider(VRCPhysBoneCollider src)
        {
            ResolveColliderOrientation(src, out GameObject targetGO, out Vector3 pos,
                                       out DynamicBoneColliderBase.Direction dir, "DynBone_PlaneCollider",
                                       out DynamicBoneColliderBase.Bound bound);

            var dc = targetGO.AddComponent<DynamicBonePlaneCollider>();
            dc.enabled     = src.enabled;
            dc.m_Center    = pos;
            dc.m_Direction = dir;
            dc.m_Bound     = bound;
            _dbcList.Add(dc);
        }

        // ------------------------------------------------------------------ //
        //  コライダー回転→向き解決（Sphere/Capsule用）
        // ------------------------------------------------------------------ //
        private void ResolveColliderOrientation(
            VRCPhysBoneCollider src,
            out GameObject go,
            out Vector3 pos,
            out DynamicBoneColliderBase.Direction dir,
            string goName)
        {
            go  = src.gameObject;
            pos = src.position;
            dir = DynamicBoneColliderBase.Direction.Y;

            var rot = src.rotation;
            if      (rot == Quaternion.AngleAxis(-90f, Vector3.forward) ||
                     rot == Quaternion.AngleAxis( 90f, Vector3.forward))
                dir = DynamicBoneColliderBase.Direction.X;
            else if (rot == Quaternion.identity ||
                     rot == Quaternion.AngleAxis(180f, Vector3.forward))
                dir = DynamicBoneColliderBase.Direction.Y;
            else if (rot == Quaternion.AngleAxis( 90f, Vector3.right) ||
                     rot == Quaternion.AngleAxis(-90f, Vector3.right))
                dir = DynamicBoneColliderBase.Direction.Z;
            else
            {
                // 斜め回転 → 親 GO を追加して回転を吸収
                go  = CreateChildGO(src.transform, Vector3.zero, rot, goName);
                pos = Quaternion.Inverse(rot) * src.position;
            }
        }

        // ------------------------------------------------------------------ //
        //  コライダー回転→向き解決（Plane用、Bound 付き）
        // ------------------------------------------------------------------ //
        private void ResolveColliderOrientation(
            VRCPhysBoneCollider src,
            out GameObject go,
            out Vector3 pos,
            out DynamicBoneColliderBase.Direction dir,
            string goName,
            out DynamicBoneColliderBase.Bound bound)
        {
            go    = src.gameObject;
            pos   = src.position;
            dir   = DynamicBoneColliderBase.Direction.Y;
            bound = DynamicBoneColliderBase.Bound.Outside;

            var rot = src.rotation;
            if      (rot == Quaternion.AngleAxis(-90f, Vector3.forward))
                { dir = DynamicBoneColliderBase.Direction.X; }
            else if (rot == Quaternion.AngleAxis( 90f, Vector3.forward))
                { dir = DynamicBoneColliderBase.Direction.X; bound = DynamicBoneColliderBase.Bound.Inside; }
            else if (rot == Quaternion.identity)
                { dir = DynamicBoneColliderBase.Direction.Y; }
            else if (rot == Quaternion.AngleAxis(180f, Vector3.forward))
                { dir = DynamicBoneColliderBase.Direction.Y; bound = DynamicBoneColliderBase.Bound.Inside; }
            else if (rot == Quaternion.AngleAxis( 90f, Vector3.right))
                { dir = DynamicBoneColliderBase.Direction.Z; }
            else if (rot == Quaternion.AngleAxis(-90f, Vector3.right))
                { dir = DynamicBoneColliderBase.Direction.Z; bound = DynamicBoneColliderBase.Bound.Inside; }
            else
            {
                go  = CreateChildGO(src.transform, Vector3.zero, rot, goName);
                pos = Quaternion.Inverse(rot) * src.position;
            }
        }

        // ------------------------------------------------------------------ //
        //  ユーティリティ
        // ------------------------------------------------------------------ //

        private static float AverageBoneLength(VRCPhysBone pb)
        {
            if (pb.bones.Count <= 0) return 0f;
            float total = 0f; int count = 0;
            foreach (var bone in pb.bones)
            {
                if (bone.childIndex >= 0)
                {
                    var child = pb.bones[bone.childIndex];
                    total += Vector3.Distance(bone.transform.position, child.transform.position);
                    count++;
                }
                else if (bone.isEndBone && pb.endpointPosition != Vector3.zero)
                {
                    total += Vector3.Distance(bone.transform.position,
                                              bone.transform.TransformPoint(pb.endpointPosition));
                    count++;
                }
            }
            return count > 0 ? total / count : 0f;
        }

        private static bool IsConstantCurve(AnimationCurve ac, out float value)
        {
            value = ac.keys[0].value;
            for (int i = 1; i < ac.keys.Length; i++)
                if (!Mathf.Approximately(value, ac.keys[i].value)) return false;
            return true;
        }

        private static float CurveAbsMax(AnimationCurve ac, float delta, float mult)
        {
            float max = 0f;
            foreach (var key in ac.keys)
            {
                float v = Mathf.Abs(delta + mult * key.value);
                if (v > max) max = v;
            }
            return max;
        }

        private GameObject CreateChildGO(Transform parent, Vector3 localPos, Quaternion localRot, string baseName)
        {
            var go = new GameObject();
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            go.transform.localScale    = Vector3.one;
            go.name = ObjectNames.GetUniqueName(
                GetSiblingNames(go), baseName);
            return go;
        }

        private string[] GetSiblingNames(GameObject go)
        {
            var parent = go.transform.parent;
            if (parent == null)
                return go.scene.GetRootGameObjects().Select(x => x.name).ToArray();
            var names = new List<string>();
            foreach (Transform t in parent)
                if (t.gameObject != go) names.Add(t.name);
            return names.ToArray();
        }

        // ------------------------------------------------------------------ //
        //  Stiffness ↔ MaxAngle 変換テーブル初期化
        //  （VRChat SDK 公式の PhysBoneMigration.StiffToMaxAngle と同値）
        // ------------------------------------------------------------------ //
        private static void InitConversionTable()
        {
            if (_maxAngleToStiff != null) return;

            var stiffToAngle = new AnimationCurve(new[]
            {
                new Keyframe(0f,   180f),
                new Keyframe(0.1f, 129f),
                new Keyframe(0.2f, 106f),
                new Keyframe(0.3f,  89f),
                new Keyframe(0.4f,  74f),
                new Keyframe(0.5f,  60f),
                new Keyframe(0.6f,  47f),
                new Keyframe(0.7f,  35f),
                new Keyframe(0.8f,  23f),
                new Keyframe(0.9f,  11f),
                new Keyframe(1f,     0f),
            });
            for (int i = 0; i < stiffToAngle.length; i++)
                stiffToAngle.SmoothTangents(i, 0f);

            // 逆引きテーブル: MaxAngle → Stiffness（1801点サンプリング）
            var kfs = new Keyframe[1801];
            for (int i = 0; i < kfs.Length; i++)
            {
                float n = i / 1800f;
                kfs[i] = new Keyframe(stiffToAngle.Evaluate(n), n);
            }
            _maxAngleToStiff = new AnimationCurve(kfs);
        }

        private void OnDestroy()
        {
            _target          = null;
            _log             = "";
            _maxAngleToStiff = null;
            _pbcArray        = null;
            _dbcList         = null;
        }
    }
}
#endif
