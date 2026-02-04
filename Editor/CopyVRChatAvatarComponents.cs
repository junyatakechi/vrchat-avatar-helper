using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;

namespace JayT.VRChatAvatarHelper.Editor
{
    public class CopyVRChatAvatarComponents : EditorWindow
    {
        [SerializeField] private Animator sourceAvatar;
        [SerializeField] private Transform sourceArmature;
        [SerializeField] private Transform targetArmature;
        [SerializeField] private List<SkinnedMeshRenderer> sourceSkinnedMeshes = new List<SkinnedMeshRenderer>();
        [SerializeField] private List<SkinnedMeshRenderer> targetSkinnedMeshes = new List<SkinnedMeshRenderer>();
        [SerializeField] private int skinnedMeshCount = 0;
        [SerializeField] private bool copyBoneTransforms = true;
        [SerializeField] private bool copyAvatarDescriptor = false;
        [SerializeField] private bool copyPhysBones = false;
        [SerializeField] private bool copyPhysBoneColliders = false;

        private Vector2 scrollPosition;
        private Dictionary<Transform, Transform> boneMappingHumanoidOnly = new Dictionary<Transform, Transform>();
        private Dictionary<Transform, Transform> boneMappingAll = new Dictionary<Transform, Transform>();
        private Dictionary<VRCPhysBoneCollider, VRCPhysBoneCollider> colliderMapping = new Dictionary<VRCPhysBoneCollider, VRCPhysBoneCollider>();

        [MenuItem("Tools/JayT/VRChatAvatarHelper/Copy VRChat Avatar Components")]
        public static void ShowWindow()
        {
            GetWindow<CopyVRChatAvatarComponents>("Copy VRChat Avatar Components");
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            GUILayout.Label("Copy VRChat Avatar Components", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            sourceAvatar = (Animator)EditorGUILayout.ObjectField("Source Avatar", sourceAvatar, typeof(Animator), true);
            sourceArmature = (Transform)EditorGUILayout.ObjectField("Source Armature", sourceArmature, typeof(Transform), true);
            
            EditorGUILayout.Space();
            
            targetArmature = (Transform)EditorGUILayout.ObjectField("Target Armature", targetArmature, typeof(Transform), true);

            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Copy Options", EditorStyles.boldLabel);
            copyBoneTransforms = EditorGUILayout.Toggle("Copy Bone Transforms", copyBoneTransforms);
            copyAvatarDescriptor = EditorGUILayout.Toggle("Copy Avatar Descriptor", copyAvatarDescriptor);
            copyPhysBoneColliders = EditorGUILayout.Toggle("Copy PhysBone Colliders", copyPhysBoneColliders);
            copyPhysBones = EditorGUILayout.Toggle("Copy PhysBones", copyPhysBones);

            EditorGUILayout.Space();

            int newCount = EditorGUILayout.IntField("SkinnedMesh Count", skinnedMeshCount);
            if (newCount != skinnedMeshCount)
            {
                skinnedMeshCount = Mathf.Max(0, newCount);
                ResizeSkinnedMeshLists();
            }

            if (skinnedMeshCount > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("SkinnedMeshRenderers", EditorStyles.boldLabel);
                
                for (int i = 0; i < skinnedMeshCount; i++)
                {
                    EditorGUILayout.LabelField($"--- Pair {i} ---", EditorStyles.boldLabel);
                    sourceSkinnedMeshes[i] = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("  Source", sourceSkinnedMeshes[i], typeof(SkinnedMeshRenderer), true);
                    targetSkinnedMeshes[i] = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("  Target", targetSkinnedMeshes[i], typeof(SkinnedMeshRenderer), true);
                    EditorGUILayout.Space();
                }
            }

            EditorGUILayout.Space();

            if (GUILayout.Button("Copy Components"))
            {
                CopyComponents();
            }

            EditorGUILayout.EndScrollView();
        }

        private void ResizeSkinnedMeshLists()
        {
            while (sourceSkinnedMeshes.Count < skinnedMeshCount)
            {
                sourceSkinnedMeshes.Add(null);
            }
            while (sourceSkinnedMeshes.Count > skinnedMeshCount)
            {
                sourceSkinnedMeshes.RemoveAt(sourceSkinnedMeshes.Count - 1);
            }

            while (targetSkinnedMeshes.Count < skinnedMeshCount)
            {
                targetSkinnedMeshes.Add(null);
            }
            while (targetSkinnedMeshes.Count > skinnedMeshCount)
            {
                targetSkinnedMeshes.RemoveAt(targetSkinnedMeshes.Count - 1);
            }
        }

        private void CopyComponents()
        {
            if (sourceAvatar == null || targetArmature == null)
            {
                EditorUtility.DisplayDialog("Error", "Source Avatar and Target Armature must be assigned.", "OK");
                return;
            }

            if (!sourceAvatar.isHuman)
            {
                EditorUtility.DisplayDialog("Error", "Source avatar must be Humanoid.", "OK");
                return;
            }

            boneMappingHumanoidOnly.Clear();
            boneMappingAll.Clear();
            colliderMapping.Clear();

            BuildHumanoidBoneMapping();
            BuildAllBoneMapping(sourceArmature, targetArmature);

            if (copyBoneTransforms)
            {
                if (sourceArmature != null && targetArmature != null)
                {
                    CopyTransformAndScaleAdjuster(sourceArmature, targetArmature);
                }
                CopyHumanoidBoneTransforms();
            }

            if (copyPhysBoneColliders)
            {
                CopyAllPhysBoneColliders();
            }

            if (copyPhysBones)
            {
                CopyAllPhysBones();
            }

            for (int i = 0; i < skinnedMeshCount; i++)
            {
                if (sourceSkinnedMeshes[i] != null && targetSkinnedMeshes[i] != null)
                {
                    CopyBlendShapes(sourceSkinnedMeshes[i], targetSkinnedMeshes[i]);
                }
            }

            if (copyAvatarDescriptor)
            {
                CopyAvatarDescriptor();
            }

            EditorUtility.DisplayDialog("Complete", "Components copied successfully.", "OK");
        }

        private void BuildHumanoidBoneMapping()
        {
            if (sourceArmature != null && targetArmature != null)
            {
                boneMappingHumanoidOnly[sourceArmature] = targetArmature;
            }

            var humanBones = System.Enum.GetValues(typeof(HumanBodyBones));

            foreach (HumanBodyBones bone in humanBones)
            {
                if (bone == HumanBodyBones.LastBone) continue;

                Transform sourceBone = sourceAvatar.GetBoneTransform(bone);
                
                if (sourceBone != null)
                {
                    string boneName = sourceBone.gameObject.name;
                    Transform targetBone = FindBoneByName(targetArmature, boneName);
                    
                    if (targetBone != null)
                    {
                        boneMappingHumanoidOnly[sourceBone] = targetBone;
                    }
                }
            }
        }

        private void BuildAllBoneMapping(Transform sourceRoot, Transform targetRoot)
        {
            if (sourceRoot == null || targetRoot == null) return;

            boneMappingAll[sourceRoot] = targetRoot;
            BuildAllBoneMappingRecursive(sourceRoot, targetRoot);
        }

        private void BuildAllBoneMappingRecursive(Transform sourceParent, Transform targetRoot)
        {
            foreach (Transform sourceChild in sourceParent)
            {
                Transform targetChild = FindBoneByName(targetRoot, sourceChild.name);
                if (targetChild != null)
                {
                    boneMappingAll[sourceChild] = targetChild;
                    BuildAllBoneMappingRecursive(sourceChild, targetRoot);
                }
            }
        }

        private void CopyHumanoidBoneTransforms()
        {
            var humanBones = System.Enum.GetValues(typeof(HumanBodyBones));

            foreach (HumanBodyBones bone in humanBones)
            {
                if (bone == HumanBodyBones.LastBone) continue;

                Transform sourceBone = sourceAvatar.GetBoneTransform(bone);
                
                if (sourceBone != null && boneMappingHumanoidOnly.TryGetValue(sourceBone, out Transform targetBone))
                {
                    CopyTransformAndScaleAdjuster(sourceBone, targetBone);
                }
            }
        }

        private Transform FindBoneByName(Transform root, string boneName)
        {
            if (root.name == boneName)
            {
                return root;
            }

            foreach (Transform child in root)
            {
                Transform found = FindBoneByName(child, boneName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private void CopyTransformAndScaleAdjuster(Transform source, Transform target)
        {
            target.localPosition = source.localPosition;
            target.localRotation = source.localRotation;
            target.localScale = source.localScale;

            var sourceScaleAdjuster = source.GetComponent<ModularAvatarScaleAdjuster>();
            if (sourceScaleAdjuster != null)
            {
                var targetScaleAdjuster = target.GetComponent<ModularAvatarScaleAdjuster>();
                if (targetScaleAdjuster == null)
                {
                    targetScaleAdjuster = target.gameObject.AddComponent<ModularAvatarScaleAdjuster>();
                }
                targetScaleAdjuster.Scale = sourceScaleAdjuster.Scale;
            }
        }

        private void CopyAllPhysBoneColliders()
        {
            if (sourceArmature == null) return;

            var sourceColliders = sourceArmature.GetComponentsInChildren<VRCPhysBoneCollider>(true);

            foreach (var sourceCollider in sourceColliders)
            {
                if (sourceCollider == null) continue;

                Transform sourceTransform = sourceCollider.transform;
                
                if (boneMappingAll.TryGetValue(sourceTransform, out Transform targetTransform))
                {
                    var targetCollider = CopyPhysBoneCollider(sourceCollider, targetTransform);
                    if (targetCollider != null)
                    {
                        colliderMapping[sourceCollider] = targetCollider;
                    }
                }
                else
                {
                    Debug.LogWarning($"[PhysBoneCollider] GameObject '{sourceCollider.name}' not found in target. Skipping.");
                }
            }
        }

        private VRCPhysBoneCollider CopyPhysBoneCollider(VRCPhysBoneCollider source, Transform targetTransform)
        {
            if (source == null || targetTransform == null) return null;

            VRCPhysBoneCollider target = targetTransform.GetComponent<VRCPhysBoneCollider>();
            if (target == null)
            {
                target = targetTransform.gameObject.AddComponent<VRCPhysBoneCollider>();
            }

            SerializedObject sourceObj = new SerializedObject(source);
            SerializedObject targetObj = new SerializedObject(target);

            CopySerializedProperty(sourceObj, targetObj, "shapeType");
            CopySerializedProperty(sourceObj, targetObj, "insideBounds");
            CopySerializedProperty(sourceObj, targetObj, "radius");
            CopySerializedProperty(sourceObj, targetObj, "height");
            CopySerializedProperty(sourceObj, targetObj, "position");
            CopySerializedProperty(sourceObj, targetObj, "rotation");
            CopySerializedProperty(sourceObj, targetObj, "bonesAsSpheres");

            targetObj.ApplyModifiedProperties();

            if (source.rootTransform != null)
            {
                if (boneMappingAll.TryGetValue(source.rootTransform, out Transform mappedRoot))
                {
                    target.rootTransform = mappedRoot;
                }
                else
                {
                    Debug.LogWarning($"[PhysBoneCollider] rootTransform '{source.rootTransform.name}' on '{source.name}' not found in target. Set to null.");
                    target.rootTransform = null;
                }
            }
            else
            {
                target.rootTransform = null;
            }

            EditorUtility.SetDirty(target);

            return target;
        }

        private void CopyAllPhysBones()
        {
            if (sourceArmature == null) return;

            var sourcePhysBones = sourceArmature.GetComponentsInChildren<VRCPhysBone>(true);

            foreach (var sourcePhysBone in sourcePhysBones)
            {
                if (sourcePhysBone == null) continue;

                Transform sourceTransform = sourcePhysBone.transform;
                
                if (boneMappingAll.TryGetValue(sourceTransform, out Transform targetTransform))
                {
                    CopyPhysBone(sourcePhysBone, targetTransform);
                }
                else
                {
                    Debug.LogWarning($"[PhysBone] GameObject '{sourcePhysBone.name}' not found in target. Skipping.");
                }
            }
        }

        private void CopyPhysBone(VRCPhysBone source, Transform targetTransform)
        {
            if (source == null || targetTransform == null) return;

            VRCPhysBone target = targetTransform.GetComponent<VRCPhysBone>();
            if (target == null)
            {
                target = targetTransform.gameObject.AddComponent<VRCPhysBone>();
            }

            SerializedObject sourceObj = new SerializedObject(source);
            SerializedObject targetObj = new SerializedObject(target);

            CopyAllPhysBoneProperties(sourceObj, targetObj);
            
            RemapRootTransform(source, target);
            RemapIgnoreTransforms(source, target);
            RemapAndSetColliders(source, target, targetObj);

            targetObj.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
        }

        private void CopyAllPhysBoneProperties(SerializedObject sourceObj, SerializedObject targetObj)
        {
            string[] properties = {
                "integrationType",
                "pull", "pullCurve", "spring", "springCurve",
                "stiffness", "stiffnessCurve", "gravity", "gravityCurve",
                "gravityFalloff", "gravityFalloffCurve",
                "immobile", "immobileCurve", "immobileType",
                "limitType", "maxAngleX", "maxAngleXCurve",
                "maxAngleZ", "maxAngleZCurve", "limitRotation",
                "limitRotationXCurve", "limitRotationYCurve", "limitRotationZCurve",
                "radius", "radiusCurve", "allowCollision", "collisionFilter",
                "stretchMotion", "stretchMotionCurve",
                "maxStretch", "maxStretchCurve", "maxSquish", "maxSquishCurve",
                "allowGrabbing", "allowPosing", "grabMovement", "snapToHand",
                "parameter", "isAnimated", "resetWhenDisabled",
                "multiChildType", "version"
            };

            foreach (var prop in properties)
            {
                CopySerializedProperty(sourceObj, targetObj, prop);
            }
        }

        private void RemapRootTransform(VRCPhysBone source, VRCPhysBone target)
        {
            if (source.rootTransform != null)
            {
                if (boneMappingAll.TryGetValue(source.rootTransform, out Transform mappedRoot))
                {
                    target.rootTransform = mappedRoot;
                }
                else
                {
                    Debug.LogWarning($"[PhysBone] rootTransform '{source.rootTransform.name}' on '{source.name}' not found in target. Set to null.");
                    target.rootTransform = null;
                }
            }
            else
            {
                target.rootTransform = null;
            }
        }

        private void RemapIgnoreTransforms(VRCPhysBone source, VRCPhysBone target)
        {
            target.ignoreTransforms = new List<Transform>();
            if (source.ignoreTransforms != null)
            {
                foreach (var ignoreTransform in source.ignoreTransforms)
                {
                    if (ignoreTransform == null) continue;

                    if (boneMappingAll.TryGetValue(ignoreTransform, out Transform mappedIgnore))
                    {
                        target.ignoreTransforms.Add(mappedIgnore);
                    }
                    else
                    {
                        Debug.LogWarning($"[PhysBone] ignoreTransform '{ignoreTransform.name}' on '{source.name}' not found in target. Skipping.");
                    }
                }
            }
        }

        private void RemapAndSetColliders(VRCPhysBone source, VRCPhysBone target, SerializedObject targetObj)
        {
            target.colliders = new List<VRCPhysBoneColliderBase>();
            if (source.colliders != null)
            {
                foreach (var sourceCollider in source.colliders)
                {
                    if (sourceCollider == null) continue;

                    if (sourceCollider is VRCPhysBoneCollider collider)
                    {
                        if (colliderMapping.TryGetValue(collider, out VRCPhysBoneCollider mappedCollider))
                        {
                            target.colliders.Add(mappedCollider);
                        }
                        else
                        {
                            Debug.LogWarning($"[PhysBone] Collider '{collider.name}' on '{source.name}' not found in collider mapping. Skipping.");
                        }
                    }
                }
            }

            SerializedProperty collidersProp = targetObj.FindProperty("colliders");
            if (collidersProp != null && collidersProp.isArray)
            {
                collidersProp.arraySize = target.colliders.Count;
                for (int i = 0; i < target.colliders.Count; i++)
                {
                    SerializedProperty elementProp = collidersProp.GetArrayElementAtIndex(i);
                    elementProp.objectReferenceValue = target.colliders[i];
                }
            }
        }

        private void CopySerializedProperty(SerializedObject source, SerializedObject target, string propertyName)
        {
            SerializedProperty sourceProp = source.FindProperty(propertyName);
            SerializedProperty targetProp = target.FindProperty(propertyName);

            if (sourceProp != null && targetProp != null)
            {
                switch (sourceProp.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        targetProp.intValue = sourceProp.intValue;
                        break;
                    case SerializedPropertyType.Boolean:
                        targetProp.boolValue = sourceProp.boolValue;
                        break;
                    case SerializedPropertyType.Float:
                        targetProp.floatValue = sourceProp.floatValue;
                        break;
                    case SerializedPropertyType.String:
                        targetProp.stringValue = sourceProp.stringValue;
                        break;
                    case SerializedPropertyType.Color:
                        targetProp.colorValue = sourceProp.colorValue;
                        break;
                    case SerializedPropertyType.ObjectReference:
                        break;
                    case SerializedPropertyType.Enum:
                        targetProp.enumValueIndex = sourceProp.enumValueIndex;
                        break;
                    case SerializedPropertyType.Vector2:
                        targetProp.vector2Value = sourceProp.vector2Value;
                        break;
                    case SerializedPropertyType.Vector3:
                        targetProp.vector3Value = sourceProp.vector3Value;
                        break;
                    case SerializedPropertyType.Vector4:
                        targetProp.vector4Value = sourceProp.vector4Value;
                        break;
                    case SerializedPropertyType.Rect:
                        targetProp.rectValue = sourceProp.rectValue;
                        break;
                    case SerializedPropertyType.AnimationCurve:
                        targetProp.animationCurveValue = sourceProp.animationCurveValue;
                        break;
                    case SerializedPropertyType.Bounds:
                        targetProp.boundsValue = sourceProp.boundsValue;
                        break;
                    case SerializedPropertyType.Quaternion:
                        targetProp.quaternionValue = sourceProp.quaternionValue;
                        break;
                }
            }
        }

        private void CopyBlendShapes(SkinnedMeshRenderer source, SkinnedMeshRenderer target)
        {
            Mesh sourceMesh = source.sharedMesh;
            Mesh targetMesh = target.sharedMesh;

            if (sourceMesh == null || targetMesh == null)
            {
                return;
            }

            int sourceBlendShapeCount = sourceMesh.blendShapeCount;

            for (int i = 0; i < sourceBlendShapeCount; i++)
            {
                string blendShapeName = sourceMesh.GetBlendShapeName(i);
                float weight = source.GetBlendShapeWeight(i);

                int targetIndex = targetMesh.GetBlendShapeIndex(blendShapeName);
                if (targetIndex >= 0)
                {
                    target.SetBlendShapeWeight(targetIndex, weight);
                }
            }
        }

        private void CopyAvatarDescriptor()
        {
            var sourceDescriptor = sourceAvatar.GetComponent<VRCAvatarDescriptor>();
            if (sourceDescriptor == null)
            {
                Debug.LogWarning("Source avatar does not have VRCAvatarDescriptor component.");
                return;
            }

            var targetAnimator = targetArmature.GetComponentInParent<Animator>();
            if (targetAnimator == null)
            {
                Debug.LogError("Target armature does not have an Animator in parent hierarchy.");
                return;
            }

            var targetDescriptor = targetAnimator.GetComponent<VRCAvatarDescriptor>();
            if (targetDescriptor == null)
            {
                targetDescriptor = targetAnimator.gameObject.AddComponent<VRCAvatarDescriptor>();
            }

            Undo.RecordObject(targetDescriptor, "Copy Avatar Descriptor");

            string json = EditorJsonUtility.ToJson(sourceDescriptor);
            EditorJsonUtility.FromJsonOverwrite(json, targetDescriptor);

            EditorUtility.SetDirty(targetDescriptor);
        }
    }
}