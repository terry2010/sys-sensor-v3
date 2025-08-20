<template>
  <div class="card">
    <h3>History</h3>
    <div class="row">
      <label>窗口(s): <input type="number" v-model.number="winSec" min="2" max="120" /></label>
      <label>步长(ms): <input type="number" v-model.number="stepMs" min="100" max="5000" step="100" /></label>
      <button @click="load" :disabled="loading">查询</button>
    </div>
    <div ref="chartRef" class="chart"></div>
  </div>
</template>
<script setup lang="ts">
import { ref, onMounted, watch } from 'vue';
import * as echarts from 'echarts';
import { service } from '../api/service';
import { useHistoryQueryStore } from '../stores/historyQuery';

const chartRef = ref<HTMLDivElement | null>(null);
const chart = ref<echarts.EChartsType | null>(null);
const loading = ref(false);
const error = ref<string | null>(null);
const winSec = ref(6);
const stepMs = ref(500);

const render = (data: { x: number[]; cpu: number[] }) => {
  if (!chart.value && chartRef.value) {
    chart.value = echarts.init(chartRef.value);
  }
  chart.value?.setOption({
    tooltip: { trigger: 'axis' },
    xAxis: { type: 'category', data: data.x.map(x=> new Date(x).toLocaleTimeString()) },
    yAxis: { type: 'value', min: 0, max: 100, axisLabel: { formatter: '{value} %' } },
    series: [{ type: 'line', name: 'CPU %', data: data.cpu, smooth: true }],
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
  withTimeout(service.queryHistory({ from_ts: now - winSec.value * 1000, to_ts: now, modules: ['cpu'], step_ms: stepMs.value }))
    .then((res) => {
      const x: number[] = []; const cpu: number[] = [];
      for (const it of res.items) { if ((it as any)?.cpu) { x.push((it as any).ts); cpu.push((it as any).cpu.usage_percent); } }
      if (my === reqSeq) render({ x, cpu });
    })
    .catch((e:any) => { if (my === reqSeq) error.value = e?.message || String(e); })
    .finally(() => { clearTimeout(lt); if (my === reqSeq) loading.value = false; });
};

onMounted(() => { load(); window.addEventListener('resize', ()=> chart.value?.resize()); });
watch([winSec, stepMs], () => { /* 不自动加载，避免频繁请求 */ });

// 监听历史查询结果联动
const hq = useHistoryQueryStore();
watch(() => hq.items, (arr) => {
  if (!arr || arr.length === 0) return;
  const x: number[] = []; const cpu: number[] = [];
  for (const it of arr) { if ((it as any)?.cpu) { x.push((it as any).ts); cpu.push((it as any).cpu.usage_percent); } }
  if (x.length) render({ x, cpu });
}, { deep: true });
</script>
<style scoped>
.card { border: 1px solid #ddd; border-radius: 8px; padding: 12px; }
.row { display: flex; gap: 12px; align-items: center; margin-bottom: 8px; }
.chart { width: 100%; height: 320px; }
input { width: 80px; }
.err { color: #c00; font-size: 12px; }
</style>
