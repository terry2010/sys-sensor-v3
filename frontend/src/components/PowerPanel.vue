<template>
  <div class="card">
    <h3>Power</h3>
    <div v-if="bat">
      <div class="kv"><span>State</span><span class="v">{{ bat.state ?? '—' }}</span></div>
      <div class="kv"><span>Percentage</span><span class="v">{{ fmtPct(bat.percentage) }}</span></div>
      <div class="kv"><span>AC Line</span><span class="v">{{ bat.ac_line_online ? 'Online' : 'Offline' }}</span></div>
      <div class="kv"><span>Time Remaining</span><span class="v">{{ fmtMin(bat.time_remaining_min) }}</span></div>
      <div class="kv"><span>Time To Full</span><span class="v">{{ fmtMin(bat.time_to_full_min) }}</span></div>
      <div class="kv"><span>Time On Battery</span><span class="v">{{ fmtSec(bat.time_on_battery_sec) }}</span></div>
    </div>
    <div v-else>
      —
    </div>
    <details class="json-box" v-if="power">
      <summary>power JSON</summary>
      <pre><code>{{ powerJson }}</code></pre>
    </details>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useMetricsStore } from '../stores/metrics';

const metrics = useMetricsStore();
const power = computed<any>(() => (metrics.latest as any)?.power ?? null);
const bat = computed<any>(() => power.value?.battery ?? null);
const powerJson = computed(() => { try { return JSON.stringify(power.value, null, 2); } catch { return '{}'; } });

const fmtPct = (v: any) => typeof v === 'number' && isFinite(v) ? `${v.toFixed(1)}%` : '—';
const fmtMin = (v: any) => typeof v === 'number' && isFinite(v) ? `${Math.max(0, Math.round(v))} min` : '—';
const fmtSec = (v: any) => typeof v === 'number' && isFinite(v) ? `${Math.max(0, Math.round(v))} s` : '—';
</script>

<style scoped>
.kv { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; padding: 4px 0; border-bottom: 1px dashed #f0f0f0; font-size: 12px; }
.v { text-align: right; font-weight: 600; }
.json-box { margin-top: 8px; }
.json-box pre { max-height: 240px; overflow: auto; background: #0b1020; color: #cde2ff; padding: 8px; border-radius: 6px; }
</style>
