<template>
  <div class="card">
    <h3>GPU</h3>
    <div class="sub">Windows 性能计数器 + LHM（若可用）</div>
    <div class="kv"><span>Active</span><span>{{ activeName || '—' }}</span></div>
    <div class="kv"><span>Temperature (°C)</span><span>{{ fmt1(temperatureC) }}</span></div>
    <table class="tbl" v-if="adapters.length">
      <thead>
        <tr>
          <th>#</th>
          <th>Name</th>
          <th class="num">Usage %</th>
          <th class="num">Used (MB)</th>
          <th class="num">Total (MB)</th>
          <th class="num">Ded Used</th>
          <th class="num">Ded Total</th>
          <th class="num">Sha Used</th>
          <th class="num">Sha Total</th>
          <th class="num">DXGI Local Budget</th>
          <th class="num">DXGI Local Used</th>
          <th class="num">DXGI NonLocal Budget</th>
          <th class="num">DXGI NonLocal Used</th>
          <th class="num">3D%</th>
          <th class="num">Compute%</th>
          <th class="num">Copy%</th>
          <th class="num">VDec%</th>
          <th class="num">VEnc%</th>
          <th class="num">Core MHz</th>
          <th class="num">Mem MHz</th>
          <th class="num">Power W</th>
          <th class="num">Fan RPM</th>
          <th class="num">MemCtrl%</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="(a, i) in adapters" :key="i" :class="{ active: i === activeIndex }">
          <td>{{ i }}</td>
          <td>{{ a.name || a.key || `adapter_${i}` }}</td>
          <td class="num">{{ fmt1(a.usage_percent) }}</td>
          <td class="num">{{ fmtMB(sumMB(a.vram_dedicated_used_mb, a.vram_shared_used_mb)) }}</td>
          <td class="num">{{ fmtMB(sumMB(a.vram_dedicated_total_mb, a.vram_shared_total_mb)) }}</td>
          <td class="num">{{ fmtMB(a.vram_dedicated_used_mb) }}</td>
          <td class="num">{{ fmtMB(a.vram_dedicated_total_mb) }}</td>
          <td class="num">{{ fmtMB(a.vram_shared_used_mb) }}</td>
          <td class="num">{{ fmtMB(a.vram_shared_total_mb) }}</td>
          <td class="num">{{ fmtMB(a.dxgi_local_budget_mb) }}</td>
          <td class="num">{{ fmtMB(a.dxgi_local_usage_mb) }}</td>
          <td class="num">{{ fmtMB(a.dxgi_nonlocal_budget_mb) }}</td>
          <td class="num">{{ fmtMB(a.dxgi_nonlocal_usage_mb) }}</td>
          <td class="num">{{ fmt1(a.usage_by_engine_percent?.['3d']) }}</td>
          <td class="num">{{ fmt1(a.usage_by_engine_percent?.compute) }}</td>
          <td class="num">{{ fmt1(a.usage_by_engine_percent?.copy) }}</td>
          <td class="num">{{ fmt1(a.usage_by_engine_percent?.video_decode ?? a.video_decode_util_percent) }}</td>
          <td class="num">{{ fmt1(a.usage_by_engine_percent?.video_encode ?? a.video_encode_util_percent) }}</td>
          <td class="num">{{ fmt1(a.core_clock_mhz) }}</td>
          <td class="num">{{ fmt1(a.memory_clock_mhz) }}</td>
          <td class="num">{{ fmt1(a.power_draw_w) }}</td>
          <td class="num">{{ fmt1(a.fan_rpm) }}</td>
          <td class="num">{{ fmt1(a.mem_controller_load_percent) }}</td>
        </tr>
      </tbody>
    </table>
    <div v-else>—</div>

    <div class="kv" style="margin-top:8px"><span>Top Processes</span><span></span></div>
    <table class="tbl" v-if="topProcesses.length">
      <thead>
        <tr>
          <th class="num">PID</th>
          <th>Name</th>
          <th class="num">Usage %</th>
          <th class="num">VDec%</th>
          <th class="num">VEnc%</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="p in topProcesses" :key="p.pid">
          <td class="num">{{ p.pid }}</td>
          <td>{{ p.name || '—' }}</td>
          <td class="num">{{ fmt1(p.usage_percent) }}</td>
          <td class="num">{{ fmt1(p.video_decode_util_percent) }}</td>
          <td class="num">{{ fmt1(p.video_encode_util_percent) }}</td>
        </tr>
      </tbody>
    </table>
    <div v-else>—</div>

    <div class="kv" style="margin-top:8px"><span>Active Adapter (Static)</span><span></span></div>
    <div v-if="activeStatic || activeAdapter">
      <div class="kv"><span>Name</span><span>{{ activeName || activeAdapter?.name || '—' }}</span></div>
      <div class="kv"><span>VendorId</span><span>{{ fmtHex(activeAdapter?.vendor_id) }}</span></div>
      <div class="kv"><span>DeviceId</span><span>{{ fmtHex(activeAdapter?.device_id) }}</span></div>
      <div class="kv"><span>SubSysId</span><span>{{ fmtHex(activeAdapter?.subsys_id) }}</span></div>
      <div class="kv"><span>Revision</span><span>{{ fmtHex(activeAdapter?.revision) }}</span></div>
      <div class="kv"><span>DXGI Flags</span><span>{{ fmtHex(activeAdapter?.dxgi_flags) }}</span></div>
      <div class="kv"><span>Driver Version</span><span>{{ activeStatic?.driver_version ?? '—' }}</span></div>
      <div class="kv"><span>Driver Date</span><span>{{ activeStatic?.driver_date ?? '—' }}</span></div>
      <div class="kv"><span>PNP Device ID</span><span>{{ activeStatic?.pnp_device_id ?? '—' }}</span></div>
      <div class="kv"><span>Adapter Compatibility</span><span>{{ activeStatic?.adapter_compatibility ?? '—' }}</span></div>
    </div>
    <div v-else>—</div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useMetricsStore } from '../stores/metrics';

const metrics = useMetricsStore();
const gpu = computed<any>(() => (metrics.latest as any)?.gpu ?? null);
const adapters = computed<any[]>(() => {
  const arr = (gpu.value?.adapters as any[]) || [];
  return Array.isArray(arr) ? arr : [];
});
const topProcesses = computed<any[]>(() => {
  const arr = (gpu.value?.top_processes as any[]) || [];
  return Array.isArray(arr) ? arr : [];
});
const activeIndex = computed<number>(() => {
  const n = gpu.value?.active_adapter_index;
  return typeof n === 'number' && n >= 0 ? n : -1;
});
const activeName = computed<string | null>(() => gpu.value?.active_adapter_name || null);
const activeAdapter = computed<any | null>(() => {
  const i = activeIndex.value;
  const arr = adapters.value;
  return i >= 0 && i < arr.length ? arr[i] : null;
});
const activeStatic = computed<any | null>(() => gpu.value?.active_adapter_static || null);

const fmt1 = (v: any) => (typeof v === 'number' && isFinite(v)) ? v.toFixed(1) : (v == null ? '—' : String(v));
const fmtMB = (v: any) => (typeof v === 'number' && isFinite(v)) ? `${Math.max(0, Math.round(v))} MB` : (v == null ? '—' : String(v));
const sumMB = (a: any, b: any) => {
  const x = (typeof a === 'number' && isFinite(a)) ? a : 0;
  const y = (typeof b === 'number' && isFinite(b)) ? b : 0;
  return x + y;
};
const fmtHex = (v: any) => (typeof v === 'number' && isFinite(v)) ? `0x${v.toString(16).toUpperCase()}` : (v == null ? '—' : String(v));

// temperature: 优先活跃适配器的 core 温度，否则取所有适配器最大值
const temperatureC = computed<number | null>(() => {
  const arr = adapters.value;
  if (!arr.length) return null;
  const idx = activeIndex.value;
  if (idx >= 0 && idx < arr.length) {
    const v = arr[idx]?.temperature_core_c;
    if (typeof v === 'number') return v;
  }
  let max: number | null = null;
  for (const a of arr) {
    const v = a?.temperature_core_c;
    if (typeof v === 'number') max = max == null ? v : Math.max(max, v);
  }
  return max;
});
</script>

<style scoped>
.sub { color: #666; font-size: 12px; }
.kv { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; padding: 4px 0; border-bottom: 1px dashed #f0f0f0; font-size: 12px; }
.tbl { width: 100%; border-collapse: collapse; font-size: 12px; margin-top: 8px; }
.tbl th, .tbl td { border-bottom: 1px dashed #eee; padding: 6px 8px; text-align: left; }
.tbl .num { text-align: right; font-variant-numeric: tabular-nums; }
tr.active td { background: #f6ffed; }
</style>
