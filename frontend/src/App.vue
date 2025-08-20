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
import { ensureEventBridge } from './api/rpc.tauri';

const session = useSessionStore();
const metrics = useMetricsStore();

onMounted(async () => {
  // 统一初始化顺序：先探测并尝试启动事件桥 -> 再进行会话与 metrics 订阅
  const w: any = typeof window !== 'undefined' ? window : {};
  try {
    await import('@tauri-apps/api/core');
    // 非阻塞：不要等待事件桥建立，避免后端异常导致 UI 等待
    void ensureEventBridge();
    w.__IS_TAURI__ = true;
  } catch {
    w.__IS_TAURI__ = false;
  }
  // 非阻塞：优先启动 metrics，session.init() 不阻塞主流程
  metrics.start();
  void session.init();
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
