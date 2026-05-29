using System.IO;
using UnityEditor;
using UnityEngine;

namespace BetterTabs
{
    internal static class BetterTabsInteractionHandler
    {
        public static void OpenAsset(string path)
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (obj != null) AssetDatabase.OpenAsset(obj);
        }

        public static void RevealInExplorer(string path)
        {
            EditorUtility.RevealInFinder(path);
        }

        public static void CopyPath(string path)
        {
            EditorGUIUtility.systemCopyBuffer = path;
        }

        public static string Duplicate(string path)
        {
            var ext = Path.GetExtension(path);
            var nameNoExt = Path.GetFileNameWithoutExtension(path);
            var dir = Path.GetDirectoryName(path)?.Replace('\\', '/');

            string newPath;
            int n = 1;
            do
            {
                var candidate = $"{nameNoExt} ({n}){ext}";
                newPath = $"{dir}/{candidate}";
                n++;
            } while (AssetDatabase.AssetPathExists(newPath));

            AssetDatabase.CopyAsset(path, newPath);
            AssetDatabase.Refresh();
            return newPath;
        }

        public static bool Delete(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            bool confirmed = EditorUtility.DisplayDialog(
                "Delete Asset",
                $"Are you sure you want to delete '{name}'?\nThis cannot be undone.",
                "Delete", "Cancel");

            if (!confirmed) return false;
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.Refresh();
            return true;
        }

        public static void SelectDependencies(string path)
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (obj == null) return;
            Selection.objects = EditorUtility.CollectDependencies(new Object[] { obj });
        }

        public static void OpenProperties(string path)
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (obj == null) return;
            EditorUtility.OpenPropertyEditor(obj);
        }

        public static void ShowInProject(string path)
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (obj == null) return;
            Selection.activeObject = obj;
            EditorGUIUtility.PingObject(obj);
        }

        static void AddCreateSubmenu(GenericMenu menu, string targetFolder,
            System.Action<string> onRenameRequested, System.Action onRefresh)
        {
            Object folderObj = AssetDatabase.LoadAssetAtPath<Object>(targetFolder);
            string[] allItems = Unsupported.GetSubmenus("Assets/Create");

            foreach (string fullPath in allItems)
            {
                if (string.IsNullOrEmpty(fullPath)) continue;

                string displayPath = fullPath.StartsWith("Assets/")
                    ? fullPath.Substring("Assets/".Length)
                    : fullPath;

                string capturedPath = fullPath;
                menu.AddItem(new GUIContent(displayPath), false, () =>
                {
                    if (folderObj != null) Selection.activeObject = folderObj;
                    EditorApplication.ExecuteMenuItem(capturedPath);
                    onRefresh?.Invoke();
                });
            }
        }

        public static GenericMenu BuildContextMenu(
            string path,
            System.Action<string> onRenameRequested,
            System.Action<string> onDeletedOrDuplicated,
            System.Action onRefresh)
        {
            var menu = new GenericMenu();
            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            string parentFolder = Path.GetDirectoryName(path)?.Replace('\\', '/');

            AddCreateSubmenu(menu, parentFolder, onRenameRequested, onRefresh);
            menu.AddSeparator("");

            menu.AddItem(new GUIContent("Open"), false, () => OpenAsset(path));
            menu.AddItem(new GUIContent("Show in Project"), false, () => ShowInProject(path));
            menu.AddItem(new GUIContent("Reveal in Explorer"), false, () => RevealInExplorer(path));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Rename"), false, () => onRenameRequested?.Invoke(path));
            menu.AddItem(new GUIContent("Duplicate"), false, () =>
            {
                Duplicate(path);
                onDeletedOrDuplicated?.Invoke(path);
            });
            menu.AddItem(new GUIContent("Delete"), false, () =>
            {
                if (Delete(path)) onDeletedOrDuplicated?.Invoke(path);
            });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Copy Path"), false, () => CopyPath(path));
            menu.AddItem(new GUIContent("Select Dependencies"), false, () => SelectDependencies(path));
            menu.AddItem(new GUIContent("Find References In Project"), false, () =>
            {
                if (obj != null)
                {
                    Selection.activeObject = obj;
                    EditorApplication.ExecuteMenuItem("Assets/Find References In Project");
                }
            });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Reimport"), false, () =>
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate));
            menu.AddItem(new GUIContent("Refresh"), false, () =>
            {
                AssetDatabase.Refresh();
                onRefresh?.Invoke();
            });
            menu.AddSeparator("");
            if (obj != null)
                menu.AddItem(new GUIContent("Properties"), false, () => OpenProperties(path));
            else
                menu.AddDisabledItem(new GUIContent("Properties"));

            return menu;
        }

        public static GenericMenu BuildFolderContextMenu(
            string folderPath,
            System.Action<string> onRenameRequested,
            System.Action<string> onDeletedOrDuplicated,
            System.Action onRefresh)
        {
            var menu = new GenericMenu();

            AddCreateSubmenu(menu, folderPath, onRenameRequested, onRefresh);
            menu.AddSeparator("");

            menu.AddItem(new GUIContent("Show in Project"), false, () => ShowInProject(folderPath));
            menu.AddItem(new GUIContent("Reveal in Explorer"), false, () => RevealInExplorer(folderPath));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Rename"), false, () => onRenameRequested?.Invoke(folderPath));
            menu.AddItem(new GUIContent("Delete"), false, () =>
            {
                if (Delete(folderPath)) onDeletedOrDuplicated?.Invoke(folderPath);
            });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Copy Path"), false, () => CopyPath(folderPath));
            menu.AddItem(new GUIContent("Find References In Project"), false, () =>
            {
                var obj = AssetDatabase.LoadAssetAtPath<Object>(folderPath);
                if (obj != null)
                {
                    Selection.activeObject = obj;
                    EditorApplication.ExecuteMenuItem("Assets/Find References In Project");
                }
            });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Reimport"), false, () =>
                AssetDatabase.ImportAsset(folderPath, ImportAssetOptions.ForceUpdate));
            menu.AddItem(new GUIContent("Refresh"), false, () =>
            {
                AssetDatabase.Refresh();
                onRefresh?.Invoke();
            });

            return menu;
        }
    }
}
