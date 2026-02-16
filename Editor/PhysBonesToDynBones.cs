// WIP: まだ、完成度低い
// PhysBones to Dynamic Bones Converter
// Based on FACS01-01/PhysBone-to-DynamicBone (v2)
// Updated for VRChat SDK 3.8+ (VRCPhysBone v1.0 / v1.1, ignoreOtherPhysBones)
//
// Lossless:
//   Pull -> Elasticity (+Curve)
//   Immobile -> Inert (+Curve)
//   Radius -> Radius (+Curve, scale-corrected)
//   Ignore Transforms, Root Transform, Enabled
//   All colliders (Sphere / Capsule / Plane)
//
// Lossy:
//   Spring(v1.0) / Momentum(v1.1)   -> Damping (+Curve)       * 1:1 not possible
//   MaxAngleX / Polar(Pitch,Yaw)    -> Stiffness (+Curve)      * angle-to-ratio approximation
//   Stiffness(v1.1)                 -> Stiffness add (approx)  * different concepts
//   Spring -> Stiffness/Elasticity partial redistribution       * shape retention补完
//   Gravity + GravityFalloff        -> Gravity + Force          * v1.1 ratio support
//   Hinge LimitType + FreezeAxis    -> FreezeAxis               * diagonal axis -> None
//
// Not converted (no DB equivalent):
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
        //  Fields
        // ------------------------------------------------------------------ //
        private static GameObject _target;
        private static string     _log = "";

        // Stiffness <-> MaxAngle conversion table (VRCSDK official values)
        private static AnimationCurve _maxAngleToStiff;

        // Converted collider mapping
        private static VRCPhysBoneCollider[]           _pbcArray;
        private static List<DynamicBoneColliderBase>   _dbcList;

        // Quaternion comparison tolerance (degrees)
        private const float QuaternionTolerance = 2f;

        // ------------------------------------------------------------------ //
        //  Tuning constants (adjust these during testing)
        // ------------------------------------------------------------------ //
        private const float SpringToStiffnessFactor     = 0.20f;
        private const float SpringToElasticityFactor    = 0.15f;
        private const float ImmobileToStiffnessFactor   = 0.30f;
        private const float PullDeficitToStiffnessFactor = 0.20f;
        private const float GravityV11Scale             = 0.05f;

        // ------------------------------------------------------------------ //
        //  Menu
        // ------------------------------------------------------------------ //
        [MenuItem("Tools/JayT/VRChatAvatarHelper/PhysBones to DynamicBones")]
        public static void ShowWindow()
        {
            _target = null;
            _log    = "";
            GetWindow<PhysBonesToDynBones>(false, "PB -> DB Converter", true);
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
        //  Main conversion
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

            // Collider conversion
            _dbcList = new List<DynamicBoneColliderBase>();
            foreach (var col in _pbcArray)
            {
                if (col.shapeType == VRCPhysBoneColliderBase.ShapeType.Plane)
                    ConvertPlaneCollider(col);
                else
                    ConvertCollider(col);
            }

            // Bone conversion
            int warnCount = 0;
            var warnings = new List<string>();
            foreach (var pb in pbArray)
                ConvertBone(pb, warnings, ref warnCount);

            // Remove original components
            foreach (var pbc in _pbcArray) DestroyImmediate(pbc);
            foreach (var pb  in pbArray)   DestroyImmediate(pb);

            // Log
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
        //  Bone conversion
        // ------------------------------------------------------------------ //
        private void ConvertBone(VRCPhysBone pb, List<string> warnings, ref int warnCount)
        {
            pb.InitTransforms(false);

            // Null check / cleanup
            if (pb.rootTransform == null) pb.rootTransform = pb.transform;
            pb.ignoreTransforms = pb.ignoreTransforms?.Where(t => t != null).ToList();
            if (pb.ignoreTransforms?.Count == 0) pb.ignoreTransforms = null;
            pb.colliders = pb.colliders?.Where(c => c != null).ToList();
            if (pb.colliders?.Count == 0) pb.colliders = null;

            var db = pb.gameObject.AddComponent<DynamicBone>();

            // ---- Lossless ----
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

            // ---- ImmobileType warning ----
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

            // ---- FreezeAxis (Hinge only) ----
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

            // ---- Gravity + GravityFalloff -> Gravity + Force ----
            // [FIX] v1.1: Gravity is a ratio (0-1), use fixed small coefficient instead of boneLength
            float gVal;
            if (pb.version == VRCPhysBoneBase.Version.Version_1_1)
            {
                gVal = -pb.gravity * GravityV11Scale;
            }
            else
            {
                float boneLen = Mathf.Max(1e-5f, AverageBoneLength(pb));
                gVal = -pb.gravity * boneLen
                     / Mathf.Max(1e-5f, Mathf.Abs(pb.transform.lossyScale.x));
            }

            if      (pb.gravityFalloff >= 1f - 1e-5f) { db.m_Gravity = new Vector3(0, gVal, 0); }
            else if (pb.gravityFalloff <= 1e-5f)      { db.m_Force   = new Vector3(0, gVal, 0); }
            else
            {
                float s = Mathf.Round(1e8f * Mathf.Sin(2f * Mathf.PI * pb.gravityFalloff)) / 1e8f;
                float c = Mathf.Round(1e8f * Mathf.Cos(2f * Mathf.PI * pb.gravityFalloff)) / 1e8f;
                db.m_Gravity = new Vector3(0, gVal * s, 0);
                db.m_Force   = new Vector3(0, gVal * c, 0);
            }

            // ---- Spring / Momentum -> Damping ----
            ConvertSpringToDamping(pb, db);

            // ---- Limit -> Stiffness (with Polar support) ----
            ConvertLimitToStiffness(pb, db, warnings, ref warnCount);

            // ---- [FIX] Spring -> partial redistribution to Stiffness/Elasticity ----
            // PB's Spring has implicit shape retention effect that DB lacks.
            // Compensate by adding a portion to Stiffness and Elasticity.
            db.m_Stiffness  = Mathf.Clamp01(db.m_Stiffness  + pb.spring * SpringToStiffnessFactor);
            db.m_Elasticity = Mathf.Clamp01(db.m_Elasticity  + pb.spring * SpringToElasticityFactor);

            // ---- [FIX] Immobile/Pull -> Stiffness base value ----
            // PB retains shape through the combination of Immobile + Pull.
            // DB relies primarily on Stiffness for this, so we add a base value.
            float baseStiffness = pb.immobile * ImmobileToStiffnessFactor
                                + (1f - pb.pull) * PullDeficitToStiffnessFactor;
            db.m_Stiffness = Mathf.Clamp01(db.m_Stiffness + baseStiffness);

            // ---- v1.1 specific fields ----
            if (pb.version == VRCPhysBoneBase.Version.Version_1_1)
            {
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

            // ---- Collider reference migration ----
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
        //  Spring -> Damping conversion
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
        //  [FIX] Limit -> Stiffness conversion (Polar / Angle / Hinge / None)
        //  Replaces old ConvertAngleToStiffness:
        //  - Polar: weighted average of MaxPitch(maxAngleX) and MaxYaw(maxAngleZ)
        //  - Angle/Hinge: maxAngleX only (as before)
        //  - None: limit-derived Stiffness = 0 (base value added later)
        // ------------------------------------------------------------------ //
        private void ConvertLimitToStiffness(VRCPhysBone pb, DynamicBone db,
                                              List<string> warnings, ref int warnCount)
        {
            // No limit -> no limit-derived stiffness
            // (base value from Immobile/Pull/Spring is added in ConvertBone)
            if (pb.limitType == VRCPhysBoneBase.LimitType.None)
            {
                db.m_Stiffness        = 0f;
                db.m_StiffnessDistrib = null;
                return;
            }

            // ---- Compute effective angle ----
            float effectiveAngle;
            if (pb.limitType == VRCPhysBoneBase.LimitType.Polar)
            {
                // Polar: weighted average biased toward the tighter axis (smaller angle)
                // to better represent the overall constraint strength
                float minAngle = Mathf.Min(pb.maxAngleX, pb.maxAngleZ);
                float maxAngle = Mathf.Max(pb.maxAngleX, pb.maxAngleZ);
                effectiveAngle = minAngle * 0.7f + maxAngle * 0.3f;

                warnings.Add($"  [{pb.gameObject.name}] Polar(Pitch={pb.maxAngleX}, Yaw={pb.maxAngleZ}) → effectiveAngle={effectiveAngle:F1} で Stiffness 近似");
                warnCount++;
            }
            else
            {
                // Angle / Hinge: use maxAngleX directly
                effectiveAngle = pb.maxAngleX;
            }

            // ---- Without curve ----
            var curve = pb.maxAngleXCurve;
            bool isFlatOne = curve == null || curve.length == 0
                          || IsConstantCurve(curve, out float cval) && Mathf.Approximately(cval, 1f);

            if (isFlatOne)
            {
                db.m_Stiffness        = _maxAngleToStiff.Evaluate(effectiveAngle);
                db.m_StiffnessDistrib = null;
                return;
            }

            // ---- With curve ----
            var trueCurve = BuildStiffnessCurve(curve, effectiveAngle);
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

        // [FIX] Takes effectiveAngle instead of hardcoded 180
        private AnimationCurve BuildStiffnessCurve(AnimationCurve angleCurve, float baseAngle)
        {
            var kfs = new Keyframe[angleCurve.length];
            for (int i = 0; i < kfs.Length; i++)
            {
                float t = (float)i / Mathf.Max(1, kfs.Length - 1);
                float v = _maxAngleToStiff.Evaluate(baseAngle * angleCurve.keys[i].value);
                kfs[i] = new Keyframe(t, v);
            }
            var result = new AnimationCurve(kfs);
            for (int i = 0; i < result.length; i++) result.SmoothTangents(i, 0f);
            return result;
        }

        // ================================================================== //
        //  Collider conversion (Sphere / Capsule)
        // ================================================================== //
        private void ConvertCollider(VRCPhysBoneCollider src)
        {
            Transform baseTransform = src.rootTransform != null ? src.rootTransform : src.transform;

            var bound = src.insideBounds
                      ? DynamicBoneColliderBase.Bound.Inside
                      : DynamicBoneColliderBase.Bound.Outside;

            float r = src.radius;
            float h = src.shapeType == VRCPhysBoneColliderBase.ShapeType.Capsule ? src.height : 0f;

            ResolveColliderOrientation(src, baseTransform,
                out GameObject targetGO, out Vector3 pos,
                out DynamicBoneColliderBase.Direction dir);

            var dc = targetGO.AddComponent<DynamicBoneCollider>();
            dc.enabled     = src.enabled;
            dc.m_Center    = pos;
            dc.m_Direction = dir;
            dc.m_Bound     = bound;
            dc.m_Radius    = r;
            dc.m_Height    = h;
            _dbcList.Add(dc);
        }

        // ================================================================== //
        //  Collider conversion (Plane)
        // ================================================================== //
        private void ConvertPlaneCollider(VRCPhysBoneCollider src)
        {
            Transform baseTransform = src.rootTransform != null ? src.rootTransform : src.transform;

            ResolveColliderOrientationPlane(src, baseTransform,
                out GameObject targetGO, out Vector3 pos,
                out DynamicBoneColliderBase.Direction dir,
                out DynamicBoneColliderBase.Bound bound);

            var dc = targetGO.AddComponent<DynamicBonePlaneCollider>();
            dc.enabled     = src.enabled;
            dc.m_Center    = pos;
            dc.m_Direction = dir;
            dc.m_Bound     = bound;
            _dbcList.Add(dc);
        }

        // ================================================================== //
        //  Collider orientation resolver (Sphere / Capsule)
        // ================================================================== //
        private void ResolveColliderOrientation(
            VRCPhysBoneCollider src,
            Transform baseTransform,
            out GameObject go,
            out Vector3 pos,
            out DynamicBoneColliderBase.Direction dir)
        {
            go  = baseTransform.gameObject;
            pos = src.position;
            dir = DynamicBoneColliderBase.Direction.Y;

            var rot = src.rotation;

            if (QuatApprox(rot, Quaternion.identity))
            {
                dir = DynamicBoneColliderBase.Direction.Y;
                return;
            }

            if (QuatApprox(rot, Quaternion.AngleAxis(-90f, Vector3.forward)) ||
                QuatApprox(rot, Quaternion.AngleAxis( 90f, Vector3.forward)))
            {
                dir = DynamicBoneColliderBase.Direction.X;
                return;
            }

            if (QuatApprox(rot, Quaternion.AngleAxis(180f, Vector3.forward)) ||
                QuatApprox(rot, Quaternion.AngleAxis(180f, Vector3.up)))
            {
                dir = DynamicBoneColliderBase.Direction.Y;
                return;
            }

            if (QuatApprox(rot, Quaternion.AngleAxis( 90f, Vector3.right)) ||
                QuatApprox(rot, Quaternion.AngleAxis(-90f, Vector3.right)))
            {
                dir = DynamicBoneColliderBase.Direction.Z;
                return;
            }

            Vector3 rotatedY = rot * Vector3.up;
            float absX = Mathf.Abs(rotatedY.x);
            float absY = Mathf.Abs(rotatedY.y);
            float absZ = Mathf.Abs(rotatedY.z);

            const float axisThreshold = 0.95f;

            if (absX >= absY && absX >= absZ && absX >= axisThreshold)
            {
                dir = DynamicBoneColliderBase.Direction.X;
                return;
            }
            if (absY >= absX && absY >= absZ && absY >= axisThreshold)
            {
                dir = DynamicBoneColliderBase.Direction.Y;
                return;
            }
            if (absZ >= absX && absZ >= absY && absZ >= axisThreshold)
            {
                dir = DynamicBoneColliderBase.Direction.Z;
                return;
            }

            go  = CreateChildGO(baseTransform, Vector3.zero, rot, "DynBone_Collider");
            pos = Quaternion.Inverse(rot) * src.position;
            dir = DynamicBoneColliderBase.Direction.Y;
        }

        // ================================================================== //
        //  Collider orientation resolver (Plane)
        // ================================================================== //
        private void ResolveColliderOrientationPlane(
            VRCPhysBoneCollider src,
            Transform baseTransform,
            out GameObject go,
            out Vector3 pos,
            out DynamicBoneColliderBase.Direction dir,
            out DynamicBoneColliderBase.Bound bound)
        {
            go    = baseTransform.gameObject;
            pos   = src.position;
            dir   = DynamicBoneColliderBase.Direction.Y;
            bound = DynamicBoneColliderBase.Bound.Outside;

            var rot = src.rotation;

            if (QuatApprox(rot, Quaternion.identity))
            {
                dir = DynamicBoneColliderBase.Direction.Y;
                bound = DynamicBoneColliderBase.Bound.Outside;
                return;
            }

            if (QuatApprox(rot, Quaternion.AngleAxis(-90f, Vector3.forward)))
            {
                dir = DynamicBoneColliderBase.Direction.X;
                bound = DynamicBoneColliderBase.Bound.Outside;
                return;
            }
            if (QuatApprox(rot, Quaternion.AngleAxis(90f, Vector3.forward)))
            {
                dir = DynamicBoneColliderBase.Direction.X;
                bound = DynamicBoneColliderBase.Bound.Inside;
                return;
            }

            if (QuatApprox(rot, Quaternion.AngleAxis(180f, Vector3.forward)) ||
                QuatApprox(rot, Quaternion.AngleAxis(180f, Vector3.right)))
            {
                dir = DynamicBoneColliderBase.Direction.Y;
                bound = DynamicBoneColliderBase.Bound.Inside;
                return;
            }

            if (QuatApprox(rot, Quaternion.AngleAxis(90f, Vector3.right)))
            {
                dir = DynamicBoneColliderBase.Direction.Z;
                bound = DynamicBoneColliderBase.Bound.Outside;
                return;
            }
            if (QuatApprox(rot, Quaternion.AngleAxis(-90f, Vector3.right)))
            {
                dir = DynamicBoneColliderBase.Direction.Z;
                bound = DynamicBoneColliderBase.Bound.Inside;
                return;
            }

            Vector3 normal = rot * Vector3.up;
            float absX = Mathf.Abs(normal.x);
            float absY = Mathf.Abs(normal.y);
            float absZ = Mathf.Abs(normal.z);

            const float axisThreshold = 0.95f;

            if (absX >= absY && absX >= absZ && absX >= axisThreshold)
            {
                dir   = DynamicBoneColliderBase.Direction.X;
                bound = normal.x > 0
                    ? DynamicBoneColliderBase.Bound.Outside
                    : DynamicBoneColliderBase.Bound.Inside;
                return;
            }
            if (absY >= absX && absY >= absZ && absY >= axisThreshold)
            {
                dir   = DynamicBoneColliderBase.Direction.Y;
                bound = normal.y > 0
                    ? DynamicBoneColliderBase.Bound.Outside
                    : DynamicBoneColliderBase.Bound.Inside;
                return;
            }
            if (absZ >= absX && absZ >= absY && absZ >= axisThreshold)
            {
                dir   = DynamicBoneColliderBase.Direction.Z;
                bound = normal.z > 0
                    ? DynamicBoneColliderBase.Bound.Outside
                    : DynamicBoneColliderBase.Bound.Inside;
                return;
            }

            go    = CreateChildGO(baseTransform, Vector3.zero, rot, "DynBone_PlaneCollider");
            pos   = Quaternion.Inverse(rot) * src.position;
            dir   = DynamicBoneColliderBase.Direction.Y;
            bound = DynamicBoneColliderBase.Bound.Outside;
        }

        // ------------------------------------------------------------------ //
        //  Utilities
        // ------------------------------------------------------------------ //

        private static bool QuatApprox(Quaternion a, Quaternion b)
        {
            return Quaternion.Angle(a, b) < QuaternionTolerance;
        }

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
        //  Stiffness <-> MaxAngle conversion table init
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