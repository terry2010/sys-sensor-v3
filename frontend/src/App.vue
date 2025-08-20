<template>
  <div class="container">
    <header>
      <h1>SysSensorV3 DebugView</h1>
      <p class="sub">Tauri + Vue（当前为 Web Mock 运行，后续接入命名管道）</p>
    </header>

    <section class="cards">
      <SnapshotPanel />
      <HistoryChart />
    </section>
  </div>
</template>

<script setup lang="ts">
import { onMounted } from 'vue';
import SnapshotPanel from './components/SnapshotPanel.vue';
import HistoryChart from './components/HistoryChart.vue';
import { useSessionStore } from './stores/session';
import { useMetricsStore } from './stores/metrics';

const session = useSessionStore();
const metrics = useMetricsStore();

onMounted(() => {
  session.init();
  metrics.start();
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
.cards { display: grid; gap: 16px; grid-template-columns: 1fr; }
@media (min-width: 900px) {
  .cards { grid-template-columns: 1fr 1fr; }
}
</style>
