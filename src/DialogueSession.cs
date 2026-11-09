#nullable enable
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace DialogueFlow
{
    internal sealed class DialogueSession : IDisposable
    {
        private enum SkipPhase { Idle, FadeOut, Advance, FadeIn }

        private const float FadeOutDuration = 0.14f;
        private const float FadeInDuration = 0.18f;
        private const float SkipTimeout = 8f;
        private readonly ModLog log;
        private readonly SpeedEnvelope speedEnvelope = new SpeedEnvelope();
        private readonly ElasticSpring jelly = new ElasticSpring(0f);
        private readonly HashSet<Dialogue> openingNodes = new HashSet<Dialogue>();
        private readonly List<BubbleState> bubbles = new List<BubbleState>();
        private DialogUIManager? manager;
        private StoreClientInstance? customer;
        private StoreClient? blueprint;
        private Dialogue? openingRoot;
        private Dialogue? lastLine;
        private SkipPhase skipPhase;
        private float phaseTime;
        private float skipTime;
        private float readyTime;
        private float opacity = 1f;
        private bool autoAttempted;
        private bool openingStarted;
        private bool openingFinished;
        private bool issuingAdvance;
        private bool blocked;
        private bool faulted;
        private bool disposed;
        private GameObject? transitionRoot;
        private CanvasGroup? transitionGroup;

        internal static DialogueSession? Current { get; private set; }
        public bool IsActive { get; private set; }
        public bool IsSkipping => skipPhase != SkipPhase.Idle;
        public float Speed => speedEnvelope.Value;

        public DialogueSession(ModLog log)
        {
            this.log = log;
            Current = this;
        }

        public void Tick(float deltaTime, bool accelerateHeld, bool skipPressed, bool autoSkip, bool inputBlocked)
        {
            if (disposed) return;
            float dt = Mathf.Clamp(deltaTime, 0f, 0.1f);
            blocked = inputBlocked || !Application.isFocused || Time.timeScale <= 0f;
            RefreshContext();
            IsActive = CanControlOpening();

            if (manager == null)
            {
                ResetEffects();
                return;
            }

            if (!ReferenceEquals(lastLine, manager.currentDialog))
            {
                lastLine = manager.currentDialog;
                readyTime = 0f;
            }

            bool choice = manager.currentDialog != null && manager.IsChoice();
            if (choice)
            {
                // 一个自动请求到选项即结束，选择后由新的手动操作决定是否继续。
                autoAttempted = true;
                BeginFadeIn();
            }
            if ((!IsActive || blocked) && skipPhase != SkipPhase.Idle)
                BeginFadeIn();

            bool held = IsActive && !blocked && accelerateHeld && skipPhase == SkipPhase.Idle;
            speedEnvelope.Step(held, dt);
            jelly.Step(held ? 1f : 0f, dt);

            if (!IsActive)
            {
                speedEnvelope.Reset();
                jelly.Reset(0f);
            }

            if (IsActive && !blocked && skipPhase == SkipPhase.Idle)
            {
                bool automatic = autoSkip && !autoAttempted && IsRepeatVisit();
                if (skipPressed || automatic)
                {
                    autoAttempted = true;
                    skipPhase = SkipPhase.FadeOut;
                    phaseTime = 0f;
                    skipTime = 0f;
                    EnsureTransitionOverlay();
                }
            }

            UpdateSkip(dt);
            ApplyBubbleEffects();

            if (held && !IsSkipping)
                AdvanceWhileHeld(dt);
            else
                readyTime = 0f;
        }

        private void RefreshContext()
        {
            DialogUIManager? nextManager = DialogUIManager.Instance;
            StoreClientInstance? nextCustomer = PlayerStore.HasInstance
                ? PlayerStore.Instance.currentClientInstance : null;
            if (nextManager != manager)
            {
                ReleaseBubbles();
                manager = nextManager;
            }
            // UI 引用可晚于单例出现，逐帧按对象身份补齐，每个对象只捕获一次。
            if (manager != null)
            {
                AddBubble(manager.clientBubble);
                AddBubble(manager.playerBubble);
                AddBubble(manager.secBubble);
                AddName(manager.clientNameBubble);
                AddName(manager.secNameBubble);
            }

            if (!ReferenceEquals(nextCustomer, customer))
            {
                ResetEffects();
                customer = nextCustomer;
                blueprint = customer?.GetClientBlueprint();
                openingRoot = blueprint?.mainDialogue;
                openingNodes.Clear();
                openingStarted = false;
                openingFinished = false;
                autoAttempted = false;
                faulted = false;
                lastLine = null;
            }

            if (manager == null || blueprint == null || openingFinished) return;
            if (openingRoot == null && !openingStarted)
                openingRoot = blueprint.mainDialogue;
            RefreshOpeningNodes();
            Dialogue? line = manager.currentDialog;
            if (line != null && openingNodes.Contains(line))
            {
                openingStarted = true;
                return;
            }
            if (openingStarted && !manager.HasPausedConversation)
                openingFinished = true;
        }

        private void RefreshOpeningNodes()
        {
            // 使用实际引用链识别开场。isMainDialog 只标记单节点，且链可在动作中追加。
            openingNodes.Clear();
            Dialogue? node = openingRoot;
            while (node != null && openingNodes.Count < 256 && openingNodes.Add(node))
            {
                if (node.HasChoices()) break;
                node = node.nextDialogue;
            }
        }

        private bool CanControlOpening()
        {
            return !faulted && manager != null && blueprint != null && customer != null
                && !openingFinished && manager.currentDialog != null
                && openingNodes.Contains(manager.currentDialog)
                && !manager.IsChoice() && !manager.HasPausedConversation
                && !manager.isSecMonologuePlaying
                && IsTradingIntent(blueprint.clientIntent);
        }

        private static bool IsTradingIntent(StoreClient.ClientIntent intent)
        {
            switch (intent)
            {
                case StoreClient.ClientIntent.BUY:
                case StoreClient.ClientIntent.SELL:
                case StoreClient.ClientIntent.SELLNBUY:
                case StoreClient.ClientIntent.BARTER:
                case StoreClient.ClientIntent.INFORMATION_DEALER:
                case StoreClient.ClientIntent.PROCUREMENT_OFFER:
                case StoreClient.ClientIntent.PROCUREMENT_COLLECT:
                case StoreClient.ClientIntent.WHOLESALE:
                case StoreClient.ClientIntent.APPRAISAL_SERVICE:
                case StoreClient.ClientIntent.GUNSMITH:
                    return true;
                default:
                    return false;
            }
        }
        private bool IsRepeatVisit()
        {
            return blueprint != null && !string.IsNullOrEmpty(blueprint.identifier)
                // 原版在生成当前接待实例前已经累计本次访问，首遇计数因此是 1。
                && StoreClientManager.GetCustomerTotalVisitedCount(blueprint.identifier) >= 2;
        }

        private void UpdateSkip(float dt)
        {
            if (skipPhase == SkipPhase.Idle) return;
            phaseTime += dt;
            if (skipPhase == SkipPhase.FadeOut)
            {
                opacity = 1f - Smooth01(phaseTime / FadeOutDuration);
                if (phaseTime >= FadeOutDuration)
                {
                    opacity = 0f;
                    skipPhase = SkipPhase.Advance;
                    phaseTime = 0f;
                }
                return;
            }
            if (skipPhase == SkipPhase.FadeIn)
            {
                opacity = Smooth01(phaseTime / FadeInDuration);
                if (phaseTime >= FadeInDuration)
                {
                    opacity = 1f;
                    skipPhase = SkipPhase.Idle;
                }
                return;
            }

            opacity = 0f;
            skipTime += dt;
            if (!IsActive || blocked)
            {
                BeginFadeIn();
                return;
            }
            if (skipTime >= SkipTimeout)
            {
                // 对未能由原版推进的等待恢复显示，保留游戏本身的继续操作入口。
                log.Warning("跳过等待超时，已恢复对白显示。可通过原版继续操作检查当前状态。");
                BeginFadeIn();
                return;
            }
            IssueAdvance();
        }

        private void AdvanceWhileHeld(float dt)
        {
            if (manager == null || IsAnyBubbleTyping())
            {
                readyTime = 0f;
                return;
            }
            readyTime += dt * Speed;
            if (readyTime >= 0.45f)
            {
                readyTime = 0f;
                IssueAdvance();
            }
        }

        private bool IsAnyBubbleTyping()
        {
            foreach (BubbleState bubble in bubbles)
                if (bubble.Text != null && bubble.Text.gameObject.activeInHierarchy
                    && bubble.Text.typewriter != null && bubble.Text.typewriter.isShowingText)
                    return true;
            return false;
        }

        private void IssueAdvance()
        {
            if (manager == null || !CanControlOpening()) return;
            try
            {
                issuingAdvance = true;
                // 复用原版两阶段按键。ContinueConversation 会更新 advanceSerial，
                // 已排队的自动继续因此失效，每个节点仍执行自己的开始与结束动作。
                // 与原版空格和气泡点击入口保持一致，同时清除已排队的继续提示。
                TutorialUIManager tutorial = TutorialUIManager.Instance;
                if (tutorial != null)
                    tutorial.DismissAdvanceDialogHint();
                manager.OnAdvancePressed();
            }
            catch (Exception exception)
            {
                // 原版动作可能已经产生副作用，本次顾客停止自动重试并恢复显示。
                faulted = true;
                log.Error("对白推进发生异常，当前顾客恢复原版操作。" + exception);
                BeginFadeIn();
            }
            finally
            {
                issuingAdvance = false;
            }
        }

        private void BeginFadeIn()
        {
            if (skipPhase == SkipPhase.Idle || skipPhase == SkipPhase.FadeIn) return;
            // 从当前透明度连续恢复，避免淡出中途取消时发生亮度跳变。
            phaseTime = InverseSmooth01(opacity) * FadeInDuration;
            skipPhase = SkipPhase.FadeIn;
        }

        private void ApplyBubbleEffects()
        {
            bool affectText = IsActive && !blocked;
            float multiplier = affectText ? Speed : 1f;
            float deformation = affectText ? jelly.Value : 0f;
            foreach (BubbleState bubble in bubbles)
                bubble.Apply(multiplier, deformation, opacity);
            ApplyTransitionOverlay();
        }

        private void EnsureTransitionOverlay()
        {
            if (transitionRoot != null) return;
            // 独立的短时视觉遮罩让交易界面也能淡入，生命周期由本会话负责。
            transitionRoot = new GameObject("DialogueFlow.Transition",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup), typeof(GraphicRaycaster));
            Canvas canvas = transitionRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            transitionGroup = transitionRoot.GetComponent<CanvasGroup>();
            transitionGroup.alpha = 0f;
            transitionGroup.blocksRaycasts = false;
            GameObject shade = new GameObject("Shade", typeof(RectTransform), typeof(Image));
            shade.transform.SetParent(transitionRoot.transform, false);
            RectTransform rect = shade.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            Image image = shade.GetComponent<Image>();
            image.color = Color.black;
            image.raycastTarget = true;
        }

        private void ApplyTransitionOverlay()
        {
            if (transitionGroup == null) return;
            transitionGroup.alpha = 1f - opacity;
            transitionGroup.blocksRaycasts = skipPhase != SkipPhase.Idle;
        }
        private void AddBubble(TextBubble? bubble)
        {
            if (bubble == null || HasBubble(bubble.gameObject)) return;
            bubbles.Add(new BubbleState(bubble.gameObject, bubble));
        }

        private void AddName(GameObject? name)
        {
            if (name == null || HasBubble(name)) return;
            bubbles.Add(new BubbleState(name, null));
        }

        private bool HasBubble(GameObject gameObject)
        {
            foreach (BubbleState state in bubbles)
                if (state.IsFor(gameObject)) return true;
            return false;
        }
        private void ResetEffects()
        {
            skipPhase = SkipPhase.Idle;
            opacity = 1f;
            phaseTime = 0f;
            skipTime = 0f;
            readyTime = 0f;
            speedEnvelope.Reset();
            jelly.Reset(0f);
            foreach (BubbleState bubble in bubbles) bubble.Restore();
            ApplyTransitionOverlay();
        }

        private void ReleaseBubbles()
        {
            foreach (BubbleState bubble in bubbles) bubble.Dispose();
            bubbles.Clear();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ResetEffects();
            ReleaseBubbles();
            if (transitionRoot != null) UnityEngine.Object.Destroy(transitionRoot);
            transitionRoot = null;
            transitionGroup = null;
            if (ReferenceEquals(Current, this)) Current = null;
        }

        internal bool ShouldSuppressAdvance(DialogUIManager source)
        {
            return ReferenceEquals(source, manager) && CanControlOpening()
                && !issuingAdvance && (skipPhase == SkipPhase.FadeOut || skipPhase == SkipPhase.Advance);
        }

        internal float ScaleWait(float value)
        {
            return IsActive && !blocked && CanControlOpening() ? value / Mathf.Max(1f, Speed) : value;
        }

        private static float Smooth01(float value)
        {
            float t = Mathf.Clamp01(value);
            return t * t * (3f - 2f * t);
        }

        private static float InverseSmooth01(float value)
        {
            float t = Mathf.Clamp01(value);
            return 0.5f - Mathf.Sin(Mathf.Asin(1f - 2f * t) / 3f);
        }

        private sealed class BubbleState : IDisposable
        {
            private readonly Transform transform;
            private readonly CanvasGroup group;
            private readonly bool ownsGroup;
            private Vector3 baseScale;
            private Vector3 lastScale;
            private float baseAlpha;
            private float lastAlpha;
            private float normalWait;
            private float middleWait;
            private float longWait;
            private float lastNormalWait;
            private float lastMiddleWait;
            private float lastLongWait;
            public TextBubble? Text { get; }

            public BubbleState(GameObject gameObject, TextBubble? text)
            {
                Text = text;
                transform = gameObject.transform;
                baseScale = lastScale = transform.localScale;
                CanvasGroup? existing = gameObject.GetComponent<CanvasGroup>();
                ownsGroup = existing == null;
                group = existing != null ? existing : gameObject.AddComponent<CanvasGroup>();
                baseAlpha = lastAlpha = group.alpha;
                if (Text != null && Text.typewriter != null)
                {
                    normalWait = lastNormalWait = Text.typewriter.waitForNormalChars;
                    middleWait = lastMiddleWait = Text.typewriter.waitMiddle;
                    longWait = lastLongWait = Text.typewriter.waitLong;
                }
            }

            public bool IsFor(GameObject gameObject)
            {
                return transform != null && transform.gameObject == gameObject;
            }

            public void Apply(float speed, float deformation, float opacity)
            {
                if (transform == null || group == null) return;
                // 原版字号选项可能在会话中重设比例，以它的新基值继续叠加局部反馈。
                if (transform.localScale != lastScale) baseScale = transform.localScale;
                if (!Mathf.Approximately(group.alpha, lastAlpha)) baseAlpha = group.alpha;
                lastScale = Vector3.Scale(baseScale,
                    new Vector3(1f + 0.045f * deformation, 1f - 0.03f * deformation, 1f));
                transform.localScale = lastScale;
                lastAlpha = baseAlpha * opacity;
                group.alpha = lastAlpha;

                if (Text == null || Text.typewriter == null) return;
                var writer = Text.typewriter;
                if (!Mathf.Approximately(writer.waitForNormalChars, lastNormalWait)) normalWait = writer.waitForNormalChars;
                if (!Mathf.Approximately(writer.waitMiddle, lastMiddleWait)) middleWait = writer.waitMiddle;
                if (!Mathf.Approximately(writer.waitLong, lastLongWait)) longWait = writer.waitLong;
                // 缩短公开等待时间，原版内部的语言和玩家字速倍率继续参与计算。
                writer.waitForNormalChars = lastNormalWait = normalWait / speed;
                writer.waitMiddle = lastMiddleWait = middleWait / speed;
                writer.waitLong = lastLongWait = longWait / speed;
            }

            public void Restore()
            {
                if (transform != null && transform.localScale == lastScale) transform.localScale = baseScale;
                if (group != null && Mathf.Approximately(group.alpha, lastAlpha)) group.alpha = baseAlpha;
                lastScale = baseScale;
                lastAlpha = baseAlpha;
                if (Text == null || Text.typewriter == null) return;
                var writer = Text.typewriter;
                if (Mathf.Approximately(writer.waitForNormalChars, lastNormalWait)) writer.waitForNormalChars = normalWait;
                if (Mathf.Approximately(writer.waitMiddle, lastMiddleWait)) writer.waitMiddle = middleWait;
                if (Mathf.Approximately(writer.waitLong, lastLongWait)) writer.waitLong = longWait;
                lastNormalWait = normalWait;
                lastMiddleWait = middleWait;
                lastLongWait = longWait;
            }

            public void Dispose()
            {
                Restore();
                if (ownsGroup && group != null) UnityEngine.Object.Destroy(group);
            }
        }
    }

    [HarmonyPatch(typeof(DialogUIManager), nameof(DialogUIManager.ScaleByDialogSpeed))]
    internal static class DialoguePacingPatch
    {
        private static void Postfix(ref float __result)
        {
            DialogueSession? session = DialogueSession.Current;
            if (session != null) __result = session.ScaleWait(__result);
        }
    }

    [HarmonyPatch(typeof(DialogUIManager), nameof(DialogUIManager.OnAdvancePressed))]
    internal static class DialogueAdvanceInputPatch
    {
        private static bool Prefix(DialogUIManager __instance)
        {
            return DialogueSession.Current?.ShouldSuppressAdvance(__instance) != true;
        }
    }
}