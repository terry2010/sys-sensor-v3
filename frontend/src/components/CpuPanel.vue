<template>
  <div class="card">
    <h3>CPU</h3>
    <div class="rows">
      <div class="line">
        <span class="k">usage</span>
        <span class="v">{{ pct(cpu?.usage_percent) }}</span>
      </div>
      <div class="bars">
        <div class="bar user" :style="{ width: pctNum(cpu?.user_percent) + '%' }"></div>
        <div class="bar system" :style="{ width: pctNum(cpu?.system_percent) + '%' }"></div>
        <div class="bar idle" :style="{ width: pctNum(cpu?.idle_percent) + '%' }"></div>
      </div>
      <div class="line small">
        <span class="k">user/system/idle</span>
        <span class="v">{{ pct(cpu?.user_percent) }} / {{ pct(cpu?.system_percent) }} / {{ pct(cpu?.idle_percent) }}</span>
      </div>
      <div class="line small">
        <span class="k">uptime</span>
        <span class="v">{{ uptime(cpu?.uptime_sec) }}</span>
      </div>
      <div class="grid small">
        <div><span class="k">load 1/5/15</span><span class="v">{{ pct(cpu?.load_avg_1m) }} / {{ pct(cpu?.load_avg_5m) }} / {{ pct(cpu?.load_avg_15m) }}</span></div>
        <div><span class="k">proc/thread</span><span class="v">{{ num(cpu?.process_count) }} / {{ num(cpu?.thread_count) }}</span></div>
        <div><span class="k">freq</span><span class="v">{{ mhz(cpu?.current_mhz) }} / {{ mhz(cpu?.max_mhz) }}</span></div>
        <div v-if="hasKernelCounters"><span class="k">ctx/sys/irq</span><span class="v">{{ perSec(cpu?.context_switches_per_sec) }} / {{ perSec(cpu?.syscalls_per_sec) }} / {{ perSec(cpu?.interrupts_per_sec) }}</span></div>
        <div v-if="cpu && (cpu as any).bus_mhz"><span class="k">bus/mult</span><span class="v">{{ mhz((cpu as any).bus_mhz) }} / {{ mult((cpu as any).multiplier) }}</span></div>
        <div v-if="cpu && (cpu as any).min_mhz"><span class="k">min freq</span><span class="v">{{ mhz((cpu as any).min_mhz) }}</span></div>
        <div v-if="cpu && (cpu as any).package_temp_c != null"><span class="k">pkg temp</span><span class="v">{{ degC((cpu as any).package_temp_c) }}</span></div>
      </div>
      <div v-if="Array.isArray(cpu?.per_core) && cpu!.per_core.length" class="per-core">
        <div class="pc-head">per-core (usage / MHz)</div>
        <div class="pc-list">
          <div v-for="(v, idx) in cpu!.per_core" :key="idx" class="pc-row">
            <span class="pc-idx">#{{ idx }}</span>
            <div class="pc-bar"><div class="pc-fill" :style="{ width: pctNum(v) + '%' }"></div></div>
            <span class="pc-val">{{ pct(v) }}</span>
            <span class="pc-mhz" v-if="hasPerCoreMhz">{{ mhz((cpu as any).per_core_mhz?.[idx]) }}</span>
          </div>
        </div>
      </div>
      <div v-if="Array.isArray(cpu?.top_processes) && cpu!.top_processes.length" class="top-proc">
        <div class="tp-head">top processes</div>
        <div class="tp-list">
          <div v-for="(p, i) in cpu!.top_processes" :key="p.pid ?? i" class="tp-row">
            <span class="tp-name" :title="p.name">{{ p.name || '(unknown)' }}</span>
            <span class="tp-cpu">{{ pct(p.cpu_percent) }}</span>
            <span class="tp-pid">PID {{ p.pid }}</span>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
<script setup lang="ts">
import { computed } from 'vue';
import { useMetricsStore } from '../stores/metrics';

const metrics = useMetricsStore();
const cpu = computed(() => (metrics.latest as any)?.cpu ?? null);

const pctNum = (v: any) => typeof v === 'number' ? Math.max(0, Math.min(100, v)) : 0;
const pct = (v: any) => typeof v === 'number' ? `${v.toFixed(1)}%` : '-';
const num = (v: any) => typeof v === 'number' ? v : (typeof v === 'string' ? v : '-');
const mhz = (v: any) => {
  if (v == null || typeof v !== 'number') return '-';
  return `${Math.round(v).toLocaleString()} MHz`;
};
const perSec = (v: any) => {
  if (v == null || typeof v !== 'number' || !isFinite(v)) return '-';
  const n = Math.max(0, v);
  if (n >= 1_000_000) return `${(n/1_000_000).toFixed(2)}M/s`;
  if (n >= 1_000) return `${(n/1_000).toFixed(1)}k/s`;
  return `${Math.round(n)}/s`;
};
const mult = (v: any) => {
  if (v == null || typeof v !== 'number' || !isFinite(v)) return '-';
  return `${v.toFixed(2)}×`;
};
const degC = (v: any) => {
  if (v == null || typeof v !== 'number' || !isFinite(v)) return '-';
  return `${v.toFixed(1)} °C`;
};
const hasKernelCounters = computed(() => {
  const c = cpu.value as any;
  return c && (typeof c.context_switches_per_sec === 'number' || typeof c.syscalls_per_sec === 'number' || typeof c.interrupts_per_sec === 'number');
});
const hasPerCoreMhz = computed(() => Array.isArray((cpu.value as any)?.per_core_mhz));
const uptime = (sec: any) => {
  const s = typeof sec === 'number' ? Math.max(0, Math.floor(sec)) : null;
  if (s == null) return '-';
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const ss = s % 60;
  const pad = (n: number) => n < 10 ? `0${n}` : `${n}`;
  return `${pad(h)}:${pad(m)}:${pad(ss)}`;
};
</script>
<style scoped>
.card { border: 1px solid #ddd; border-radius: 8px; padding: 12px; }
.rows { display: flex; flex-direction: column; gap: 8px; }
.line { display: flex; justify-content: space-between; align-items: center; }
.line .k { color: #666; }
.line .v { font-weight: 600; }
.small { font-size: 12px; color: #444; }
.bars { display: flex; height: 10px; background: #f0f0f0; border-radius: 6px; overflow: hidden; }
.bar { height: 100%; }
.user { background: #4caf50; }
.system { background: #ff9800; }
.idle { background: #9e9e9e; }
.grid { display: grid; grid-template-columns: 1fr 1fr; gap: 6px 12px; }
@media (max-width: 800px) { .grid { grid-template-columns: 1fr; } }
/* per-core */
.per-core { margin-top: 6px; }
.pc-head { font-size: 12px; color: #666; margin-bottom: 4px; }
.pc-list { display: flex; flex-direction: column; gap: 4px; max-height: 180px; overflow: auto; }
.pc-row { display: grid; grid-template-columns: 36px 1fr 56px 84px; align-items: center; gap: 8px; font-size: 12px; }
.pc-idx { color: #888; text-align: right; }
.pc-bar { height: 8px; background: #f0f0f0; border-radius: 6px; overflow: hidden; }
.pc-fill { height: 100%; background: linear-gradient(90deg, #4caf50, #ff9800); }
.pc-val { text-align: right; font-variant-numeric: tabular-nums; }
.pc-mhz { text-align: right; white-space: nowrap; font-variant-numeric: tabular-nums; color: #333; }
/* top processes */
.top-proc { margin-top: 8px; }
.tp-head { font-size: 12px; color: #666; margin-bottom: 4px; }
.tp-list { display: flex; flex-direction: column; gap: 4px; max-height: 140px; overflow: auto; }
.tp-row { display: grid; grid-template-columns: 1fr 64px 80px; align-items: center; font-size: 12px; }
.tp-name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: #333; }
.tp-cpu { text-align: right; font-variant-numeric: tabular-nums; color: #111; }
.tp-pid { text-align: right; color: #777; }
</style>
