// Assets/Editor/RemoveVRCComponents.cs
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace JayT.VRChatAvatarHelper.Editor
{
    public class RemoveVRCComponents : EditorWindow
    {
        private GameObject targetAvatar;

        [MenuItem("Tools/JayT/VRChatAvatarHelper/Remove VRC & NDMF Components")]
        public static void ShowWindow()
        {
            GetWindow<RemoveVRCComponents>("Remove VRC Components");
        }

        private void OnGUI()
        {
            GUILayout.Label("対象アバター（ルートオブジェクト）", EditorStyles.boldLabel);
            targetAvatar = (GameObject)EditorGUILayout.ObjectField(targetAvatar, typeof(GameObject), true);

            GUILayout.Space(10);

            if (GUILayout.Button("不要コンポーネントを一括削除"))
            {
                if (targetAvatar == null)
                {
                    EditorUtility.DisplayDialog("エラー", "アバターを指定してください。", "OK");
                    return;
                }

                RemoveComponents();
            }
        }

        private void RemoveComponents()
        {
            var targetTypeNames = new List<string>
            {
                // ===== VRChat Avatar SDK =====
                // アバター基本
                "VRCAvatarDescriptor",
                "PipelineManager",          // VRCPipelineManager

                // PhysBone
                "VRCPhysBone",
                "VRCPhysBoneCollider",

                // Contact
                "VRCContactReceiver",
                "VRCContactSender",

                // Constraints
                "VRCAimConstraint",
                "VRCLookAtConstraint",
                "VRCParentConstraint",
                "VRCPositionConstraint",
                "VRCRotationConstraint",
                "VRCScaleConstraint",

                // その他アバターコンポーネント
                "VRCSpatialAudioSource",
                "VRCStation",
                "VRCHeadChop",
                "VRCIKFollower",            // 非推奨だが残存する場合あり

                // ===== NDMF Portable Components =====
                "PortableDynamicBone",
                "PortableDynamicBoneCollider",
            };

            var components = targetAvatar.GetComponentsInChildren<Component>(true);
            int count = 0;

            foreach (var component in components)
            {
                if (component == null) continue;
                string typeName = component.GetType().Name;
                if (targetTypeNames.Contains(typeName))
                {
                    Undo.DestroyObjectImmediate(component);
                    count++;
                }
            }

            EditorUtility.DisplayDialog("完了", $"{count} 個のコンポーネントを削除しました。", "OK");
            Debug.Log($"[RemoveVRCComponents] {count} 個削除完了");
        }
    }
}