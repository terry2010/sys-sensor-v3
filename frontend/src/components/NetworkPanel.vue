<template>
  <div class="card">
    <h3>Network</h3>
    <div class="sub">实时网络速率（总览与每接口）</div>
    <div v-if="totals">
      <div class="kv"><span class="k">rx_bytes_per_sec</span><span class="v">{{ fmtBps(totals.rx_bytes_per_sec) }}</span></div>
      <div class="kv"><span class="k">tx_bytes_per_sec</span><span class="v">{{ fmtBps(totals.tx_bytes_per_sec) }}</span></div>
    </div>

    <details class="json-box">
      <summary>per_interface_io</summary>
      <div class="table">
        <div class="thead">
          <span class="c if">if_id</span>
          <span class="c name">name</span>
          <span class="c rx">rx_bytes_per_sec</span>
          <span class="c tx">tx_bytes_per_sec</span>
        </div>
        <div class="row" v-for="(it, idx) in list" :key="idx">
          <span class="c if">{{ it.if_id }}</span>
          <span class="c name">{{ it.name }}</span>
          <span class="c rx">{{ fmtBps(it.rx_bytes_per_sec) }}</span>
          <span class="c tx">{{ fmtBps(it.tx_bytes_per_sec) }}</span>
        </div>
      </div>
      <pre><code>{{ netJson }}</code></pre>
    </details>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useMetricsStore } from '../stores/metrics';

const metrics = useMetricsStore();
const net = computed<any>(() => (metrics.latest as any)?.network || null);
const totals = computed<any>(() => net.value?.io_totals || null);
const list = computed<any[]>(() => Array.isArray(net.value?.per_interface_io) ? net.value.per_interface_io : []);
const netJson = computed<string>(() => { try { return JSON.stringify(net.value || {}, null, 2); } catch { return '{}'; } });

const fmtBps = (v: any) => {
  if (v === null || v === undefined) return '-';
  if (typeof v !== 'number' || !isFinite(v)) return '-';
  // 简易单位换算
  if (v >= 1024 * 1024) return (v / (1024 * 1024)).toFixed(2) + ' MB/s';
  if (v >= 1024) return (v / 1024).toFixed(1) + ' KB/s';
  return v.toFixed(0) + ' B/s';
};
</script>

<style scoped>
.sub { color: #666; font-size: 12px; margin-bottom: 6px; }
.kv { display: flex; justify-content: space-between; font-size: 13px; }
.k { color: #555; }
.v { color: #222; font-weight: 600; }
.table { margin-top: 8px; border-top: 1px dashed #eee; }
.thead, .row { display: grid; grid-template-columns: 190px 1fr 150px 150px; gap: 8px; padding: 6px 0; border-bottom: 1px dashed #f0f0f0; font-size: 12px; }
.thead { color: #666; font-weight: 600; }
.c { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.json-box pre { max-height: 240px; overflow: auto; background: #0b1020; color: #cde2ff; padding: 8px; border-radius: 6px; margin-top: 8px; }
</style>
