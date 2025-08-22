<template>
  <div class="card">
    <h3>Memory</h3>
    <div class="sub">实时内存使用与细分指标</div>
    <div class="grid">
      <div class="kv"><span class="k">total</span><span class="v">{{ fmtMb(mem.total_mb) }}</span></div>
      <div class="kv"><span class="k">used</span><span class="v warn">{{ fmtMb(mem.used_mb) }}</span></div>
      <div class="kv"><span class="k">available</span><span class="v">{{ fmtMb(mem.available_mb) }}</span></div>
      <div class="kv"><span class="k">percent_used</span><span class="v warn">{{ fmtPct(mem.percent_used) }}</span></div>

      <div class="kv"><span class="k">cached_mb</span><span class="v">{{ fmtMb(mem.cached_mb) }}</span></div>
      <div class="kv"><span class="k">commit_used/limit</span><span class="v">{{ fmtMb(mem.commit_used_mb) }} / {{ fmtMb(mem.commit_limit_mb) }}</span></div>
      <div class="kv"><span class="k">commit_percent</span><span class="v">{{ fmtPct(mem.commit_percent) }}</span></div>

      <div class="kv"><span class="k">swap_used/total</span><span class="v">{{ fmtMb(mem.swap_used_mb) }} / {{ fmtMb(mem.swap_total_mb) }}</span></div>

      <div class="kv"><span class="k">pages_in/sec</span><span class="v">{{ fmtNum(mem.pages_in_per_sec) }}</span></div>
      <div class="kv"><span class="k">pages_out/sec</span><span class="v">{{ fmtNum(mem.pages_out_per_sec) }}</span></div>
      <div class="kv"><span class="k">page_faults/sec</span><span class="v">{{ fmtNum(mem.page_faults_per_sec) }}</span></div>

      <div class="kv"><span class="k">compressed_mb</span><span class="v">{{ fmtMb(mem.compressed_bytes_mb) }}</span></div>
      <div class="kv"><span class="k">pool(paged/nonpaged)</span><span class="v">{{ fmtMb(mem.pool_paged_mb) }} / {{ fmtMb(mem.pool_nonpaged_mb) }}</span></div>
      <div class="kv"><span class="k">standby_cache_mb</span><span class="v">{{ fmtMb(mem.standby_cache_mb) }}</span></div>
      <div class="kv"><span class="k">working_set_total_mb</span><span class="v">{{ fmtMb(mem.working_set_total_mb) }}</span></div>

      <div class="kv"><span class="k">memory_pressure</span><span class="v" :class="pressureClass">{{ fmtPct(mem.memory_pressure_percent) }} ({{ mem.memory_pressure_level || '-' }})</span></div>
    </div>

    <details class="json-box">
      <summary>JSON 详情</summary>
      <pre><code>{{ memJson }}</code></pre>
    </details>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useMetricsStore } from '../stores/metrics';

const metrics = useMetricsStore();
const mem = computed<any>(() => (metrics.latest as any)?.memory || {});
const memJson = computed(() => {
  try { return JSON.stringify(mem.value || {}, null, 2); } catch { return '{}'; }
});

const fmtMb = (v: any) => typeof v === 'number' && isFinite(v) ? `${Math.round(v)} MB` : '-';
const fmtPct = (v: any) => typeof v === 'number' && isFinite(v) ? `${v.toFixed(1)}%` : '-';
const fmtNum = (v: any) => typeof v === 'number' && isFinite(v) ? v.toFixed(2) : '-';
const pressureClass = computed(() => {
  const lvl = String(mem.value?.memory_pressure_level || '').toLowerCase();
  return lvl === 'red' ? 'bad' : (lvl === 'yellow' ? 'warn' : 'ok');
});
</script>

<style scoped>
.sub { color: #666; font-size: 12px; margin-bottom: 6px; }
.grid { display: grid; grid-template-columns: 1fr 1fr; gap: 6px 12px; }
.kv { display: flex; justify-content: space-between; font-size: 13px; }
.k { color: #555; }
.v { color: #222; font-weight: 600; }
.v.warn { color: #b36200; }
.v.bad { color: #b00020; }
.json-box { margin-top: 8px; }
.json-box pre { max-height: 260px; overflow: auto; background: #0b1020; color: #cde2ff; padding: 8px; border-radius: 6px; }
</style>
