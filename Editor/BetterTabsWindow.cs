using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace BetterTabs
{
    public class BetterTabsWindow : EditorWindow
    {
        // ── State ────────────────────────────────────────────────────────────
        List<BetterTabEntry> _tabs = new List<BetterTabEntry>();
        int _selectedIndex = -1;

        // ── View toggle ──────────────────────────────────────────────────────
        bool _gridView = false;

        // ── Scroll positions ─────────────────────────────────────────────────
        Vector2 _tabScroll;
        Vector2 _assetScroll;
        bool _scrollToSelected;
        float _tabScrollTarget;
        double _tabScrollTime;

        // ── Folder drag-drop state ───────────────────────────────────────────
        bool _isDragHovering;

        // ── Tab reorder drag state ────────────────────────────────────────────
        int _dragTabIndex = -1;
        int _pressedTabIndex = -1;
        float _dragTabOffsetX;
        float _dragTabCurrentX;

        // ── Closed-tab history (Ctrl+Shift+T) ────────────────────────────────
        readonly Stack<string> _closedTabStack = new Stack<string>();

        // ── Tree & search ─────────────────────────────────────────────────────
        BetterTreeRenderer _tree;
        BetterSearchHandler _search;
        string _searchInputText = "";

        // ── Local asset selection ─────────────────────────────────────────────
        string _selectedAssetPath;

        // ── Inline rename ─────────────────────────────────────────────────────
        string _renamingPath;
        string _renameBuffer;

        // ── Dirty flag (set by postprocessor / projectChanged) ────────────────
        static bool s_dirty;
        static BetterTabsWindow s_instance;

        // ── Constants ────────────────────────────────────────────────────────
        const int TabHeight = 22;
        const int AssetRowHeight = 20;
        const int GridIconSize = 64;
        const int GridCellSize = 80;

        // ── Project panel ─────────────────────────────────────────────────────
        bool _projectPanelOpen;
        float _splitterX = 220f;
        bool _splitterDragging;
        BetterProjectTreeRenderer _projectTree;
        Vector2 _projectScroll;

        // ── Layout bounds (set each frame in OnGUI) ───────────────────────────
        float _rightX;
        float _rightW;

        static string ProjectPanelOpenKey => $"BetterTabs_ProjectPanelOpen_{Application.productName}";
        static string SplitterXKey => $"BetterTabs_SplitterX_{Application.productName}";
        static string ProjectExpandedKey => $"BetterTabs_ProjectExpanded_{Application.productName}";

        // ── Static API (used by postprocessor) ───────────────────────────────

        public static void RequestRefresh()
        {
            s_dirty = true;
            if (s_instance != null) s_instance.Repaint();
        }

        // Returns true if any of the paths belongs to an open tab root.
        public static bool CheckPathsAffectTabs(string[] paths)
        {
            if (s_instance == null || paths == null) return false;
            foreach (var p in paths)
                foreach (var tab in s_instance._tabs)
                    if (p.StartsWith(tab.path))
                        return true;
            return false;
        }

        // ── Menu items ───────────────────────────────────────────────────────
        [MenuItem("Window/BetterTabs/Open BetterTabs")]
        public static void Open()
        {
            var w = GetWindow<BetterTabsWindow>();
            w.Show();
        }

        // Ctrl+T global shortcut — registered via ShortcutManager, not MenuItem,
        // so it does not appear as a visible menu entry.
        [Shortcut("BetterTabs/Add Tab from Selection", KeyCode.T, ShortcutModifiers.Action)]
        static void AddTabFromSelectionShortcut()
        {
            var w = GetWindow<BetterTabsWindow>();
            w.Show();
            w.Focus();
            w.OpenNewTab();
        }

        // ── Lifecycle ────────────────────────────────────────────────────────
        void OnEnable()
        {
            s_instance = this;
            titleContent = new GUIContent("BetterTabs", LoadWindowIcon());
            _search = new BetterSearchHandler();
            _tree = new BetterTreeRenderer(OnFoldoutToggled);

            _projectTree = new BetterProjectTreeRenderer();
            _projectTree.Setup(OnProjectPanelItemClicked, OnAssetDragged, () => { _projectTree.InvalidateCache(); RequestRefresh(); });
            _projectPanelOpen = EditorPrefs.GetBool(ProjectPanelOpenKey, false);
            _splitterX = EditorPrefs.GetFloat(SplitterXKey, 220f);
            _projectTree.LoadExpandedState(EditorPrefs.GetString(ProjectExpandedKey, ""));

            if (BetterTabsPrefs.Load(out var tabs, out var idx))
            {
                _tabs = tabs;
                _selectedIndex = tabs.Count == 0 ? -1 : Mathf.Clamp(idx, 0, tabs.Count - 1);
            }

            RestoreActiveTabState();
            SyncProjectTreeHighlight();

            EditorApplication.projectChanged += OnProjectChanged;
        }

        void OnDisable()
        {
            s_instance = null;
            EditorApplication.projectChanged -= OnProjectChanged;
            PersistActiveTabState();
            BetterTabsPrefs.Save(_tabs, _selectedIndex);
            EditorPrefs.SetBool(ProjectPanelOpenKey, _projectPanelOpen);
            EditorPrefs.SetFloat(SplitterXKey, _splitterX);
            if (_projectTree != null)
                EditorPrefs.SetString(ProjectExpandedKey, _projectTree.SaveExpandedState());
        }

        void OnProjectChanged()
        {
            RequestRefresh();
        }

        // ── GUI ──────────────────────────────────────────────────────────────
        void OnGUI()
        {
            if (s_dirty)
            {
                s_dirty = false;
                _tree.InvalidateCache();
                _projectTree?.InvalidateCache();
                Repaint();
            }

            if (Event.current.type == EventType.MouseLeaveWindow)
            {
                _dragTabIndex = -1;
                _pressedTabIndex = -1;
                if (_splitterDragging) _splitterDragging = false;
            }

            HandleKeyboardShortcuts();
            HandleDragEvents();

            DrawTabBar();

            // Layout bounds for the right panel
            _rightX = _projectPanelOpen ? _splitterX + 4 : 0;
            _rightW = position.width - _rightX;

            // Left project panel
            if (_projectPanelOpen)
            {
                DrawProjectPanel(new Rect(0, TabHeight, _splitterX, position.height - TabHeight));
                DrawSplitter();
            }

            // Right panel content
            if (_tabs.Count == 0)
            {
                DrawEmptyState();
            }
            else
            {
                if (ActiveTabIsFolder()) DrawSearchToolbar();
                DrawContent();
            }

            if (_isDragHovering)
                DrawDropOverlay();

            if (_searchInputText != _search.CommittedQuery)
                Repaint();
        }

        // ── Empty state ──────────────────────────────────────────────────────
        void DrawEmptyState()
        {
            var r = new Rect(_rightX, 0, _rightW, position.height);

            if (_isDragHovering)
            {
                EditorGUI.DrawRect(r, new Color(0.2f, 0.5f, 1f, 0.15f));
                DrawBorder(r, new Color(0.3f, 0.6f, 1f, 0.8f), 2f);
            }

            var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                fontSize = 14,
                normal = { textColor = _isDragHovering ? new Color(0.5f, 0.8f, 1f) : Color.gray }
            };
            GUI.Label(r, "⊕ Drag a folder or asset here", style);
        }

        // ── Tab bar ──────────────────────────────────────────────────────────
        void DrawTabBar()
        {
            const float rightReserved = 54f;
            const float arrowW = 16f;
            float fullBarWidth = position.width - rightReserved;
            GUI.Box(new Rect(0, 0, position.width, TabHeight), GUIContent.none, EditorStyles.toolbar);

            int count = _tabs.Count;
            var widths = new float[count];
            var starts = new float[count];
            float total = 0f;
            for (int i = 0; i < count; i++)
            {
                float lw = EditorStyles.toolbarButton.CalcSize(new GUIContent(_tabs[i].name)).x;
                widths[i] = Mathf.Clamp(lw + 40, 80, 180);
                starts[i] = total;
                total += widths[i];
            }

            bool hasOverflow = total > fullBarWidth;
            float scrollOffsetX = hasOverflow ? arrowW : 0f;
            float barWidth = fullBarWidth - (hasOverflow ? arrowW * 2f : 0f);
            var barRect = new Rect(scrollOffsetX, 0, barWidth, TabHeight);

            // Auto-scroll to keep selected tab in view
            if (_scrollToSelected && _selectedIndex >= 0 && _selectedIndex < count)
            {
                _scrollToSelected = false;
                float tabStart = starts[_selectedIndex];
                float tabEnd   = tabStart + widths[_selectedIndex];
                float target   = _tabScrollTarget;
                if (tabStart < target)
                    target = tabStart;
                else if (tabEnd > target + barWidth)
                    target = tabEnd - barWidth;
                _tabScrollTarget = Mathf.Clamp(target, 0f, Mathf.Max(0f, total - barWidth));
            }

            // Clamp target in case the window was resized
            _tabScrollTarget = Mathf.Clamp(_tabScrollTarget, 0f, Mathf.Max(0f, total - barWidth));

            // Smooth scroll animation
            {
                double now = EditorApplication.timeSinceStartup;
                float dt = Mathf.Clamp((float)(now - _tabScrollTime), 0f, 0.05f);
                _tabScrollTime = now;
                _tabScroll.x = Mathf.Lerp(_tabScroll.x, _tabScrollTarget, dt * 14f);
                if (Mathf.Abs(_tabScroll.x - _tabScrollTarget) < 0.5f)
                    _tabScroll.x = _tabScrollTarget;
                else
                    Repaint();
            }

            _tabScroll = GUI.BeginScrollView(barRect, _tabScroll,
                new Rect(0, 0, total, TabHeight),
                false, false, GUIStyle.none, GUIStyle.none);

            var ev = Event.current;
            int toRemove = -1;

            if (ev.type == EventType.MouseDown && ev.button == 0)
            {
                for (int i = 0; i < count; i++)
                {
                    var tr = new Rect(starts[i], 0, widths[i], TabHeight);
                    if (!tr.Contains(ev.mousePosition)) continue;
                    var cr = new Rect(starts[i] + widths[i] - 16, 3, 14, 14);
                    if (cr.Contains(ev.mousePosition)) break;
                    _pressedTabIndex = i;
                    _dragTabOffsetX = ev.mousePosition.x - starts[i];
                    _dragTabCurrentX = starts[i];
                    ev.Use();
                    break;
                }
            }
            else if (ev.type == EventType.MouseDrag && _pressedTabIndex >= 0)
            {
                if (_dragTabIndex < 0) _dragTabIndex = _pressedTabIndex;
                _dragTabCurrentX = Mathf.Clamp(
                    ev.mousePosition.x - _dragTabOffsetX,
                    0f, total - widths[_dragTabIndex]);
                CheckTabSwap(starts, widths);
                ev.Use();
                Repaint();
            }
            else if (ev.type == EventType.MouseUp && ev.button == 0)
            {
                bool acted = false;
                if (_dragTabIndex >= 0) { CommitTabDrag(); acted = true; }
                else if (_pressedTabIndex >= 0) { SelectTab(_pressedTabIndex); acted = true; }
                _dragTabIndex = -1;
                _pressedTabIndex = -1;
                if (acted) ev.Use();
            }

            for (int i = 0; i < count; i++)
            {
                if (i == _dragTabIndex) continue;

                var tabIcon = GetTabIcon(_tabs[i].path);
                DrawTabAt(i, starts[i], widths[i], tabIcon, active: i == _selectedIndex, ghost: false);

                if (ev.type == EventType.ContextClick
                    && new Rect(starts[i], 0, widths[i], TabHeight).Contains(ev.mousePosition))
                {
                    ShowTabContextMenu(i);
                    ev.Use();
                }

                var closeRect = new Rect(starts[i] + widths[i] - 16, 3, 14, 14);
                if (GUI.Button(closeRect, "×", EditorStyles.miniLabel))
                    toRemove = i;
            }

            if (_dragTabIndex >= 0)
                EditorGUI.DrawRect(
                    new Rect(starts[_dragTabIndex], 0, widths[_dragTabIndex], TabHeight),
                    new Color(0.3f, 0.6f, 1f, 0.18f));

            if (_dragTabIndex >= 0 && _dragTabIndex < count)
            {
                DrawTabAt(_dragTabIndex, _dragTabCurrentX, widths[_dragTabIndex],
                    GetTabIcon(_tabs[_dragTabIndex].path), active: _dragTabIndex == _selectedIndex, ghost: true);
            }

            GUI.EndScrollView();

            // ── Overflow arrows ───────────────────────────────────────────────
            if (hasOverflow)
            {
                bool canLeft  = _tabScrollTarget > 0.5f;
                bool canRight = _tabScrollTarget < total - barWidth - 0.5f;

                using (new EditorGUI.DisabledScope(!canLeft))
                {
                    if (GUI.Button(new Rect(0, 0, arrowW, TabHeight), "‹", EditorStyles.toolbarButton))
                        _tabScrollTarget = Mathf.Max(0f, _tabScrollTarget - 80f);
                }
                using (new EditorGUI.DisabledScope(!canRight))
                {
                    if (GUI.Button(new Rect(scrollOffsetX + barWidth, 0, arrowW, TabHeight), "›", EditorStyles.toolbarButton))
                        _tabScrollTarget = Mathf.Min(total - barWidth, _tabScrollTarget + 80f);
                }
            }

            // ── + button — adds selected folder if not already open ───────────
            string selPath = Selection.activeObject != null
                ? AssetDatabase.GetAssetPath(Selection.activeObject) : null;
            bool canAdd = !string.IsNullOrEmpty(selPath)
                && !_tabs.Any(t => t.path == selPath);

            var plusRect = new Rect(position.width - rightReserved, 1, 24, TabHeight - 2);
            using (new EditorGUI.DisabledScope(!canAdd))
            {
                if (GUI.Button(plusRect, new GUIContent("+"), EditorStyles.toolbarButton))
                    AddOrSelectTab(selPath);
            }

            // ── Project panel toggle ──────────────────────────────────────────
            var panelIcon = EditorGUIUtility.FindTexture("d_Project");
            var panelContent = panelIcon != null
                ? new GUIContent(panelIcon, _projectPanelOpen ? "Hide Project Panel" : "Show Project Panel")
                : new GUIContent(_projectPanelOpen ? "◁" : "▷", _projectPanelOpen ? "Hide Project Panel" : "Show Project Panel");

            var panelBtnRect = new Rect(position.width - 28, 1, 26, TabHeight - 2);
            if (GUI.Button(panelBtnRect, panelContent, EditorStyles.toolbarButton))
            {
                _projectPanelOpen = !_projectPanelOpen;
                if (_projectPanelOpen)
                {
                    _projectTree.InvalidateCache();
                    SyncProjectTreeHighlight();
                }
                EditorPrefs.SetBool(ProjectPanelOpenKey, _projectPanelOpen);
            }

            if (toRemove >= 0)
                RemoveTab(toRemove);
        }

        void DrawTabAt(int index, float x, float width, Texture2D icon, bool active, bool ghost)
        {
            var drawRect = new Rect(x, 0, width, TabHeight);

            Color bg = ghost
                ? new Color(0.2f, 0.45f, 0.8f, 0.9f)
                : active
                    ? new Color(0.25f, 0.47f, 0.75f, 0.5f)
                    : Color.clear;

            if (bg.a > 0)
                EditorGUI.DrawRect(drawRect, bg);

            if (icon != null)
                GUI.DrawTexture(new Rect(x + 2, 2, 16, 16), icon, ScaleMode.ScaleToFit);

            var labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                clipping = TextClipping.Clip,
                fontStyle = active ? FontStyle.Bold : FontStyle.Normal
            };
            if (ghost) labelStyle.normal.textColor = Color.white;

            GUI.Label(new Rect(x + 20, 2, width - 38, TabHeight - 4), _tabs[index].name, labelStyle);
        }

        // ── Tab reorder helpers ───────────────────────────────────────────────

        void CheckTabSwap(float[] starts, float[] widths)
        {
            if (_dragTabIndex < 0) return;

            if (_dragTabIndex > 0)
            {
                int left = _dragTabIndex - 1;
                if (_dragTabCurrentX < starts[left] + widths[left] * 0.5f)
                {
                    SwapTabs(_dragTabIndex, left);
                    _dragTabIndex--;
                    return;
                }
            }

            if (_dragTabIndex < _tabs.Count - 1)
            {
                int right = _dragTabIndex + 1;
                if (_dragTabCurrentX + widths[_dragTabIndex] > starts[right] + widths[right] * 0.5f)
                {
                    SwapTabs(_dragTabIndex, right);
                    _dragTabIndex++;
                }
            }
        }

        void SwapTabs(int i, int j)
        {
            (_tabs[i], _tabs[j]) = (_tabs[j], _tabs[i]);
            if (_selectedIndex == i) _selectedIndex = j;
            else if (_selectedIndex == j) _selectedIndex = i;
        }

        void CommitTabDrag()
        {
            BetterTabsPrefs.Save(_tabs, _selectedIndex);
            Repaint();
        }

        void ShowTabContextMenu(int index)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Close"), false, () => RemoveTab(index));
            menu.AddItem(new GUIContent("Close Others"), false, () => CloseOthers(index));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Open in Project"), false, () => PingTab(index));
            menu.ShowAsContext();
        }

        // ── Search toolbar ────────────────────────────────────────────────────
        void DrawSearchToolbar()
        {
            float y = TabHeight;
            var toolbarRect = new Rect(_rightX, y, _rightW, TabHeight);
            GUI.Box(toolbarRect, GUIContent.none, EditorStyles.toolbar);

            float clearBtnW = _searchInputText.Length > 0 ? 20 : 0;
            float viewBtnW = 26;
            float fieldW = _rightW - clearBtnW - viewBtnW - 6;

            var searchRect = new Rect(_rightX + 2, y + 2, fieldW, TabHeight - 4);

            EditorGUI.BeginChangeCheck();
            _searchInputText = EditorGUI.TextField(searchRect, _searchInputText, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck())
            {
                bool changed = _search.Tick(_searchInputText, ActiveTabPath());
                if (changed) SyncSearchToTab();
            }
            else
            {
                bool changed = _search.Tick(_searchInputText, ActiveTabPath());
                if (changed) SyncSearchToTab();
            }

            if (clearBtnW > 0)
            {
                var clearRect = new Rect(_rightX + 2 + fieldW, y + 2, clearBtnW - 2, TabHeight - 4);
                if (GUI.Button(clearRect, "×", EditorStyles.toolbarButton))
                    ClearSearch();
            }

            var toggleRect = new Rect(_rightX + _rightW - viewBtnW - 2, y + 1, viewBtnW, TabHeight - 2);
            var viewIcon = _gridView
                ? EditorGUIUtility.FindTexture("UnityEditor.SceneView")
                : EditorGUIUtility.FindTexture("d_GridLayoutGroup Icon");

            var viewContent = viewIcon != null
                ? new GUIContent(viewIcon, _gridView ? "List view" : "Grid view")
                : new GUIContent(_gridView ? "≡" : "⊞", _gridView ? "List view" : "Grid view");

            if (GUI.Button(toggleRect, viewContent, EditorStyles.toolbarButton))
                _gridView = !_gridView;
        }

        // ── Content area ──────────────────────────────────────────────────────
        void DrawContent()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _tabs.Count)
            {
                var emptyRect = new Rect(_rightX, TabHeight, _rightW, position.height - TabHeight);
                GUI.Label(emptyRect, "No tab selected.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            string rootPath = ActiveTabPath();

            if (!ActiveTabIsFolder())
            {
                var pinRect = new Rect(_rightX, TabHeight, _rightW, position.height - TabHeight);
                DrawAssetPinView(pinRect, rootPath);
                return;
            }

            float topOffset = TabHeight * 2;
            var listRect = new Rect(_rightX, topOffset, _rightW, position.height - topOffset);

            if (_search.IsSearching)
            {
                DrawSearchResults(listRect);
                return;
            }

            if (_gridView)
                DrawGridView(listRect, rootPath);
            else
                DrawTreeView(listRect, rootPath);
        }

        // ── Asset pin view ────────────────────────────────────────────────────
        void DrawAssetPinView(Rect r, string path)
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);

            var preview = obj != null ? AssetPreview.GetAssetPreview(obj) : null;
            if (preview == null) preview = AssetDatabase.GetCachedIcon(path) as Texture2D;

            float previewSize = Mathf.Min(r.width * 0.4f, r.height * 0.4f, 128f);
            float cx = r.x + r.width * 0.5f;
            float cy = r.y + r.height * 0.5f - 24f;

            if (preview != null)
                GUI.DrawTexture(
                    new Rect(cx - previewSize * 0.5f, cy - previewSize * 0.5f, previewSize, previewSize),
                    preview, ScaleMode.ScaleToFit);

            var nameStyle = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(r.x, cy + previewSize * 0.5f + 6f, r.width, 20f),
                Path.GetFileNameWithoutExtension(path), nameStyle);

            var typeStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.55f, 0.55f, 0.55f) }
            };
            GUI.Label(new Rect(r.x, cy + previewSize * 0.5f + 26f, r.width, 16f),
                AssetDatabase.GetMainAssetTypeAtPath(path)?.Name ?? "", typeStyle);

            float btnY = cy + previewSize * 0.5f + 50f;
            float btnW = 120f;
            float btnX = cx - btnW * 1.5f - 4f;

            if (GUI.Button(new Rect(btnX, btnY, btnW, 22f), "Open"))
                BetterTabsInteractionHandler.OpenAsset(path);

            if (GUI.Button(new Rect(btnX + btnW + 4f, btnY, btnW, 22f), "Show in Project"))
                BetterTabsInteractionHandler.ShowInProject(path);

            if (GUI.Button(new Rect(btnX + (btnW + 4f) * 2f, btnY, btnW, 22f), "Reveal in Explorer"))
                BetterTabsInteractionHandler.RevealInExplorer(path);
        }

        // ── Tree view ─────────────────────────────────────────────────────────
        void DrawTreeView(Rect listRect, string rootPath)
        {
            _tree.SetCallbacks(
                _selectedAssetPath,
                _renamingPath,
                _renameBuffer,
                OnAssetSelected,
                StartRename,
                CommitRename,
                CancelRename,
                OnAssetModified,
                BetterTabsWindow.RequestRefresh);

            float contentHeight = _tree.MeasureHeight(rootPath);
            var contentRect = new Rect(0, 0, listRect.width - 16, Mathf.Max(contentHeight, listRect.height));

            _assetScroll = GUI.BeginScrollView(listRect, _assetScroll, contentRect);

            GUILayout.BeginArea(new Rect(0, 0, contentRect.width, contentHeight));
            _tree.Draw(rootPath, contentRect.width, OnAssetDragged);
            GUILayout.EndArea();

            GUI.EndScrollView();
        }

        // ── Search results ────────────────────────────────────────────────────
        void DrawSearchResults(Rect listRect)
        {
            var results = _search.Results;
            float headerH = EditorGUIUtility.singleLineHeight + 4;

            var countRect = new Rect(listRect.x + 4, listRect.y, listRect.width - 8, headerH);
            var countStyle = new GUIStyle(EditorStyles.miniLabel)
                { normal = { textColor = new Color(0.6f, 0.6f, 0.6f) } };
            GUI.Label(countRect,
                $"{results.Count} result{(results.Count == 1 ? "" : "s")}", countStyle);

            var scrollArea = new Rect(listRect.x, listRect.y + headerH, listRect.width, listRect.height - headerH);
            float totalH = results.Count * AssetRowHeight;
            var contentRect = new Rect(0, 0, scrollArea.width - 16, totalH);

            _assetScroll = GUI.BeginScrollView(scrollArea, _assetScroll, contentRect);

            for (int i = 0; i < results.Count; i++)
            {
                var path = results[i];
                var rowRect = new Rect(0, i * AssetRowHeight, contentRect.width, AssetRowHeight);

                bool isSelected = path == _selectedAssetPath;
                if (isSelected)
                    EditorGUI.DrawRect(rowRect, new Color(0.17f, 0.36f, 0.53f, 1f));
                else if (i % 2 == 0)
                    EditorGUI.DrawRect(rowRect, new Color(0, 0, 0, 0.04f));

                DrawSearchResultRow(rowRect, path, isSelected);
            }

            GUI.EndScrollView();
        }

        void DrawSearchResultRow(Rect rowRect, string path, bool isSelected)
        {
            var ev = Event.current;

            if (ev.type == EventType.MouseDown && rowRect.Contains(ev.mousePosition) && ev.button == 0)
            {
                if (ev.clickCount == 2) { BetterTabsInteractionHandler.OpenAsset(path); ev.Use(); }
                else { OnAssetSelected(path); ev.Use(); }
            }
            if (ev.type == EventType.ContextClick && rowRect.Contains(ev.mousePosition))
            {
                BetterTabsInteractionHandler.BuildContextMenu(path, StartRename, OnAssetModified, RequestRefresh)
                    .ShowAsContext();
                ev.Use();
            }
            if (ev.type == EventType.MouseDrag && rowRect.Contains(ev.mousePosition))
            { OnAssetDragged(path); ev.Use(); }

            var icon = AssetDatabase.GetCachedIcon(path) as Texture2D;
            if (icon != null)
                GUI.DrawTexture(new Rect(rowRect.x + 2, rowRect.y + 2, 16, 16), icon, ScaleMode.ScaleToFit);

            var richStyle = new GUIStyle(EditorStyles.miniLabel) { richText = true };
            if (isSelected) richStyle.normal.textColor = Color.white;
            GUI.Label(new Rect(rowRect.x + 22, rowRect.y, rowRect.width - 120, rowRect.height),
                _search.Highlight(path), richStyle);

            var typeStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = isSelected ? new Color(0.8f, 0.8f, 0.8f) : new Color(0.5f, 0.5f, 0.5f) }
            };
            GUI.Label(new Rect(rowRect.xMax - 100, rowRect.y, 96, rowRect.height),
                AssetDatabase.GetMainAssetTypeAtPath(path)?.Name ?? "", typeStyle);
        }

        // ── Grid view ─────────────────────────────────────────────────────────
        void DrawGridView(Rect listRect, string rootPath)
        {
            var guids = AssetDatabase.FindAssets("", new string[] { rootPath });
            var folders = new List<string>();
            var files   = new List<string>();
            foreach (var guid in guids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var parent = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
                if (parent != rootPath) continue;
                if (AssetDatabase.IsValidFolder(assetPath)) folders.Add(assetPath);
                else                                        files.Add(assetPath);
            }
            var assets = new List<string>(folders.Count + files.Count);
            assets.AddRange(folders);
            assets.AddRange(files);

            int cols = Mathf.Max(1, (int)(listRect.width / GridCellSize));
            int rows = Mathf.CeilToInt((float)assets.Count / cols);
            float totalH = rows * GridCellSize;
            var contentRect = new Rect(0, 0, listRect.width - 16, totalH);

            _assetScroll = GUI.BeginScrollView(listRect, _assetScroll, contentRect);

            for (int i = 0; i < assets.Count; i++)
            {
                int col = i % cols;
                int row = i / cols;
                DrawGridCell(new Rect(col * GridCellSize, row * GridCellSize, GridCellSize, GridCellSize), assets[i]);
            }

            GUI.EndScrollView();
        }

        void DrawGridCell(Rect cellRect, string path)
        {
            bool isFolder = AssetDatabase.IsValidFolder(path);

            Texture2D icon;
            if (isFolder)
                icon = EditorGUIUtility.FindTexture("Folder Icon");
            else
            {
                icon = AssetPreview.GetAssetPreview(AssetDatabase.LoadAssetAtPath<Object>(path));
                if (icon == null) icon = AssetDatabase.GetCachedIcon(path) as Texture2D;
            }

            var ev = Event.current;

            bool isSelected = path == _selectedAssetPath;
            if (isSelected)
                EditorGUI.DrawRect(cellRect, new Color(0.17f, 0.36f, 0.53f, 0.6f));

            if (ev.type == EventType.MouseDown && cellRect.Contains(ev.mousePosition) && ev.button == 0)
            {
                if (ev.clickCount == 2)
                {
                    if (isFolder) AddOrSelectTab(path);
                    else BetterTabsInteractionHandler.OpenAsset(path);
                    ev.Use();
                }
                else { OnAssetSelected(path); ev.Use(); }
            }
            if (ev.type == EventType.ContextClick && cellRect.Contains(ev.mousePosition))
            {
                var menu = isFolder
                    ? BetterTabsInteractionHandler.BuildFolderContextMenu(path, StartRename, OnAssetModified, RequestRefresh)
                    : BetterTabsInteractionHandler.BuildContextMenu(path, StartRename, OnAssetModified, RequestRefresh);
                menu.ShowAsContext();
                ev.Use();
            }
            if (ev.type == EventType.MouseDrag && cellRect.Contains(ev.mousePosition))
            { OnAssetDragged(path); ev.Use(); }

            var iconRect = new Rect(cellRect.x + (GridCellSize - GridIconSize) / 2, cellRect.y + 4, GridIconSize, GridIconSize);
            if (icon != null)
                GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit);

            var labelRect = new Rect(cellRect.x + 2, cellRect.y + GridCellSize - 18, GridCellSize - 4, 16);
            var labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                clipping = TextClipping.Clip,
                normal = { textColor = isSelected ? Color.white : EditorStyles.miniLabel.normal.textColor }
            };
            GUI.Label(labelRect, TruncateWithEllipsis(Path.GetFileName(path), labelStyle, labelRect.width), labelStyle);
        }

        static string TruncateWithEllipsis(string text, GUIStyle style, float maxWidth)
        {
            if (style.CalcSize(new GUIContent(text)).x <= maxWidth) return text;
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (style.CalcSize(new GUIContent(text[..mid] + "…")).x <= maxWidth) lo = mid;
                else hi = mid - 1;
            }
            return lo == 0 ? "…" : text[..lo] + "…";
        }

        // ── Project panel ─────────────────────────────────────────────────────
        void DrawProjectPanel(Rect r)
        {
            // Background
            EditorGUI.DrawRect(r, EditorGUIUtility.isProSkin
                ? new Color(0.19f, 0.19f, 0.19f, 1f)
                : new Color(0.76f, 0.76f, 0.76f, 1f));

            // Scroll content
            var contentArea = new Rect(r.x, r.y, r.width, r.height);
            float contentH = _projectTree.MeasureHeight();
            float panelW = contentArea.width;
            var contentRect = new Rect(0, 0, panelW - 16, Mathf.Max(contentH, contentArea.height));

            _projectScroll = GUI.BeginScrollView(contentArea, _projectScroll, contentRect);
            GUILayout.BeginArea(new Rect(0, 0, contentRect.width, Mathf.Max(contentH, 1f)));
            _projectTree.Draw(contentRect.width);
            GUILayout.EndArea();
            GUI.EndScrollView();
        }

        void DrawSplitter()
        {
            var splitterRect = new Rect(_splitterX, TabHeight, 4, position.height - TabHeight);
            EditorGUI.DrawRect(splitterRect, new Color(0.1f, 0.1f, 0.1f, 1f));
            EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeHorizontal);

            var ev = Event.current;
            if (ev.type == EventType.MouseDown && splitterRect.Contains(ev.mousePosition) && ev.button == 0)
            {
                _splitterDragging = true;
                ev.Use();
            }

            if (_splitterDragging)
            {
                if (ev.type == EventType.MouseDrag)
                {
                    _splitterX = Mathf.Clamp(ev.mousePosition.x, 100f, position.width - 200f);
                    _rightX = _splitterX + 4;
                    _rightW = position.width - _rightX;
                    ev.Use();
                    Repaint();
                }
                if (ev.type == EventType.MouseUp)
                {
                    _splitterDragging = false;
                    EditorPrefs.SetFloat(SplitterXKey, _splitterX);
                    ev.Use();
                }
            }
        }

        void SyncProjectTreeHighlight()
        {
            if (_projectTree == null) return;
            string path = !string.IsNullOrEmpty(_selectedAssetPath) ? _selectedAssetPath : ActiveTabPath();
            if (string.IsNullOrEmpty(path)) return;
            _projectTree.SetHighlight(path);
            _projectTree.ExpandToPath(path);
            ScrollProjectPanelToPath(path);
        }

        void ScrollProjectPanelToPath(string path)
        {
            if (!_projectPanelOpen || _projectTree == null) return;
            float y = _projectTree.GetYPositionOf(path);
            if (y < 0f) return;
            float panelH = position.height - TabHeight * 2f;
            if (y < _projectScroll.y || y + 20f > _projectScroll.y + panelH)
                _projectScroll.y = Mathf.Max(0f, y - panelH * 0.35f);
        }

        // ── Folder drag-drop handling ─────────────────────────────────────────
        void HandleDragEvents()
        {
            var ev = Event.current;
            var windowRect = new Rect(0, 0, position.width, position.height);

            if (ev.type == EventType.DragUpdated && windowRect.Contains(ev.mousePosition))
            {
                if (IsDraggingAsset())
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    _isDragHovering = true;
                    ev.Use();
                    Repaint();
                }
            }
            else if (ev.type == EventType.DragPerform && windowRect.Contains(ev.mousePosition))
            {
                if (IsDraggingAsset())
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var path in DragAndDrop.paths)
                        if (!string.IsNullOrEmpty(path))
                            AddOrSelectTab(path);
                    _isDragHovering = false;
                    ev.Use();
                    Repaint();
                }
            }
            else if (ev.type == EventType.DragExited)
            {
                _isDragHovering = false;
                Repaint();
            }
        }

        bool IsDraggingAsset()
        {
            return DragAndDrop.paths != null && DragAndDrop.paths.Length > 0;
        }

        void DrawDropOverlay()
        {
            var r = new Rect(_rightX, 0, _rightW, position.height);
            EditorGUI.DrawRect(r, new Color(0.2f, 0.5f, 1f, 0.08f));
            DrawBorder(r, new Color(0.3f, 0.6f, 1f, 0.8f), 2f);
        }

        static void DrawBorder(Rect r, Color color, float thickness)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, thickness), color);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - thickness, r.width, thickness), color);
            EditorGUI.DrawRect(new Rect(r.x, r.y, thickness, r.height), color);
            EditorGUI.DrawRect(new Rect(r.xMax - thickness, r.y, thickness, r.height), color);
        }

        // ── Asset selection / interaction callbacks ────────────────────────────

        void OnProjectPanelItemClicked(string path)
        {
            _selectedAssetPath = path;
            _projectTree.SetHighlight(path);
            string capturedPath = path;
            EditorApplication.delayCall += () =>
            {
                Object obj = AssetDatabase.LoadAssetAtPath<Object>(capturedPath);
                if (obj != null) Selection.activeObject = obj;
            };
            Repaint();
        }

        void OnAssetSelected(string path)
        {
            _selectedAssetPath = path;
            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (obj != null) Selection.activeObject = obj;
            if (_projectPanelOpen)
            {
                _projectTree.SetHighlight(path);
                _projectTree.ExpandToPath(path);
                ScrollProjectPanelToPath(path);
            }
            Repaint();
        }

        void OnAssetDragged(string path)
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (obj == null) return;
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = new Object[] { obj };
            DragAndDrop.paths = new string[] { path };
            DragAndDrop.StartDrag(Path.GetFileNameWithoutExtension(path));
        }

        void OnAssetModified(string path)
        {
            if (_selectedAssetPath == path)
                _selectedAssetPath = null;
            _tree.InvalidateCache();
            Repaint();
        }

        // ── Inline rename ─────────────────────────────────────────────────────

        void StartRename(string path)
        {
            _renamingPath = path;
            _renameBuffer = Path.GetFileNameWithoutExtension(path);
            _selectedAssetPath = path;
            Repaint();
        }

        void CommitRename(string newName)
        {
            if (_renamingPath == null) return;
            string path = _renamingPath;
            _renamingPath = null;
            _renameBuffer = null;

            string trimmed = newName.Trim();
            string oldName = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(trimmed) && trimmed != oldName)
            {
                string err = AssetDatabase.RenameAsset(path, trimmed);
                if (!string.IsNullOrEmpty(err))
                    Debug.LogError($"BetterTabs: Rename failed — {err}");
                AssetDatabase.Refresh();
                _tree.InvalidateCache();
            }
            Repaint();
        }

        void CancelRename()
        {
            _renamingPath = null;
            _renameBuffer = null;
            Repaint();
        }

        // ── Keyboard shortcuts & scroll navigation ────────────────────────────
        void HandleKeyboardShortcuts()
        {
            var ev = Event.current;

            // Shift+Scroll → cycle tabs   |   Ctrl+Shift+Scroll → move tab
            if (ev.type == EventType.ScrollWheel && ev.shift && _tabs.Count > 1)
            {
                // On Windows, Shift+Scroll arrives as horizontal scroll (delta.y=0, delta.x!=0).
                float rawScroll = Mathf.Abs(ev.delta.y) > 0.01f ? ev.delta.y : ev.delta.x;
                int dir = rawScroll > 0 ? -1 : 1;
                if (BetterTabsSettings.InvertScroll) dir = -dir;
                if (ev.control || ev.command)
                {
                    MoveTab(_selectedIndex, dir);
                }
                else
                {
                    int next = (_selectedIndex + dir + _tabs.Count) % _tabs.Count;
                    if (next != _selectedIndex) SelectTab(next);
                }
                ev.Use();
                return;
            }

            if (ev.type != EventType.KeyDown) return;
            bool ctrl = ev.control || ev.command;

            // Ctrl+W → close active tab
            if (ctrl && !ev.shift && ev.keyCode == KeyCode.W && _selectedIndex >= 0)
            {
                RemoveTab(_selectedIndex);
                ev.Use();
                return;
            }

            // Ctrl+Shift+T → reopen last closed tab
            if (ctrl && ev.shift && ev.keyCode == KeyCode.T)
            {
                ReopenClosedTab();
                ev.Use();
            }
        }

        void OpenNewTab()
        {
            // Use the active Project selection if it has an asset path.
            if (Selection.activeObject != null)
            {
                string selPath = AssetDatabase.GetAssetPath(Selection.activeObject);
                if (!string.IsNullOrEmpty(selPath))
                {
                    AddOrSelectTab(selPath);
                    return;
                }
            }
            // Fallback: OS folder picker.
            string folder = EditorUtility.OpenFolderPanel("Add Folder Tab", "Assets", "");
            if (string.IsNullOrEmpty(folder)) return;
            if (folder.StartsWith(Application.dataPath))
                folder = "Assets" + folder.Substring(Application.dataPath.Length);
            if (!string.IsNullOrEmpty(folder))
                AddOrSelectTab(folder);
        }

        void ReopenClosedTab()
        {
            while (_closedTabStack.Count > 0)
            {
                string path = _closedTabStack.Pop();
                // Skip if already open.
                bool alreadyOpen = false;
                foreach (var t in _tabs) if (t.path == path) { alreadyOpen = true; break; }
                if (!alreadyOpen) { AddOrSelectTab(path); return; }
            }
        }

        void MoveTab(int index, int direction)
        {
            if (index < 0 || index >= _tabs.Count || _tabs.Count <= 1) return;
            // Wrap-around so both scroll directions always produce movement.
            int target = (index + direction + _tabs.Count) % _tabs.Count;
            SwapTabs(index, target);
            BetterTabsPrefs.Save(_tabs, _selectedIndex);
            Repaint();
        }

        // ── Tab management ────────────────────────────────────────────────────
        void AddOrSelectTab(string path)
        {
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].path == path) { SelectTab(i); return; }
            }
            _tabs.Add(new BetterTabEntry(path));
            SelectTab(_tabs.Count - 1);
            BetterTabsPrefs.Save(_tabs, _selectedIndex);
        }

        void SelectTab(int index)
        {
            PersistActiveTabState();
            _selectedIndex = index;
            _scrollToSelected = true;
            _selectedAssetPath = null;
            _renamingPath = null;
            _renameBuffer = null;
            _assetScroll = Vector2.zero;
            _tree.InvalidateCache();

            var tab = _tabs[index];
            if (!AssetDatabase.IsValidFolder(tab.path))
            {
                var obj = AssetDatabase.LoadAssetAtPath<Object>(tab.path);
                if (obj != null) Selection.activeObject = obj;
            }

            RestoreActiveTabState();
            SyncProjectTreeHighlight();
            Repaint();
            BetterTabsPrefs.Save(_tabs, _selectedIndex);
        }

        void RemoveTab(int index)
        {
            _closedTabStack.Push(_tabs[index].path);
            _tabs.RemoveAt(index);
            if (_tabs.Count == 0)
            {
                _selectedIndex = -1;
                _search.Clear();
                _searchInputText = "";
                _tree.InvalidateCache();
            }
            else
            {
                _selectedIndex = Mathf.Clamp(_selectedIndex, 0, _tabs.Count - 1);
                _tree.InvalidateCache();
                RestoreActiveTabState();
            }
            BetterTabsPrefs.Save(_tabs, _selectedIndex);
            Repaint();
        }

        void CloseOthers(int keepIndex)
        {
            var keep = _tabs[keepIndex];
            _tabs.Clear();
            _tabs.Add(keep);
            SelectTab(0);
        }

        void PingTab(int index)
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>(_tabs[index].path);
            if (obj != null)
            {
                Selection.activeObject = obj;
                EditorGUIUtility.PingObject(obj);
            }
        }

        // ── Per-tab state persistence ─────────────────────────────────────────
        void PersistActiveTabState()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _tabs.Count) return;
            var tab = _tabs[_selectedIndex];
            _tree.SaveExpandedState(tab.expandedPaths);
            tab.searchQuery = _search.CommittedQuery;
        }

        void RestoreActiveTabState()
        {
            _search.Clear();
            _searchInputText = "";

            if (_selectedIndex < 0 || _selectedIndex >= _tabs.Count) return;
            var tab = _tabs[_selectedIndex];

            _tree.LoadExpandedState(tab.expandedPaths);

            if (!string.IsNullOrEmpty(tab.searchQuery))
            {
                _searchInputText = tab.searchQuery;
                _search.ForceCommit(tab.searchQuery, tab.path);
            }
        }

        void OnFoldoutToggled()
        {
            if (_selectedIndex >= 0 && _selectedIndex < _tabs.Count)
            {
                _tree.SaveExpandedState(_tabs[_selectedIndex].expandedPaths);
                BetterTabsPrefs.Save(_tabs, _selectedIndex);
            }
        }

        void ClearSearch()
        {
            _searchInputText = "";
            _search.Clear();
            if (_selectedIndex >= 0 && _selectedIndex < _tabs.Count)
                _tabs[_selectedIndex].searchQuery = "";
            BetterTabsPrefs.Save(_tabs, _selectedIndex);
            _assetScroll = Vector2.zero;
            Repaint();
        }

        void SyncSearchToTab()
        {
            if (_selectedIndex >= 0 && _selectedIndex < _tabs.Count)
                _tabs[_selectedIndex].searchQuery = _search.CommittedQuery;
        }

        string ActiveTabPath() =>
            _selectedIndex >= 0 && _selectedIndex < _tabs.Count
                ? _tabs[_selectedIndex].path
                : null;

        bool ActiveTabIsFolder()
        {
            var p = ActiveTabPath();
            return p != null && AssetDatabase.IsValidFolder(p);
        }

        static Texture2D GetTabIcon(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return EditorGUIUtility.FindTexture("Folder Icon");
            var icon = AssetDatabase.GetCachedIcon(path) as Texture2D;
            if (icon != null) return icon;
            return EditorGUIUtility.FindTexture("DefaultAsset Icon");
        }

        static Texture2D LoadWindowIcon()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Script BetterTabsWindow"))
            {
                var scriptPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!scriptPath.EndsWith("FolderTabsWindow.cs")) continue;
                var dir = Path.GetDirectoryName(scriptPath)?.Replace('\\', '/');
                return AssetDatabase.LoadAssetAtPath<Texture2D>($"{dir}/Icons/icon.png");
            }
            return null;
        }

    }
}
