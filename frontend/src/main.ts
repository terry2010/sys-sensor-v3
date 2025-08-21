import { createApp } from 'vue';
import { createPinia } from 'pinia';
import App from './App.vue';

// 提前探测并标记宿主：若存在 __TAURI__，立即标记为 tauri，避免首屏落入 mock
try {
  const w: any = typeof window !== 'undefined' ? window : {};
  if (w && w.__TAURI__) {
    w.__IS_TAURI__ = true;
    // 异步启动事件桥（不阻塞首屏挂载）
    (async () => {
      try { const { ensureEventBridge } = await import('./api/rpc.tauri'); await ensureEventBridge(); } catch {}
    })();
  }
} catch {}

const app = createApp(App);
app.use(createPinia());
app.mount('#app');
