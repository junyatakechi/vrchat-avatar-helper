using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using nadena.dev.modular_avatar.core;

namespace JayT.UnityAvatarTools.Editor
{
    public class CopyAvatarBoneTransform : EditorWindow
    {
        [SerializeField] private Animator sourceAvatar;
        [SerializeField] private Animator targetAvatar;
        [SerializeField] private Transform sourceArmature;
        [SerializeField] private Transform targetArmature;
        [SerializeField] private List<SkinnedMeshRenderer> sourceSkinnedMeshes = new List<SkinnedMeshRenderer>();
        [SerializeField] private List<SkinnedMeshRenderer> targetSkinnedMeshes = new List<SkinnedMeshRenderer>();
        [SerializeField] private int skinnedMeshCount = 0;

        private Vector2 scrollPosition;

        [MenuItem("Tools/JayT/UnityAvatarTools/Copy Avatar Bone Transform")]
        public static void ShowWindow()
        {
            GetWindow<CopyAvatarBoneTransform>("Copy Avatar Bone Transform");
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            GUILayout.Label("Copy Avatar Bone Transform", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            sourceAvatar = (Animator)EditorGUILayout.ObjectField("Source Avatar", sourceAvatar, typeof(Animator), true);
            sourceArmature = (Transform)EditorGUILayout.ObjectField("Source Armature", sourceArmature, typeof(Transform), true);
            
            EditorGUILayout.Space();
            
            targetAvatar = (Animator)EditorGUILayout.ObjectField("Target Avatar", targetAvatar, typeof(Animator), true);
            targetArmature = (Transform)EditorGUILayout.ObjectField("Target Armature", targetArmature, typeof(Transform), true);

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
    if (sourceAvatar == null || targetAvatar == null)
    {
        EditorUtility.DisplayDialog("Error", "Source and Target avatars must be assigned.", "OK");
        return;
    }

    if (!sourceAvatar.isHuman || !targetAvatar.isHuman)
    {
        EditorUtility.DisplayDialog("Error", "Both avatars must be Humanoid.", "OK");
        return;
    }

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
            // sourceBoneの名前を取得
            string boneName = sourceBone.gameObject.name;
            
            // targetAvatarのArmature配下から同じ名前のボーンを検索
            Transform targetBone = FindBoneByName(targetArmature != null ? targetArmature : targetAvatar.transform, boneName);
            
            if (targetBone != null)
            {
                CopyTransformAndScaleAdjuster(sourceBone, targetBone);
            }
        }
    }

    // Copy BlendShapes
    for (int i = 0; i < skinnedMeshCount; i++)
    {
        if (sourceSkinnedMeshes[i] != null && targetSkinnedMeshes[i] != null)
        {
            CopyBlendShapes(sourceSkinnedMeshes[i], targetSkinnedMeshes[i]);
        }
    }
}

// ボーン名で検索するヘルパーメソッド
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