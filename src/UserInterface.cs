#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace DialogueFlow
{
    internal sealed class UserInterface : IDisposable
    {
        private const string AccelerateOption = "accelerate";
        private const string SkipOption = "skip";
        private const string WindowSuffix = ".quick_panel";

        private static readonly KeyCode[] DefaultCandidates =
        {
            KeyCode.F6, KeyCode.F7, KeyCode.F10, KeyCode.F11,
            KeyCode.F12, KeyCode.F5, KeyCode.F4, KeyCode.F3,
            KeyCode.F2, KeyCode.F1
        };

        private readonly string modId;
        private readonly ModLog log;
        private readonly ConfigEntry<bool> autoSkip;
        private readonly ConfigEntry<KeyCode> accelerateKey;
        private readonly ConfigEntry<KeyCode> skipKey;
        private readonly HashSet<string> reportedEntries = new HashSet<string>();

        private CustomUIWindow? window;
        private string accelerateIssue = string.Empty;
        private string skipIssue = string.Empty;
        private string lastWarning = string.Empty;
        private string? displayedStatus;
        private string? displayedBindings;
        private string? displayedWarning;
        private bool displayedAutoSkip;
        private bool panelDismissed;
        private bool bindingsDirty;
        private bool registryReadFailed;
        private bool disposed;
        private float nextConflictCheck;
        private float nextDisplayUpdate;
        private float nextWindowAttempt;
        private KeyCode lastAccelerateKey;
        private KeyCode lastSkipKey;

        public UserInterface(ModManifest manifest, ModLog log)
        {
            modId = manifest.ID;
            this.log = log;

            GameOptionRegistry.EnsureVanilla();
            ModConfig config = ModHelper.GetConfig(modId);
            autoSkip = config.Define("auto_skip", false, "深空当铺：允许跳过对话",
                () => "自动跳过前置对话",
                () => "当前存档首次遇见某类 NPC 时正常展示开场对白。", false);

            KeyCode speedDefault = FindAvailableDefault(KeyCode.None);
            accelerateKey = config.DefineEnum("accelerate_key", speedDefault, "快捷键",
                () => "加速键（按住）",
                () => "按住时平滑加速交易开场对白，松开后恢复。", false);
            KeyCode skipDefault = FindAvailableDefault(accelerateKey.Value);
            skipKey = config.DefineEnum("skip_key", skipDefault, "快捷键",
                () => "跳过键（按下）",
                () => "按下后以淡出淡入过渡推进当前交易开场对白。", false);

            ModHelper.AddGameOptionKeyBind(modId, ModHelper.OPTION_SECTION_KEYBIND,
                AccelerateOption, () => "深空当铺：允许跳过对话 · 按住加速",
                () => AccelerateKey, SetAccelerateKey,
                () => "按住加速当前交易开场对白，松开恢复。");
            ModHelper.AddGameOptionKeyBind(modId, ModHelper.OPTION_SECTION_KEYBIND,
                SkipOption, () => "深空当铺：允许跳过对话 · 跳过开场",
                () => SkipKey, SetSkipKey,
                () => "按下跳过当前交易开场对白，保留原有动作和玩家选项。");

            ModHelper.AddGameOptionToggle(modId, ModHelper.OPTION_SECTION_DIALOGUE,
                "show_panel", () => "显示深空当铺：允许跳过对话面板",
                () => !panelDismissed, SetPanelVisible,
                () => "在游戏中显示快捷面板，可在关闭面板后重新打开。");
            RefreshConflicts();
        }

        public bool AutoSkip => autoSkip.Value;
        public KeyCode AccelerateKey => accelerateKey.Value;
        public KeyCode SkipKey => skipKey.Value;
        public bool CanAccelerate => !disposed && !registryReadFailed && accelerateIssue.Length == 0;
        public bool CanSkip => !disposed && !registryReadFailed && skipIssue.Length == 0;

        public bool BlocksInput
        {
            get
            {
                if (disposed || KeyBindHelper.SuppressHotkeys || StoreUIManager.IsTextFieldFocused())
                    return true;

                GameOptionUIManager options = GameOptionUIManager.Instance;
                if (options != null && options.IsOpen)
                    return true;

                // 原版 ESC 菜单只切换可见性，不会把 Time.timeScale 设为 0。
                StoreUIManager store = StoreUIManager.Instance;
                if (store != null && store.escapeUIManager != null
                    && store.escapeUIManager.menuPanel != null
                    && store.escapeUIManager.menuPanel.activeSelf)
                    return true;

                if (window == null || !window.IsAlive || !window.IsVisible)
                    return false;

                RectTransform rect = window.Rect;
                if (rect == null)
                    return false;

                Canvas? canvas = rect.GetComponentInParent<Canvas>();
                Camera? camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                return RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition, camera);
            }
        }

        public void Update(bool inGame, bool eligible, float speed, bool skipping)
        {
            if (disposed)
                return;

            float now = Time.unscaledTime;
            if (bindingsDirty || AccelerateKey != lastAccelerateKey || SkipKey != lastSkipKey || now >= nextConflictCheck)
            {
                RefreshConflicts();
                nextConflictCheck = now + 0.5f;
            }

            if (!inGame)
            {
                ClosePanel();
                return;
            }

            if (panelDismissed)
                return;

            if (window == null || !window.IsAlive)
            {
                if (now < nextWindowAttempt || CustomUIManager.Instance == null)
                    return;
                nextWindowAttempt = now + 1f;
                CreatePanel();
            }

            if (window == null || !window.IsAlive || now < nextDisplayUpdate)
                return;
            nextDisplayUpdate = now + 0.1f;

            string status = skipping ? "正在跳过前置对白…"
                : speed > 1.02f ? "▶▶ 快进 ×" + speed.ToString("0.0", CultureInfo.InvariantCulture)
                : eligible ? "按住加速键，或按跳过键"
                : "等待交易开场对白";
            string bindings = "加速 " + AccelerateKey + "（按住）    跳过 " + SkipKey;
            string warning = BuildWarning();

            if (displayedStatus != status)
            {
                window.Get("status").SetText(status);
                displayedStatus = status;
            }
            if (displayedBindings != bindings)
            {
                window.Get("bindings").SetText(bindings);
                displayedBindings = bindings;
            }
            if (displayedAutoSkip != AutoSkip)
            {
                window.Get("auto_skip").SetBool(AutoSkip, false);
                displayedAutoSkip = AutoSkip;
            }
            if (displayedWarning != warning)
            {
                window.Get("conflicts").SetText(warning).SetVisible(warning.Length > 0);
                displayedWarning = warning;
            }
        }

        private void CreatePanel()
        {
            CustomUIBuilder? builder = CustomUIManager.Window(modId + WindowSuffix,
                "深空当铺：允许跳过对话", CustomUIManager.LAYER_OVERLAY);
            if (builder == null)
                return;

            window = builder
                .SetSize(340f, 260f)
                .SetDraggable(true)
                .OnClose(() => panelDismissed = true)
                .AddLabel("等待交易开场对白", "status")
                .AddToggle("自动跳过前置对话", AutoSkip, value => autoSkip.Set(value), "auto_skip")
                .AddLabel("", "bindings")
                .AddButton("修改快捷键", OpenKeySettings, "key_settings")
                .AddLabel("冲突检查覆盖原版与已登记 MOD")
                .AddWarning("", "conflicts")
                .Show();

            if (window == null || !window.IsAlive)
                return;

            RectTransform rect = window.Rect;
            if (rect != null)
            {
                rect.anchorMin = Vector2.one;
                rect.anchorMax = Vector2.one;
                rect.pivot = Vector2.one;
                window.SetPosition(new Vector2(-16f, -16f));
            }

            displayedStatus = null;
            displayedBindings = null;
            displayedWarning = null;
            displayedAutoSkip = AutoSkip;
            nextDisplayUpdate = 0f;
        }

        private void SetPanelVisible(bool visible)
        {
            panelDismissed = !visible;
            if (!visible)
                ClosePanel();
        }
        private void OpenKeySettings()
        {
            GameOptionUIManager options = GameOptionUIManager.Instance;
            if (options != null)
                options.OpenUI();
            else
                log.Warning("当前场景的官方键位设置尚未就绪。");
        }

        private void SetAccelerateKey(KeyCode value)
        {
            accelerateKey.Set(value);
            bindingsDirty = true;
        }

        private void SetSkipKey(KeyCode value)
        {
            skipKey.Set(value);
            bindingsDirty = true;
        }

        private KeyCode FindAvailableDefault(KeyCode otherKey)
        {
            foreach (KeyCode candidate in DefaultCandidates)
            {
                if (candidate != otherKey && FindRegisteredOwner(candidate, null).Length == 0)
                    return candidate;
            }
            return KeyCode.None;
        }

        private void RefreshConflicts()
        {
            // 后加载的 MOD 与玩家改绑都可能改变占用，保存的选择保持原值并暂停冲突动作。
            GameOptionRegistry.EnsureVanilla();
            registryReadFailed = false;
            accelerateIssue = FindIssue(AccelerateKey, ModHelper.GameOptionId(modId, AccelerateOption));
            skipIssue = FindIssue(SkipKey, ModHelper.GameOptionId(modId, SkipOption));
            if (AccelerateKey != KeyCode.None && AccelerateKey == SkipKey)
            {
                accelerateIssue = "加速与跳过使用了同一个按键，请重新绑定。";
                skipIssue = accelerateIssue;
            }

            lastAccelerateKey = AccelerateKey;
            lastSkipKey = SkipKey;
            bindingsDirty = false;
            string warning = BuildWarning();
            if (warning.Length > 0 && warning != lastWarning)
                log.Warning(warning);
            lastWarning = warning;
        }

        private string FindIssue(KeyCode key, string ownOptionId)
        {
            if (key == KeyCode.None)
                return "快捷键未绑定，请在官方键位设置中选择按键。";
            if (key == KeyCode.Escape || key == KeyCode.Mouse0)
                return "该按键由原版界面保留，请重新绑定。";
            if (key == KeyCode.F8 || key == KeyCode.F9)
                return "该按键用于原版开发工具，请重新绑定。";

            string owner = FindRegisteredOwner(key, ownOptionId);
            return owner.Length == 0 ? string.Empty : key + " 已由 " + owner + " 占用，相关手动操作已暂停。";
        }

        private string FindRegisteredOwner(KeyCode key, string? ignoredOptionId)
        {
            foreach (GameOptionRegistry.Section section in GameOptionRegistry.Sections.ToArray())
            {
                if (section == null || section.entries == null)
                    continue;

                foreach (GameOptionRegistry.Entry entry in section.entries.ToArray())
                {
                    if (entry == null || entry.id == ignoredOptionId || entry.kind != GameOptionRegistry.Kind.KeyBind || entry.getKey == null)
                        continue;

                    try
                    {
                        if (entry.getKey() != key)
                            continue;

                        string label = entry.label?.Invoke() ?? entry.id ?? "未命名键位";
                        if (string.IsNullOrEmpty(label))
                            label = entry.id ?? "未命名键位";
                        string source = string.IsNullOrEmpty(entry.source) ? "原版" : entry.source;
                        return label + "（" + source + "）";
                    }
                    catch (Exception error)
                    {
                        registryReadFailed = true;
                        string id = entry.id ?? string.Empty;
                        if (reportedEntries.Add(id))
                            log.Warning("读取已登记键位失败 " + id + "，" + error.Message);
                    }
                }
            }
            return string.Empty;
        }

        private string BuildWarning()
        {
            if (registryReadFailed)
                return "部分已登记键位读取失败，手动快捷键暂时停用。";
            if (accelerateIssue.Length == 0)
                return skipIssue;
            if (skipIssue.Length == 0 || skipIssue == accelerateIssue)
                return accelerateIssue;
            return accelerateIssue + "\n" + skipIssue;
        }

        private void ClosePanel()
        {
            // 场景退出的程序清理保留显示选择，用户点关闭仍由 OnClose 更新偏好。
            bool dismissed = panelDismissed;
            if (window != null && window.IsAlive)
                window.Close();
            window = null;
            panelDismissed = dismissed;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            ClosePanel();
            ModHelper.RemoveGameOptions(modId);
        }
    }
}

