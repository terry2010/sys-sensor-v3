<template>
  <div class="card">
    <h3>GPU</h3>
    <div class="sub">Windows 性能计数器 + LHM（若可用）</div>
    <div class="kv"><span>Active</span><span>{{ activeName || '—' }}</span></div>
    <div class="kv"><span>Temperature (°C)</span><span>{{ fmt1(temperatureC) }}</span></div>
    <div class="kv"><span>GPU Usage</span><span>{{ fmt1(overallGpuUsage) }}</span></div>
    <div class="kv"><span>Memory Usage</span><span>
      <template v-if="overallMemTotalMB > 0">
        {{ fmtMB(overallMemUsedMB) }} / {{ fmtMB(overallMemTotalMB) }} ({{ fmt1(overallMemPercent) }})
      </template>
      <template v-else>
        {{ fmtMB(overallMemUsedMB) }}
      </template>
    </span></div>
    <div v-if="adapters.length" class="list">
      <div
        v-for="(a, i) in adapters"
        :key="i"
        class="item"
        :class="{ active: i === activeIndex }"
      >
        <div class="row head">
          <div class="left ell">#{{ i }} · {{ a.name || a.key || `adapter_${i}` }}</div>
          <div class="right">{{ fmt1(a.usage_percent) }}%</div>
        </div>
        <div class="row">
          <div class="col">
            <div class="kv"><span>VRAM Used</span><span>{{ fmtMB(sumMB(a.vram_dedicated_used_mb, a.vram_shared_used_mb)) }}</span></div>
            <div class="kv"><span>VRAM Total</span><span>{{ fmtMB(sumMB(a.vram_dedicated_total_mb, a.vram_shared_total_mb)) }}</span></div>
            <div class="kv"><span>Dedicated</span><span>{{ fmtMB(a.vram_dedicated_used_mb) }} / {{ fmtMB(a.vram_dedicated_total_mb) }}</span></div>
            <div class="kv"><span>Shared</span><span>{{ fmtMB(a.vram_shared_used_mb) }} / {{ fmtMB(a.vram_shared_total_mb) }}</span></div>
          </div>
          <div class="col">
            <div class="kv"><span>DXGI Local</span><span>{{ fmtMB(a.dxgi_local_usage_mb) }} / {{ fmtMB(a.dxgi_local_budget_mb) }}</span></div>
            <div class="kv"><span>DXGI NonLocal</span><span>{{ fmtMB(a.dxgi_nonlocal_usage_mb) }} / {{ fmtMB(a.dxgi_nonlocal_budget_mb) }}</span></div>
            <div class="kv"><span>Core/Mem MHz</span><span>{{ fmt1(a.core_clock_mhz) }} / {{ fmt1(a.memory_clock_mhz) }}</span></div>
            <div class="kv"><span>Power/Fan</span><span>{{ fmt1(a.power_draw_w) }} W / {{ fmt1(a.fan_rpm) }} RPM</span></div>
          </div>
          <div class="col">
            <div class="kv"><span>3D</span><span>{{ fmt1(a.usage_by_engine_percent?.['3d']) }}%</span></div>
            <div class="kv"><span>Compute/Copy</span><span>{{ fmt1(a.usage_by_engine_percent?.compute) }}% / {{ fmt1(a.usage_by_engine_percent?.copy) }}%</span></div>
            <div class="kv"><span>VDec/VEnc</span><span>{{ fmt1(a.usage_by_engine_percent?.video_decode ?? a.video_decode_util_percent) }}% / {{ fmt1(a.usage_by_engine_percent?.video_encode ?? a.video_encode_util_percent) }}%</span></div>
            <div class="kv"><span>MemCtrl</span><span>{{ fmt1(a.mem_controller_load_percent) }}%</span></div>
          </div>
        </div>
      </div>
    </div>
    <div v-else>—</div>

    <div class="kv" style="margin-top:8px"><span>Top Processes</span><span></span></div>
    <div v-if="topProcesses.length" class="list compact">
      <div v-for="p in topProcesses" :key="p.pid" class="item">
        <div class="row head">
          <div class="left ell">{{ p.name || '—' }}</div>
          <div class="right mono">PID {{ p.pid }}</div>
        </div>
        <div class="row cols">
          <div class="mini">GPU {{ fmt1(p.usage_percent) }}%</div>
          <div class="mini">VDec {{ fmt1(p.video_decode_util_percent) }}%</div>
          <div class="mini">VEnc {{ fmt1(p.video_encode_util_percent) }}%</div>
        </div>
      </div>
    </div>
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

    <details style="margin-top:12px">
      <summary style="cursor:pointer">gpu_raw JSON（调试）</summary>
      <pre style="max-height:240px; overflow:auto; background:#fafafa; padding:8px; border:1px solid #eee; border-radius:4px">{{ prettyJson(gpuRaw) }}</pre>
    </details>
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
const gpuRaw = computed<any | null>(() => (metrics.latest as any)?.gpu_raw ?? null);

const fmt1 = (v: any) => (typeof v === 'number' && isFinite(v)) ? v.toFixed(1) : (v == null ? '—' : String(v));
const fmtMB = (v: any) => (typeof v === 'number' && isFinite(v)) ? `${Math.max(0, Math.round(v))} MB` : (v == null ? '—' : String(v));
const sumMB = (a: any, b: any) => {
  const x = (typeof a === 'number' && isFinite(a)) ? a : 0;
  const y = (typeof b === 'number' && isFinite(b)) ? b : 0;
  return x + y;
};
const fmtHex = (v: any) => (typeof v === 'number' && isFinite(v)) ? `0x${v.toString(16).toUpperCase()}` : (v == null ? '—' : String(v));
const prettyJson = (v: any) => {
  try { return v == null ? '—' : JSON.stringify(v, null, 2); } catch { return String(v); }
};

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

// 总体 GPU 占用率：优先活跃适配器 usage_percent；否则取全局最大值
const overallGpuUsage = computed<number | null>(() => {
  const arr = adapters.value;
  if (!arr.length) return null;
  const act = activeAdapter.value;
  const v = act?.usage_percent;
  if (typeof v === 'number' && isFinite(v)) return v;
  let max: number | null = null;
  for (const a of arr) {
    const u = a?.usage_percent;
    if (typeof u === 'number' && isFinite(u)) max = max == null ? u : Math.max(max, u);
  }
  return max;
});

// 总体显存占用：活跃适配器 dedicated+shared 的已用/总量
const overallMemUsedMB = computed<number>(() => {
  const a = activeAdapter.value;
  const used = sumMB(a?.vram_dedicated_used_mb, a?.vram_shared_used_mb);
  return used;
});
const overallMemTotalMB = computed<number>(() => {
  const a = activeAdapter.value;
  const tot = sumMB(a?.vram_dedicated_total_mb, a?.vram_shared_total_mb);
  return tot;
});
const overallMemPercent = computed<number | null>(() => {
  const tot = overallMemTotalMB.value;
  const used = overallMemUsedMB.value;
  if (typeof tot === 'number' && tot > 0 && typeof used === 'number') {
    return Math.max(0, Math.min(100, (used / tot) * 100));
  }
  return null;
});
</script>

<style scoped>
.sub { color: #666; font-size: 12px; }
.kv { display: grid; grid-template-columns: 1fr auto; gap: 8px; padding: 4px 0; border-bottom: 1px dashed #f0f0f0; font-size: 12px; }
.list { display: grid; gap: 8px; margin-top: 8px; }
.list.compact .item { padding: 6px 8px; }
.item { border: 1px solid #eee; border-radius: 6px; padding: 8px; background: #fff; }
.item.active { outline: 2px solid #a3d3ff; }
.row { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.row.head { font-weight: 600; margin-bottom: 4px; }
.row.cols { gap: 12px; flex-wrap: wrap; }
.col { min-width: 160px; flex: 1; }
.ell { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.right { text-align: right; }
.mini { font-size: 12px; color: #444; }
.mono { font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace; font-size: 12px; color: #666; }
.card { max-width: 100%; overflow: hidden; }
</style>
