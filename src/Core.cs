using System;
using UnityEngine;

namespace DialogueFlow
{
    public sealed class ModEntry : IMod
    {
        public const string Id = "dialogue_flow";
        public const string Version = "0.1.2";
        private ModManifest manifest = null!;
        private ModLog log = null!;
        private FlowRuntime? runtime;

        public void Init(ModManifest manifest)
        {
            this.manifest = manifest;
            log = new ModLog(manifest);
        }

        public void OnEnable()
        {
            if (runtime != null) return;
            GameObject host = new GameObject("DialogueFlow.Runtime");
            UnityEngine.Object.DontDestroyOnLoad(host);
            runtime = host.AddComponent<FlowRuntime>();
            try
            {
                runtime.Initialize(manifest, log);
                log.Log("深空当铺：允许跳过对话已加载，自动跳过默认关闭。设置可在快捷面板和官方键位选项中修改。");
            }
            catch
            {
                runtime.Shutdown();
                UnityEngine.Object.Destroy(host);
                runtime = null;
                throw;
            }
        }

        public void OnDisable()
        {
            if (runtime == null) return;
            runtime.Shutdown();
            UnityEngine.Object.Destroy(runtime.gameObject);
            runtime = null;
        }
    }

    public sealed class FlowRuntime : MonoBehaviour
    {
        private UserInterface? controls;
        private DialogueSession? session;
        private ModLog log = null!;
        private bool stopped;

        internal void Initialize(ModManifest manifest, ModLog log)
        {
            this.log = log;
            session = new DialogueSession(log);
            controls = new UserInterface(manifest, log);
        }

        private void Update()
        {
            if (stopped || controls == null || session == null) return;
            try
            {
                bool inGame = PlayerStore.HasInstance && StoreUIManager.Instance != null;
                controls.Update(inGame, session.IsActive, session.Speed, session.IsSkipping);
                bool blocked = !inGame || controls.BlocksInput;
                bool accelerate = !blocked && controls.CanAccelerate
                    && controls.AccelerateKey != KeyCode.None && Input.GetKey(controls.AccelerateKey);
                bool skip = !blocked && controls.CanSkip
                    && controls.SkipKey != KeyCode.None && Input.GetKeyDown(controls.SkipKey);
                session.Tick(Time.unscaledDeltaTime, accelerate, skip, controls.AutoSkip, blocked);
            }
            catch (Exception error)
            {
                // 对白动作可能已有副作用，异常后释放本 MOD 控制权，避免逐帧重试再次推进。
                log.Error("深空当铺：允许跳过对话已停止控制，请保存日志并重启游戏。" + error);
                Shutdown();
                enabled = false;
            }
        }

        internal void Shutdown()
        {
            if (stopped) return;
            stopped = true;
            try
            {
                session?.Dispose();
            }
            finally
            {
                controls?.Dispose();
            }
        }

        private void OnDestroy()
        {
            Shutdown();
        }
    }
}
