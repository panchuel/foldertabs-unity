using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BetterTabs
{
    internal class BetterTreeRenderer
    {
        const int RowHeight = 20;
        const int IndentWidth = 12;

        readonly Dictionary<string, (List<string> files, List<string> folders)> _cache
            = new Dictionary<string, (List<string>, List<string>)>();

        readonly Dictionary<string, bool> _expanded = new Dictionary<string, bool>();

        System.Action _onChanged;


        // ── Interaction state (set each frame via SetCallbacks) ───────────────
        string _selectedAssetPath;
        string _renamingPath;
        string _renameBuffer;

        System.Action<string> _onSelected;
        System.Action<string> _onRenameRequested;
        System.Action<string> _onRenameCommit;
        System.Action         _onRenameCancel;
        System.Action<string> _onAssetChanged;
        System.Action         _onRefreshRequested;

        public BetterTreeRenderer(System.Action onChanged)
        {
            _onChanged = onChanged;
        }

        // ── Public API ───────────────────────────────────────────────────────

        public void LoadExpandedState(List<string> expandedPaths)
        {
            _expanded.Clear();
            if (expandedPaths == null) return;
            foreach (var p in expandedPaths)
                _expanded[p] = true;
        }

        public void SaveExpandedState(List<string> target)
        {
            target.Clear();
            foreach (var kv in _expanded)
                if (kv.Value)
                    target.Add(kv.Key);
        }

        public void InvalidateCache() => _cache.Clear();

        public void InvalidatePath(string folderPath)
        {
            _cache.Remove(folderPath);
            var parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
            if (parent != null) _cache.Remove(parent);
        }

        public float MeasureHeight(string rootPath) => MeasureFolder(rootPath, 0);

        public void SetCallbacks(
            string selectedAssetPath,
            string renamingPath,
            string renameBuffer,
            System.Action<string> onSelected,
            System.Action<string> onRenameRequested,
            System.Action<string> onRenameCommit,
            System.Action onRenameCancel,
            System.Action<string> onAssetChanged,
            System.Action onRefreshRequested)
        {
            _selectedAssetPath   = selectedAssetPath;
            _renamingPath        = renamingPath;
            _renameBuffer        = renameBuffer;
            _onSelected          = onSelected;
            _onRenameRequested   = onRenameRequested;
            _onRenameCommit      = onRenameCommit;
            _onRenameCancel      = onRenameCancel;
            _onAssetChanged      = onAssetChanged;
            _onRefreshRequested  = onRefreshRequested;
        }

        public void Draw(string rootPath, float contentWidth,
            System.Action<string> onAssetDragged)
        {
            DrawFolder(rootPath, 0, contentWidth, onAssetDragged, ref _dummyY);
            HandleKeyboardForSelection();
        }

        float _dummyY;

        // ── Measurement ──────────────────────────────────────────────────────

        float MeasureFolder(string folderPath, int depth)
        {
            EnsureCached(folderPath);
            var (files, folders) = _cache[folderPath];

            float h = files.Count * RowHeight;
            foreach (var sub in folders)
            {
                h += RowHeight;
                if (IsExpanded(sub))
                    h += MeasureFolder(sub, depth + 1);
            }
            return h;
        }

        // ── Draw ─────────────────────────────────────────────────────────────

        void DrawFolder(string folderPath, int depth, float contentWidth,
            System.Action<string> onDragged, ref float _unused)
        {
            EnsureCached(folderPath);
            var (files, folders) = _cache[folderPath];

            foreach (var sub in folders)
            {
                bool wasExpanded = IsExpanded(sub);
                bool nowExpanded = DrawFolderHeader(sub, depth, contentWidth, wasExpanded);

                if (nowExpanded != wasExpanded)
                {
                    _expanded[sub] = nowExpanded;
                    _onChanged?.Invoke();
                }

                if (nowExpanded)
                    DrawFolder(sub, depth + 1, contentWidth, onDragged, ref _unused);
            }

            foreach (var file in files)
                DrawAssetRow(file, depth, contentWidth, onDragged);
        }

        // ── Folder header ────────────────────────────────────────────────────

        bool DrawFolderHeader(string folderPath, int depth, float contentWidth, bool expanded)
        {
            var rect = GUILayoutUtility.GetRect(contentWidth, RowHeight);
            float indent = depth * IndentWidth + 2;
            var ev = Event.current;

            bool isRenaming = folderPath == _renamingPath;

            // ── Inline rename mode ────────────────────────────────────────────
            if (isRenaming)
            {
                var folderIcon = EditorGUIUtility.IconContent("Folder Icon").image as Texture2D;
                if (folderIcon != null)
                    GUI.DrawTexture(new Rect(rect.x + indent + 2, rect.y + 2, 16, 16), folderIcon, ScaleMode.ScaleToFit);

                GUI.SetNextControlName("BetterTabsRenameField");
                var nameArea = new Rect(rect.x + indent + 22, rect.y + 1, rect.width - indent - 60, rect.height - 2);
                string newName = EditorGUI.TextField(nameArea, _renameBuffer, EditorStyles.miniTextField);
                if (newName != _renameBuffer) _renameBuffer = newName;

                if (ev.type == EventType.KeyDown)
                {
                    if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter)
                    { _onRenameCommit?.Invoke(_renameBuffer); ev.Use(); }
                    else if (ev.keyCode == KeyCode.Escape)
                    { _onRenameCancel?.Invoke(); ev.Use(); }
                }

                if (GUI.GetNameOfFocusedControl() != "BetterTabsRenameField"
                    && ev.type == EventType.Repaint && _renamingPath != null)
                    _onRenameCommit?.Invoke(_renameBuffer);

                return expanded;
            }

            // ── Normal foldout ────────────────────────────────────────────────
            var icon = EditorGUIUtility.IconContent("Folder Icon").image as Texture2D;

            var foldoutRect = new Rect(rect.x + indent, rect.y, rect.width - indent - 4, rect.height);
            var content = new GUIContent("  " + Path.GetFileName(folderPath), icon);
            bool result = EditorGUI.Foldout(foldoutRect, expanded, content, true, EditorStyles.foldout);

            // ── Right-click context menu ───────────────────────────────────────
            if (ev.type == EventType.ContextClick && rect.Contains(ev.mousePosition))
            {
                BetterTabsInteractionHandler.BuildFolderContextMenu(
                    folderPath, _onRenameRequested, _onAssetChanged, _onRefreshRequested)
                    .ShowAsContext();
                ev.Use();
            }

            return result;
        }

        // ── Asset row ────────────────────────────────────────────────────────

        void DrawAssetRow(string path, int depth, float contentWidth,
            System.Action<string> onDragged)
        {
            var rect = GUILayoutUtility.GetRect(contentWidth, RowHeight);
            float indent = depth * IndentWidth + 2;

            var ev = Event.current;
            bool isSelected = path == _selectedAssetPath;
            bool isRenaming = path == _renamingPath;

            // ── Background ───────────────────────────────────────────────────
            if (isSelected)
                EditorGUI.DrawRect(rect, new Color(0.17f, 0.36f, 0.53f, 1f));
            else
            {
                int row = Mathf.RoundToInt(rect.y / RowHeight);
                if (row % 2 == 0)
                    EditorGUI.DrawRect(rect, new Color(0, 0, 0, 0.04f));
            }

            // ── Mouse events ─────────────────────────────────────────────────
            if (ev.type == EventType.MouseDown && rect.Contains(ev.mousePosition) && ev.button == 0)
            {
                if (ev.clickCount == 2) { BetterTabsInteractionHandler.OpenAsset(path); ev.Use(); }
                else { _onSelected?.Invoke(path); ev.Use(); }
            }

            if (ev.type == EventType.ContextClick && rect.Contains(ev.mousePosition))
            {
                BetterTabsInteractionHandler.BuildContextMenu(
                    path, _onRenameRequested, _onAssetChanged, _onRefreshRequested)
                    .ShowAsContext();
                ev.Use();
            }

            if (ev.type == EventType.MouseDrag && rect.Contains(ev.mousePosition))
            { onDragged?.Invoke(path); ev.Use(); }

            if ((ev.type == EventType.DragUpdated || ev.type == EventType.DragPerform)
                && rect.Contains(ev.mousePosition))
                HandleIncomingDrop(path, ev);

            // ── Icon ─────────────────────────────────────────────────────────
            var icon = AssetDatabase.GetCachedIcon(path) as Texture2D;
            if (icon != null)
                GUI.DrawTexture(new Rect(rect.x + indent, rect.y + 2, 16, 16), icon, ScaleMode.ScaleToFit);

            // ── Name / rename field ──────────────────────────────────────────
            var nameArea = new Rect(rect.x + indent + 20, rect.y, contentWidth - indent - 120, rect.height);

            if (isRenaming)
            {
                GUI.SetNextControlName("BetterTabsRenameField");
                string newName = EditorGUI.TextField(nameArea, _renameBuffer,
                    isSelected ? SelectedTextField() : EditorStyles.miniTextField);
                if (newName != _renameBuffer) _renameBuffer = newName;

                if (ev.type == EventType.KeyDown)
                {
                    if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter)
                    { _onRenameCommit?.Invoke(_renameBuffer); ev.Use(); }
                    else if (ev.keyCode == KeyCode.Escape)
                    { _onRenameCancel?.Invoke(); ev.Use(); }
                }

                if (GUI.GetNameOfFocusedControl() != "BetterTabsRenameField"
                    && ev.type == EventType.Repaint && _renamingPath != null)
                    _onRenameCommit?.Invoke(_renameBuffer);
            }
            else
            {
                var labelStyle = new GUIStyle(EditorStyles.miniLabel);
                if (isSelected) labelStyle.normal.textColor = Color.white;
                GUI.Label(nameArea, Path.GetFileNameWithoutExtension(path), labelStyle);
            }

            // ── Type label ───────────────────────────────────────────────────
            var typeStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = isSelected ? new Color(0.8f, 0.8f, 0.8f) : new Color(0.5f, 0.5f, 0.5f) }
            };
            GUI.Label(new Rect(rect.xMax - 100, rect.y, 96, rect.height),
                AssetDatabase.GetMainAssetTypeAtPath(path)?.Name ?? "", typeStyle);
        }

        // ── Keyboard shortcuts ───────────────────────────────────────────────

        void HandleKeyboardForSelection()
        {
            if (string.IsNullOrEmpty(_selectedAssetPath)) return;
            if (_renamingPath != null) return;

            var ev = Event.current;
            if (ev.type != EventType.KeyDown) return;

            if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter)
            { BetterTabsInteractionHandler.OpenAsset(_selectedAssetPath); ev.Use(); }
            else if (ev.keyCode == KeyCode.F2)
            { _onRenameRequested?.Invoke(_selectedAssetPath); ev.Use(); }
        }

        // ── Incoming drag-drop ───────────────────────────────────────────────

        void HandleIncomingDrop(string rowPath, Event ev)
        {
            if (DragAndDrop.paths == null || DragAndDrop.paths.Length == 0) return;
            string rowFolder = Path.GetDirectoryName(rowPath)?.Replace('\\', '/');

            if (ev.type == EventType.DragUpdated)
            { DragAndDrop.visualMode = DragAndDropVisualMode.Copy; ev.Use(); }
            else if (ev.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (var droppedPath in DragAndDrop.paths)
                {
                    string droppedParent = Path.GetDirectoryName(droppedPath)?.Replace('\\', '/');
                    if (droppedParent == rowFolder)
                        _onSelected?.Invoke(droppedPath);
                    else
                        ShowMoveOrCopyMenu(droppedPath, rowFolder);
                }
                ev.Use();
            }
        }

        void ShowMoveOrCopyMenu(string srcPath, string destFolder)
        {
            var menu = new GenericMenu();
            string fileName = Path.GetFileName(srcPath);
            string destPath = $"{destFolder}/{fileName}";

            menu.AddItem(new GUIContent("Move here"), false, () =>
            {
                string err = AssetDatabase.MoveAsset(srcPath, destPath);
                if (!string.IsNullOrEmpty(err))
                    Debug.LogError($"BetterTabs: Move failed — {err}");
                AssetDatabase.Refresh();
            });
            menu.AddItem(new GUIContent("Copy here"), false, () =>
            {
                AssetDatabase.CopyAsset(srcPath, AssetDatabase.GenerateUniqueAssetPath(destPath));
                AssetDatabase.Refresh();
            });
            menu.ShowAsContext();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        bool IsExpanded(string path) => _expanded.TryGetValue(path, out var v) && v;

        void EnsureCached(string folderPath)
        {
            if (_cache.ContainsKey(folderPath)) return;

            var files = new List<string>();
            var folders = new List<string>();

            var guids = AssetDatabase.FindAssets("", new string[] { folderPath });
            foreach (var guid in guids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var parent = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
                if (parent != folderPath) continue;

                if (AssetDatabase.IsValidFolder(assetPath))
                    folders.Add(assetPath);
                else
                    files.Add(assetPath);
            }

            _cache[folderPath] = (files, folders);
        }

        static GUIStyle SelectedTextField()
        {
            var s = new GUIStyle(EditorStyles.miniTextField);
            s.normal.textColor = Color.white;
            return s;
        }
    }
}
