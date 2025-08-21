<template>
  <div class="container">
    <header>
      <h1>SysSensorV3 DebugView</h1>
      <p class="sub">Tauri + Vue（当前来源：{{ rpcSource }}）</p>
      <div class="health">
        <span>events: {{ metrics.count }}</span>
        <span :class="{ stale: isStale }">last: {{ lastDeltaSec }}s</span>
        <span class="badge" :class="rpcSourceClass">RPC: {{ rpcSource }}</span>
        <span v-if="bridge.rx > 0" class="badge ok">bridge_rx: {{ bridge.rx }}</span>
        <span v-if="bridge.err > 0" class="badge warn">bridge_err: {{ bridge.err }}</span>
        <span v-if="state.phase" class="badge" :class="stateBadgeClass">state: {{ state.phase }}</span>
      </div>
      <div v-if="session.error" class="error">会话失败：{{ session.error }}</div>
      <div v-else-if="isStale" class="warn">未收到 metrics，请确认后端是否运行，或稍候片刻…</div>
    </header>

    <section class="cards">
      <SnapshotPanel />
      <HistoryChart />
      <ControlPanel />
      <HistoryQuery />
    </section>
  </div>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted, ref, computed } from 'vue';
import SnapshotPanel from './components/SnapshotPanel.vue';
import HistoryChart from './components/HistoryChart.vue';
import ControlPanel from './components/ControlPanel.vue';
import HistoryQuery from './components/HistoryQuery.vue';
import { useSessionStore } from './stores/session';
import { useMetricsStore } from './stores/metrics';
import { service } from './api/service';
import { ensureEventBridge } from './api/rpc.tauri';

const session = useSessionStore();
const metrics = useMetricsStore();
const bridge = ref({ rx: 0, err: 0 });
const state = ref<{ phase: string | null; ts: number | null }>({ phase: null, ts: null });

onMounted(async () => {
  // 统一初始化顺序：先探测并尝试启动事件桥 -> 再进行会话与 metrics 订阅
  const w: any = typeof window !== 'undefined' ? window : {};
  try {
    await import('@tauri-apps/api/core');
    w.__IS_TAURI__ = true;
    // 先设置订阅开关（通过 bridge_subscribe），确保事件桥首次握手后的初始订阅为 enable=true
    try { void service.subscribeMetrics(true); } catch {}
    // 再非阻塞启动事件桥（不要等待，避免后端异常导致 UI 等待）
    void ensureEventBridge();
    // 调试监听：观察桥接是否有事件/错误到达
    try {
      const { listen } = await import('@tauri-apps/api/event');
      void listen('bridge_rx', (_e: any) => { bridge.value.rx++; });
      void listen('bridge_error', (_e: any) => { bridge.value.err++; });
      // 直接监听 metrics，写入 store（用于绕过 service.onMetrics 的链路验证）
      void listen('metrics', (e: any) => {
        const p = e?.payload as any;
        if (!p) return;
        metrics.latest = p;
        metrics.history.push(p);
        if (metrics.history.length > 300) metrics.history.shift();
        metrics.lastAt = Date.now();
        metrics.count += 1;
        const w: any = typeof window !== 'undefined' ? window : {};
        if (!w.__METRICS_READY) w.__METRICS_READY = true;
      });
      // 监听 state 事件，展示生命周期状态角标
      void listen('state', (e: any) => {
        const p = e?.payload as any;
        if (!p) return;
        state.value.phase = p.phase ?? null;
        state.value.ts = typeof p.ts === 'number' ? p.ts : Date.now();
      });
    } catch { /* ignore */ }
  } catch {
    w.__IS_TAURI__ = false;
  }
  // 非阻塞：优先启动 metrics 监听；session.init() 不阻塞主流程
  metrics.start();
  void session.init();
  // 自动启动默认采集模块，确保有数据可推送
  setTimeout(async () => {
    try {
      console.log('[App] 尝试自动启动采集模块...');
      const result = await service.start?.({ modules: ['cpu', 'mem'] });
      console.log('[App] 自动启动采集模块成功:', result);
    } catch (e) {
      console.error('[App] 自动启动采集模块失败:', e);
    }
  }, 2000); // 延迟 2 秒，确保桥接已建立
});

// 健康状态（每秒刷新）
const tick = ref(0);
let t: any = null;
onMounted(() => { t = setInterval(() => tick.value++, 1000); });
onUnmounted(() => { if (t) clearInterval(t); });
const lastDeltaSec = computed(() => metrics.lastAt ? Math.floor((Date.now() - metrics.lastAt) / 1000) : -1);
const isStale = computed(() => lastDeltaSec.value < 0 || lastDeltaSec.value > 5);

// state 角标新鲜度（>10s 视为过期，黄色提示）
const stateBadgeClass = computed(() => {
  const ts = state.value.ts ?? 0;
  if (!ts) return 'warn';
  const age = Date.now() - ts;
  return age <= 10_000 ? 'ok' : 'warn';
});

// RPC 来源徽标（mock / tauri）
const rpcSource = computed(() => {
  // 依赖 tick 以周期性重评估环境
  void tick.value;
  const w: any = typeof window !== 'undefined' ? window : {};
  return w.__IS_TAURI__ ? 'tauri' : 'mock';
});
const rpcSourceClass = computed(() => rpcSource.value === 'tauri' ? 'ok' : 'warn');

// 探测逻辑已合并进上面的 onMounted 初始化
</script>

<style scoped>
.container {
  padding: 16px;
  font-family: -apple-system, Segoe UI, Roboto, Helvetica, Arial, 'Microsoft YaHei', sans-serif;
}
header {
  margin-bottom: 16px;
}
.sub { color: #666; font-size: 12px; }
.health { display: flex; gap: 10px; font-size: 12px; margin-top: 6px; }
.health .stale { color: #c00; font-weight: 600; }
.badge { padding: 2px 6px; border-radius: 10px; border: 1px solid #ddd; }
.badge.ok { background: #e6ffed; color: #046c4e; border-color: #b7ebc6; }
.badge.warn { background: #fff7e6; color: #ad4e00; border-color: #ffd591; }
.error { margin-top: 6px; color: #b00020; font-size: 12px; }
.warn { margin-top: 6px; color: #ad4e00; font-size: 12px; }
.cards { display: grid; gap: 16px; grid-template-columns: 1fr; }
@media (min-width: 900px) {
  .cards { grid-template-columns: 1fr 1fr; }
}
</style>
