<template>
  <div class="card">
    <h3>History</h3>
    <div class="row">
      <label>窗口(s): <input type="number" v-model.number="winSec" min="2" max="120" /></label>
      <label>步长(ms): <input type="number" v-model.number="stepMs" min="100" max="5000" step="100" /></label>
      <label><input type="checkbox" v-model="autoRefresh" /> 自动刷新</label>
      <label v-if="autoRefresh">间隔(s): <input type="number" v-model.number="refreshSec" min="2" max="60" /></label>
      <button @click="load" :disabled="loading">查询</button>
    </div>
    <div ref="chartRef" class="chart"></div>
    <div class="err" v-if="error">{{ error }}</div>
  </div>
</template>
<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch } from 'vue';
import * as echarts from 'echarts';
import { service } from '../api/service';
import { useHistoryQueryStore } from '../stores/historyQuery';

const chartRef = ref<HTMLDivElement | null>(null);
const chart = ref<echarts.EChartsType | null>(null);
const loading = ref(false);
const error = ref<string | null>(null);
const winSec = ref(6);
const stepMs = ref(500);
const autoRefresh = ref(true);
const refreshSec = ref(5);

const render = (data: { x: number[]; cpu: number[]; mem: number[] }) => {
  if (!chart.value && chartRef.value) {
    chart.value = echarts.init(chartRef.value);
  }
  chart.value?.setOption({
    tooltip: { trigger: 'axis' },
    xAxis: { type: 'category', data: data.x.map(x=> new Date(x).toLocaleTimeString()) },
    yAxis: [
      { type: 'value', min: 0, max: 100, axisLabel: { formatter: '{value} %' }, name: 'CPU %' },
      { type: 'value', min: 0, max: 100, axisLabel: { formatter: '{value} %' }, name: 'Memory %' },
    ],
    series: [
      { type: 'line', name: 'CPU %', data: data.cpu, smooth: true, yAxisIndex: 0 },
      { type: 'line', name: 'Memory %', data: data.mem, smooth: true, yAxisIndex: 1 },
    ],
    grid: { left: 40, right: 20, bottom: 30, top: 30 }
  });
};

let reqSeq = 0;
const withTimeout = async <T>(p: Promise<T>, ms = 6000): Promise<T> => {
  let timer: any; const t = new Promise<never>((_, rej) => { timer = setTimeout(() => rej(new Error('chart query timeout')), ms); });
  try { return await Promise.race([p, t]) as T; } finally { if (timer) clearTimeout(timer); }
};
const load = () => {
  const my = ++reqSeq; error.value = null;
  loading.value = true; const lt = setTimeout(() => { if (my === reqSeq) loading.value = false; }, 800);
  const now = Date.now();
  withTimeout(service.queryHistory({ from_ts: now - winSec.value * 1000, to_ts: now, modules: ['cpu','memory'], step_ms: stepMs.value }))
    .then((res) => {
      const x: number[] = []; const cpu: number[] = []; const mem: number[] = [];
      for (const it of res.items) {
        const ts = (it as any)?.ts; if (!ts) continue;
        x.push(ts);
        const c = (it as any)?.cpu?.usage_percent; cpu.push(typeof c === 'number' ? c : null as any);
        const m = (it as any)?.memory; let mp = null as any;
        if (m && typeof m.total === 'number' && typeof m.used === 'number' && m.total > 0) {
          mp = Math.min(100, Math.max(0, Math.round((m.used / m.total) * 10000) / 100));
        }
        mem.push(mp);
      }
      if (my === reqSeq) render({ x, cpu, mem });
    })
    .catch((e:any) => { if (my === reqSeq) error.value = e?.message || String(e); })
    .finally(() => { clearTimeout(lt); if (my === reqSeq) loading.value = false; });
};

let autoTimer: any = null;
const startTimer = () => {
  if (!autoRefresh.value) return;
  if (autoTimer) clearInterval(autoTimer);
  autoTimer = setInterval(() => load(), Math.max(2000, (refreshSec.value || 5) * 1000));
};

onMounted(() => {
  load();
  window.addEventListener('resize', ()=> chart.value?.resize());
  startTimer();
});
onUnmounted(() => { if (autoTimer) clearInterval(autoTimer); });
watch([winSec, stepMs], () => { /* 不自动加载，避免频繁请求 */ });
watch([autoRefresh, refreshSec], () => { if (autoTimer) clearInterval(autoTimer); startTimer(); });

// 监听历史查询结果联动
const hq = useHistoryQueryStore();
watch(() => hq.items, (arr) => {
  if (!arr || arr.length === 0) return;
  const x: number[] = []; const cpu: number[] = []; const mem: number[] = [];
  for (const it of arr) {
    const ts = (it as any)?.ts; if (!ts) continue;
    x.push(ts);
    const c = (it as any)?.cpu?.usage_percent; cpu.push(typeof c === 'number' ? c : null as any);
    const m = (it as any)?.memory; let mp = null as any;
    if (m && typeof m.total === 'number' && typeof m.used === 'number' && m.total > 0) {
      mp = Math.min(100, Math.max(0, Math.round((m.used / m.total) * 10000) / 100));
    }
    mem.push(mp);
  }
  if (x.length) render({ x, cpu, mem });
}, { deep: true });
</script>
<style scoped>
.card { border: 1px solid #ddd; border-radius: 8px; padding: 12px; }
.row { display: flex; gap: 12px; align-items: center; margin-bottom: 8px; }
.chart { width: 100%; height: 320px; }
input { width: 80px; }
.err { color: #c00; font-size: 12px; }
</style>
