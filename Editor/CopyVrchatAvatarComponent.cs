using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;

namespace JayT.VRChatAvatarHelper.Editor
{
    public class CopyVrchatAvatarComponent : EditorWindow
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

        [MenuItem("Tools/JayT/VRChatAvatarHelper/Copy VrchatAvatar Components")]
        public static void ShowWindow()
        {
            GetWindow<CopyVrchatAvatarComponent>("Copy VrchatAvatar Components");
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            GUILayout.Label("Copy VrchatAvatar Components", EditorStyles.boldLabel);
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

            // Clear mappings
            boneMappingHumanoidOnly.Clear();
            boneMappingAll.Clear();
            colliderMapping.Clear();

            // Build bone mappings
            BuildHumanoidBoneMapping();
            BuildAllBoneMapping(sourceArmature, targetArmature);

            // Copy bone transforms if enabled
            if (copyBoneTransforms)
            {
                // Copy Armature transform
                if (sourceArmature != null && targetArmature != null)
                {
                    CopyTransformAndScaleAdjuster(sourceArmature, targetArmature);
                }

                // Copy Humanoid bone transforms only
                CopyHumanoidBoneTransforms();
            }

            // Copy PhysBone Colliders first (PhysBones reference them)
            if (copyPhysBoneColliders)
            {
                CopyAllPhysBoneColliders();
            }

            // Copy PhysBones
            if (copyPhysBones)
            {
                CopyAllPhysBones();
            }

            // Copy BlendShapes
            for (int i = 0; i < skinnedMeshCount; i++)
            {
                if (sourceSkinnedMeshes[i] != null && targetSkinnedMeshes[i] != null)
                {
                    CopyBlendShapes(sourceSkinnedMeshes[i], targetSkinnedMeshes[i]);
                }
            }

            // Copy Avatar Descriptor
            if (copyAvatarDescriptor)
            {
                CopyAvatarDescriptor();
            }

            EditorUtility.DisplayDialog("Complete", "Components copied successfully.", "OK");
        }

        private void BuildHumanoidBoneMapping()
        {
            // Add armature mapping
            if (sourceArmature != null && targetArmature != null)
            {
                boneMappingHumanoidOnly[sourceArmature] = targetArmature;
            }

            // Map only Humanoid bones
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

            // Add root mapping
            boneMappingAll[sourceRoot] = targetRoot;

            // Recursively build mapping for all children
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
            // Copy Transform
            target.localPosition = source.localPosition;
            target.localRotation = source.localRotation;
            target.localScale = source.localScale;

            // Copy ModularAvatarScaleAdjuster if exists
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
                
                // Try to find target transform in all bone mapping
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

            // Get or add PhysBoneCollider component
            VRCPhysBoneCollider target = targetTransform.GetComponent<VRCPhysBoneCollider>();
            if (target == null)
            {
                target = targetTransform.gameObject.AddComponent<VRCPhysBoneCollider>();
            }

            // Copy properties using SerializedObject
            SerializedObject sourceObj = new SerializedObject(source);
            SerializedObject targetObj = new SerializedObject(target);

            // Copy basic properties
            CopySerializedProperty(sourceObj, targetObj, "shapeType");
            CopySerializedProperty(sourceObj, targetObj, "insideBounds");
            CopySerializedProperty(sourceObj, targetObj, "radius");
            CopySerializedProperty(sourceObj, targetObj, "height");
            CopySerializedProperty(sourceObj, targetObj, "position");
            CopySerializedProperty(sourceObj, targetObj, "rotation");
            CopySerializedProperty(sourceObj, targetObj, "bonesAsSpheres");

            targetObj.ApplyModifiedProperties();

            // Remap rootTransform with null check
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

            // Mark dirty only once at the end
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
                
                // Try to find target transform in all bone mapping
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

            // Get or add PhysBone component
            VRCPhysBone target = targetTransform.GetComponent<VRCPhysBone>();
            if (target == null)
            {
                target = targetTransform.gameObject.AddComponent<VRCPhysBone>();
            }

            SerializedObject sourceObj = new SerializedObject(source);
            SerializedObject targetObj = new SerializedObject(target);

            // Explicitly copy all VRCPhysBone properties (excluding references that need remapping)
            
            // Integration
            CopySerializedProperty(sourceObj, targetObj, "integrationType");
            
            // Forces
            CopySerializedProperty(sourceObj, targetObj, "pull");
            CopySerializedProperty(sourceObj, targetObj, "pullCurve");
            CopySerializedProperty(sourceObj, targetObj, "spring");
            CopySerializedProperty(sourceObj, targetObj, "springCurve");
            CopySerializedProperty(sourceObj, targetObj, "stiffness");
            CopySerializedProperty(sourceObj, targetObj, "stiffnessCurve");
            CopySerializedProperty(sourceObj, targetObj, "gravity");
            CopySerializedProperty(sourceObj, targetObj, "gravityCurve");
            CopySerializedProperty(sourceObj, targetObj, "gravityFalloff");
            CopySerializedProperty(sourceObj, targetObj, "gravityFalloffCurve");
            CopySerializedProperty(sourceObj, targetObj, "immobile");
            CopySerializedProperty(sourceObj, targetObj, "immobileCurve");
            CopySerializedProperty(sourceObj, targetObj, "immobileType");
            
            // Limits
            CopySerializedProperty(sourceObj, targetObj, "limitType");
            CopySerializedProperty(sourceObj, targetObj, "maxAngleX");
            CopySerializedProperty(sourceObj, targetObj, "maxAngleXCurve");
            CopySerializedProperty(sourceObj, targetObj, "maxAngleZ");
            CopySerializedProperty(sourceObj, targetObj, "maxAngleZCurve");
            CopySerializedProperty(sourceObj, targetObj, "limitRotation");
            CopySerializedProperty(sourceObj, targetObj, "limitRotationXCurve");
            CopySerializedProperty(sourceObj, targetObj, "limitRotationYCurve");
            CopySerializedProperty(sourceObj, targetObj, "limitRotationZCurve");
            
            // Collision
            CopySerializedProperty(sourceObj, targetObj, "radius");
            CopySerializedProperty(sourceObj, targetObj, "radiusCurve");
            CopySerializedProperty(sourceObj, targetObj, "allowCollision");
            CopySerializedProperty(sourceObj, targetObj, "collisionFilter");
            
            // Stretch & Squish
            CopySerializedProperty(sourceObj, targetObj, "stretchMotion");
            CopySerializedProperty(sourceObj, targetObj, "stretchMotionCurve");
            CopySerializedProperty(sourceObj, targetObj, "maxStretch");
            CopySerializedProperty(sourceObj, targetObj, "maxStretchCurve");
            CopySerializedProperty(sourceObj, targetObj, "maxSquish");
            CopySerializedProperty(sourceObj, targetObj, "maxSquishCurve");
            
            // Grab & Pose
            CopySerializedProperty(sourceObj, targetObj, "allowGrabbing");
            CopySerializedProperty(sourceObj, targetObj, "allowPosing");
            CopySerializedProperty(sourceObj, targetObj, "grabMovement");
            CopySerializedProperty(sourceObj, targetObj, "snapToHand");
            
            // Options
            CopySerializedProperty(sourceObj, targetObj, "parameter");
            CopySerializedProperty(sourceObj, targetObj, "isAnimated");
            CopySerializedProperty(sourceObj, targetObj, "resetWhenDisabled");
            
            // Multi-Child
            CopySerializedProperty(sourceObj, targetObj, "multiChildType");
            
            // Deprecated/Legacy properties (if they exist)
            CopySerializedProperty(sourceObj, targetObj, "version");
            
            targetObj.ApplyModifiedProperties();

            // Remap rootTransform with null check
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

            // Remap ignoreTransforms with null check
            target.ignoreTransforms = new List<Transform>();
            if (source.ignoreTransforms != null)
            {
                foreach (var ignoreTransform in source.ignoreTransforms)
                {
                    // Skip if null
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

            // Remap colliders with null check
            target.colliders = new List<VRCPhysBoneColliderBase>();
            if (source.colliders != null)
            {
                foreach (var sourceCollider in source.colliders)
                {
                    // Skip if null
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

            // SerializedObjectを使ってcollidersを確実に保存
            SerializedObject finalTargetObj = new SerializedObject(target);
            SerializedProperty collidersProp = finalTargetObj.FindProperty("colliders");
            
            if (collidersProp != null && collidersProp.isArray)
            {
                int colliderCount = target.colliders.Count;
                collidersProp.arraySize = colliderCount;
                
                // 各要素に参照をアサイン
                for (int i = 0; i < colliderCount; i++)
                {
                    SerializedProperty elementProp = collidersProp.GetArrayElementAtIndex(i);
                    elementProp.objectReferenceValue = target.colliders[i];
                }
                
                finalTargetObj.ApplyModifiedProperties();
                
                // Debug.Log($"[PhysBone] '{source.name}' -> Colliders Size: {colliderCount}, Assigned: {colliderCount}");
            }

            // Mark dirty only once at the end, after all properties are set
            EditorUtility.SetDirty(target);
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
                        // targetProp.objectReferenceValue = sourceProp.objectReferenceValue;
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

            // Use JSON serialization to copy all properties
            string json = EditorJsonUtility.ToJson(sourceDescriptor);
            EditorJsonUtility.FromJsonOverwrite(json, targetDescriptor);

            EditorUtility.SetDirty(targetDescriptor);
        }
    }
}