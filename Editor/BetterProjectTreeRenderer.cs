using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BetterTabs
{
    internal class BetterProjectTreeRenderer
    {
        const int RowHeight = 20;
        const int IndentWidth = 12;
        const string Root = "Assets";

        readonly Dictionary<string, (List<string> files, List<string> folders)> _cache
            = new Dictionary<string, (List<string>, List<string>)>();
        readonly Dictionary<string, bool> _expanded = new Dictionary<string, bool>();

        string _highlightedPath;
        string _renamingPath;
        string _renameBuffer;


        System.Action<string> _onItemSelected;
        System.Action<string> _onItemDragged;
        System.Action _onRefresh;

        public void Setup(
            System.Action<string> onItemSelected,
            System.Action<string> onItemDragged,
            System.Action onRefresh)
        {
            _onItemSelected = onItemSelected;
            _onItemDragged = onItemDragged;
            _onRefresh = onRefresh;
        }

        public void SetHighlight(string path)
        {
            _highlightedPath = path;
        }

        public void InvalidateCache() => _cache.Clear();

        public void ExpandToPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            string start = AssetDatabase.IsValidFolder(path)
                ? path
                : Path.GetDirectoryName(path)?.Replace('\\', '/');

            string current = start;
            while (!string.IsNullOrEmpty(current) && current != "." && current.StartsWith("Assets"))
            {
                _expanded[current] = true;
                string parent = Path.GetDirectoryName(current)?.Replace('\\', '/');
                if (string.IsNullOrEmpty(parent) || parent == current) break;
                current = parent;
            }
        }

        public float GetYPositionOf(string targetPath)
        {
            float y = 0f;
            return GetYInFolder(Root, ref y, targetPath) ? y : -1f;
        }

        bool GetYInFolder(string folderPath, ref float y, string target)
        {
            EnsureCached(folderPath);
            var (files, folders) = _cache[folderPath];

            foreach (var sub in folders)
            {
                if (sub == target) return true;
                y += RowHeight;
                if (IsExpanded(sub) && GetYInFolder(sub, ref y, target))
                    return true;
            }

            foreach (var file in files)
            {
                if (file == target) return true;
                y += RowHeight;
            }

            return false;
        }

        public string SaveExpandedState()
        {
            var parts = new List<string>();
            foreach (var kv in _expanded)
                if (kv.Value) parts.Add(kv.Key);
            return string.Join("|", parts);
        }

        public void LoadExpandedState(string serialized)
        {
            _expanded.Clear();
            if (string.IsNullOrEmpty(serialized)) return;
            foreach (var p in serialized.Split('|'))
                if (!string.IsNullOrEmpty(p))
                    _expanded[p] = true;
        }

        public float MeasureHeight() => MeasureFolder(Root, 0);

        public void Draw(float contentWidth)
        {
            DrawFolder(Root, 0, contentWidth);
        }

        // ── Measurement ──────────────────────────────────────────────────────────

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

        // ── Draw ─────────────────────────────────────────────────────────────────

        void DrawFolder(string folderPath, int depth, float contentWidth)
        {
            EnsureCached(folderPath);
            var (files, folders) = _cache[folderPath];

            foreach (var sub in folders)
            {
                bool wasExpanded = IsExpanded(sub);
                bool nowExpanded = DrawFolderRow(sub, depth, contentWidth, wasExpanded);
                if (nowExpanded != wasExpanded)
                    _expanded[sub] = nowExpanded;

                if (nowExpanded)
                    DrawFolder(sub, depth + 1, contentWidth);
            }

            foreach (var file in files)
                DrawFileRow(file, depth, contentWidth);
        }

        bool DrawFolderRow(string folderPath, int depth, float contentWidth, bool expanded)
        {
            var rect = GUILayoutUtility.GetRect(contentWidth, RowHeight);
            float indent = depth * IndentWidth + 2;
            var ev = Event.current;

            bool isHighlighted = folderPath == _highlightedPath;
            bool isRenaming = folderPath == _renamingPath;

            if (isHighlighted)
                EditorGUI.DrawRect(rect, new Color(0.17f, 0.36f, 0.53f, 0.5f));

            // ── Inline rename ─────────────────────────────────────────────────
            if (isRenaming)
            {
                var folderIcon = EditorGUIUtility.IconContent("Folder Icon").image as Texture2D;
                if (folderIcon != null)
                    GUI.DrawTexture(new Rect(rect.x + indent + 2, rect.y + 2, 16, 16), folderIcon, ScaleMode.ScaleToFit);

                GUI.SetNextControlName("BetterProjectRenameField");
                string newName = EditorGUI.TextField(
                    new Rect(rect.x + indent + 22, rect.y + 1, rect.width - indent - 26, rect.height - 2),
                    _renameBuffer, EditorStyles.miniTextField);
                if (newName != _renameBuffer) _renameBuffer = newName;

                if (ev.type == EventType.KeyDown)
                {
                    if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter)
                    { CommitRename(); ev.Use(); }
                    else if (ev.keyCode == KeyCode.Escape)
                    { CancelRename(); ev.Use(); }
                }

                if (GUI.GetNameOfFocusedControl() != "BetterProjectRenameField"
                    && ev.type == EventType.Repaint && _renamingPath != null)
                    CommitRename();

                return expanded;
            }

            // ── Click: select/highlight (no tab creation) ────────────────────
            if (ev.type == EventType.MouseDown && rect.Contains(ev.mousePosition) && ev.button == 0)
                _onItemSelected?.Invoke(folderPath);

            // ── Drag: allow dragging folder to tab bar or other panels ────────
            if (ev.type == EventType.MouseDrag && rect.Contains(ev.mousePosition))
            { _onItemDragged?.Invoke(folderPath); ev.Use(); }

            // ── Foldout ───────────────────────────────────────────────────────
            var icon = EditorGUIUtility.IconContent("Folder Icon").image as Texture2D;
            var foldoutRect = new Rect(rect.x + indent, rect.y, rect.width - indent - 4, rect.height);
            bool result = EditorGUI.Foldout(foldoutRect, expanded,
                new GUIContent("  " + Path.GetFileName(folderPath), icon), true, EditorStyles.foldout);

            // ── Context menu ──────────────────────────────────────────────────
            if (ev.type == EventType.ContextClick && rect.Contains(ev.mousePosition))
            {
                BetterTabsInteractionHandler.BuildFolderContextMenu(
                    folderPath,
                    p => StartRename(p),
                    _ => { InvalidateCache(); _onRefresh?.Invoke(); },
                    () => { InvalidateCache(); _onRefresh?.Invoke(); })
                    .ShowAsContext();
                ev.Use();
            }

            return result;
        }

        void DrawFileRow(string path, int depth, float contentWidth)
        {
            var rect = GUILayoutUtility.GetRect(contentWidth, RowHeight);
            float indent = depth * IndentWidth + 2;
            var ev = Event.current;

            bool isHighlighted = path == _highlightedPath;
            bool isRenaming = path == _renamingPath;

            if (isHighlighted)
                EditorGUI.DrawRect(rect, new Color(0.17f, 0.36f, 0.53f, 0.6f));
            else
            {
                int row = Mathf.RoundToInt(rect.y / RowHeight);
                if (row % 2 == 0)
                    EditorGUI.DrawRect(rect, new Color(0, 0, 0, 0.04f));
            }

            var icon = AssetDatabase.GetCachedIcon(path) as Texture2D;
            if (icon != null)
                GUI.DrawTexture(new Rect(rect.x + indent, rect.y + 2, 16, 16), icon, ScaleMode.ScaleToFit);

            // ── Inline rename ─────────────────────────────────────────────────
            if (isRenaming)
            {
                GUI.SetNextControlName("BetterProjectRenameField");
                string newName = EditorGUI.TextField(
                    new Rect(rect.x + indent + 20, rect.y + 1, rect.width - indent - 24, rect.height - 2),
                    _renameBuffer, EditorStyles.miniTextField);
                if (newName != _renameBuffer) _renameBuffer = newName;

                if (ev.type == EventType.KeyDown)
                {
                    if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter)
                    { CommitRename(); ev.Use(); }
                    else if (ev.keyCode == KeyCode.Escape)
                    { CancelRename(); ev.Use(); }
                }

                if (GUI.GetNameOfFocusedControl() != "BetterProjectRenameField"
                    && ev.type == EventType.Repaint && _renamingPath != null)
                    CommitRename();

                return;
            }

            var labelStyle = new GUIStyle(EditorStyles.miniLabel);
            if (isHighlighted) labelStyle.normal.textColor = Color.white;
            GUI.Label(
                new Rect(rect.x + indent + 20, rect.y, contentWidth - indent - 20, rect.height),
                Path.GetFileNameWithoutExtension(path),
                labelStyle);

            if (ev.type == EventType.MouseDown && rect.Contains(ev.mousePosition) && ev.button == 0)
            {
                if (ev.clickCount == 2) { BetterTabsInteractionHandler.OpenAsset(path); ev.Use(); }
                else { _onItemSelected?.Invoke(path); ev.Use(); }
            }

            if (ev.type == EventType.ContextClick && rect.Contains(ev.mousePosition))
            {
                BetterTabsInteractionHandler.BuildContextMenu(
                    path,
                    p => StartRename(p),
                    _ => { InvalidateCache(); _onRefresh?.Invoke(); },
                    () => { InvalidateCache(); _onRefresh?.Invoke(); })
                    .ShowAsContext();
                ev.Use();
            }

            if (ev.type == EventType.MouseDrag && rect.Contains(ev.mousePosition))
            { _onItemDragged?.Invoke(path); ev.Use(); }
        }

        // ── Inline rename ─────────────────────────────────────────────────────

        void StartRename(string path)
        {
            _renamingPath = path;
            _renameBuffer = Path.GetFileNameWithoutExtension(path);
            _highlightedPath = path;
            GUI.FocusControl("BetterProjectRenameField");
        }

        void CommitRename()
        {
            if (_renamingPath == null) return;
            string path = _renamingPath;
            _renamingPath = null;

            string trimmed = (_renameBuffer ?? "").Trim();
            string oldName = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(trimmed) && trimmed != oldName)
            {
                string err = AssetDatabase.RenameAsset(path, trimmed);
                if (!string.IsNullOrEmpty(err))
                    Debug.LogError($"BetterTabs: Rename failed — {err}");
                AssetDatabase.Refresh();
                InvalidateCache();
                _onRefresh?.Invoke();
            }
            _renameBuffer = null;
        }

        void CancelRename()
        {
            _renamingPath = null;
            _renameBuffer = null;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

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
    }
}
