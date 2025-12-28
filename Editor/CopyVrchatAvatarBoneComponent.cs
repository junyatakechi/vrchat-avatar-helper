using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.Dynamics;

namespace JayT.UnityAvatarTools.Editor
{
    public class CopyVrchatAvatarBoneComponent : EditorWindow
    {
        [SerializeField] private Animator sourceAvatar;
        [SerializeField] private Transform sourceArmature;
        [SerializeField] private Transform targetArmature;
        [SerializeField] private List<SkinnedMeshRenderer> sourceSkinnedMeshes = new List<SkinnedMeshRenderer>();
        [SerializeField] private List<SkinnedMeshRenderer> targetSkinnedMeshes = new List<SkinnedMeshRenderer>();
        [SerializeField] private int skinnedMeshCount = 0;
        [SerializeField] private bool copyPhysBones = true;
        [SerializeField] private bool copyPhysBoneColliders = true;

        private Vector2 scrollPosition;
        private Dictionary<Transform, Transform> boneMapping = new Dictionary<Transform, Transform>();
        private Dictionary<VRCPhysBoneCollider, VRCPhysBoneCollider> colliderMapping = new Dictionary<VRCPhysBoneCollider, VRCPhysBoneCollider>();

        [MenuItem("Tools/JayT/UnityAvatarTools/Copy Avatar Bone Transform")]
        public static void ShowWindow()
        {
            GetWindow<CopyVrchatAvatarBoneComponent>("Copy Avatar Bone Transform");
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            GUILayout.Label("Copy Avatar Bone Transform", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            sourceAvatar = (Animator)EditorGUILayout.ObjectField("Source Avatar", sourceAvatar, typeof(Animator), true);
            sourceArmature = (Transform)EditorGUILayout.ObjectField("Source Armature", sourceArmature, typeof(Transform), true);
            
            EditorGUILayout.Space();
            
            targetArmature = (Transform)EditorGUILayout.ObjectField("Target Armature", targetArmature, typeof(Transform), true);

            EditorGUILayout.Space();
            
            EditorGUILayout.LabelField("PhysBone Options", EditorStyles.boldLabel);
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

            if (GUILayout.Button("Copy Bone Transforms"))
            {
                CopyBoneTransforms();
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

        private void CopyBoneTransforms()
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
            boneMapping.Clear();
            colliderMapping.Clear();

            // Build bone mapping
            BuildBoneMapping(sourceArmature, targetArmature);

            // Copy Armature if specified
            if (sourceArmature != null && targetArmature != null)
            {
                CopyTransformAndScaleAdjuster(sourceArmature, targetArmature);
            }

            // Copy Humanoid bones
            var humanBones = System.Enum.GetValues(typeof(HumanBodyBones));

            foreach (HumanBodyBones bone in humanBones)
            {
                if (bone == HumanBodyBones.LastBone) continue;

                Transform sourceBone = sourceAvatar.GetBoneTransform(bone);
                
                if (sourceBone != null)
                {
                    string boneName = sourceBone.gameObject.name;
                    Transform targetBone = FindBoneByName(targetArmature != null ? targetArmature : targetArmature.transform, boneName);
                    
                    if (targetBone != null)
                    {
                        CopyTransformAndScaleAdjuster(sourceBone, targetBone);
                    }
                }
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

            EditorUtility.DisplayDialog("Complete", "Bone transforms copied successfully.", "OK");
        }

        private void BuildBoneMapping(Transform sourceRoot, Transform targetRoot)
        {
            if (sourceRoot == null || targetRoot == null) return;

            // Add root mapping
            boneMapping[sourceRoot] = targetRoot;

            // Recursively build mapping for all children
            BuildBoneMappingRecursive(sourceRoot, targetRoot);
        }

        private void BuildBoneMappingRecursive(Transform sourceParent, Transform targetRoot)
        {
            foreach (Transform sourceChild in sourceParent)
            {
                Transform targetChild = FindBoneByName(targetRoot, sourceChild.name);
                if (targetChild != null)
                {
                    boneMapping[sourceChild] = targetChild;
                }
                BuildBoneMappingRecursive(sourceChild, targetRoot);
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
                Transform sourceTransform = sourceCollider.transform;
                
                if (boneMapping.TryGetValue(sourceTransform, out Transform targetTransform))
                {
                    var targetCollider = CopyPhysBoneCollider(sourceCollider, targetTransform);
                    if (targetCollider != null)
                    {
                        colliderMapping[sourceCollider] = targetCollider;
                    }
                }
            }
        }

        private VRCPhysBoneCollider CopyPhysBoneCollider(VRCPhysBoneCollider source, Transform targetTransform)
        {
            // Check if collider already exists
            var existingCollider = targetTransform.GetComponent<VRCPhysBoneCollider>();
            VRCPhysBoneCollider target = existingCollider != null ? existingCollider : targetTransform.gameObject.AddComponent<VRCPhysBoneCollider>();

            Undo.RecordObject(target, "Copy PhysBone Collider");

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

            // Remap rootTransform
            if (source.rootTransform != null && boneMapping.TryGetValue(source.rootTransform, out Transform mappedRoot))
            {
                target.rootTransform = mappedRoot;
            }
            else
            {
                target.rootTransform = null;
            }

            targetObj.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);

            return target;
        }

        private void CopyAllPhysBones()
        {
            if (sourceArmature == null) return;

            var sourcePhysBones = sourceArmature.GetComponentsInChildren<VRCPhysBone>(true);

            foreach (var sourcePhysBone in sourcePhysBones)
            {
                Transform sourceTransform = sourcePhysBone.transform;
                
                if (boneMapping.TryGetValue(sourceTransform, out Transform targetTransform))
                {
                    CopyPhysBone(sourcePhysBone, targetTransform);
                }
            }
        }

        private void CopyPhysBone(VRCPhysBone source, Transform targetTransform)
        {
            // Check if PhysBone already exists
            var existingPhysBone = targetTransform.GetComponent<VRCPhysBone>();
            VRCPhysBone target = existingPhysBone != null ? existingPhysBone : targetTransform.gameObject.AddComponent<VRCPhysBone>();

            Undo.RecordObject(target, "Copy PhysBone");

            SerializedObject sourceObj = new SerializedObject(source);
            SerializedObject targetObj = new SerializedObject(target);

            // Copy all serialized properties except references that need remapping
            SerializedProperty sourceProp = sourceObj.GetIterator();
            sourceProp.Next(true);

            do
            {
                string propName = sourceProp.name;

                // Skip properties that need special handling
                if (propName == "m_Script" || propName == "rootTransform" || propName == "ignoreTransforms" || propName == "colliders")
                    continue;

                CopySerializedProperty(sourceObj, targetObj, propName);
            }
            while (sourceProp.Next(false));

            targetObj.ApplyModifiedProperties();

            // Remap rootTransform
            if (source.rootTransform != null && boneMapping.TryGetValue(source.rootTransform, out Transform mappedRoot))
            {
                target.rootTransform = mappedRoot;
            }
            else
            {
                target.rootTransform = null;
            }

            // Remap ignoreTransforms
            target.ignoreTransforms = new List<Transform>();
            if (source.ignoreTransforms != null)
            {
                foreach (var ignoreTransform in source.ignoreTransforms)
                {
                    if (ignoreTransform != null && boneMapping.TryGetValue(ignoreTransform, out Transform mappedIgnore))
                    {
                        target.ignoreTransforms.Add(mappedIgnore);
                    }
                }
            }

            // Remap colliders
            target.colliders = new List<VRCPhysBoneColliderBase>();
            if (source.colliders != null)
            {
                foreach (var sourceCollider in source.colliders)
                {
                    if (sourceCollider is VRCPhysBoneCollider collider && colliderMapping.TryGetValue(collider, out VRCPhysBoneCollider mappedCollider))
                    {
                        target.colliders.Add(mappedCollider);
                    }
                }
            }

            EditorUtility.SetDirty(target);
        }

        private void CopySerializedProperty(SerializedObject source, SerializedObject target, string propertyName)
        {
            SerializedProperty sourceProp = source.FindProperty(propertyName);
            SerializedProperty targetProp = target.FindProperty(propertyName);

            if (sourceProp != null && targetProp != null)
            {
                target.CopyFromSerializedProperty(sourceProp);
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
    }
}
