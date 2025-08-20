<template>
  <div class="container">
    <header>
      <h1>SysSensorV3 DebugView</h1>
      <p class="sub">Tauri + Vue（当前来源：{{ rpcSource }}）</p>
      <div class="health">
        <span>events: {{ metrics.count }}</span>
        <span :class="{ stale: isStale }">last: {{ lastDeltaSec }}s</span>
        <span class="badge" :class="rpcSourceClass">RPC: {{ rpcSource }}</span>
      </div>
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
import { ensureEventBridge } from './api/rpc.tauri';

const session = useSessionStore();
const metrics = useMetricsStore();

onMounted(() => {
  if (typeof window !== 'undefined' && (window as any).__TAURI__) {
    // 在 Tauri 宿主中启动事件桥
    ensureEventBridge();
  }
  session.init();
  metrics.start();
});

// 健康状态（每秒刷新）
const tick = ref(0);
let t: any = null;
onMounted(() => { t = setInterval(() => tick.value++, 1000); });
onUnmounted(() => { if (t) clearInterval(t); });
const lastDeltaSec = computed(() => metrics.lastAt ? Math.floor((Date.now() - metrics.lastAt) / 1000) : -1);
const isStale = computed(() => lastDeltaSec.value < 0 || lastDeltaSec.value > 5);

// RPC 来源徽标（mock / tauri）
const rpcSource = computed(() => {
  // 依赖 tick 以周期性重评估环境
  void tick.value;
  const w: any = typeof window !== 'undefined' ? window : {};
  return w.__IS_TAURI__ ? 'tauri' : 'mock';
});
const rpcSourceClass = computed(() => rpcSource.value === 'tauri' ? 'ok' : 'warn');

// 探测 Tauri v2 环境：动态引入 @tauri-apps/api/core
onMounted(async () => {
  const w: any = typeof window !== 'undefined' ? window : {};
  try {
    await import('@tauri-apps/api/core');
    // 只有当能成功调用桥接（即 invoke 可用且命令可达）时，才认为是 Tauri
    await ensureEventBridge();
    w.__IS_TAURI__ = true;
  } catch {
    w.__IS_TAURI__ = false;
  }
});
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
.cards { display: grid; gap: 16px; grid-template-columns: 1fr; }
@media (min-width: 900px) {
  .cards { grid-template-columns: 1fr 1fr; }
}
</style>
