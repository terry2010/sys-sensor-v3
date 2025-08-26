<template>
  <div class="card">
    <h3>Peripherals</h3>

    <section>
      <h4>BLE/Peripheral Batteries</h4>
      <div style="margin: 6px 0 8px; display:flex; gap:12px; align-items:center;">
        <label style="user-select:none; cursor:pointer;">
          <input type="checkbox" v-model="showOnlineOnly" /> 只显示在线
        </label>
        <span style="color:#667; font-size:12px;">共 {{ batteries.length }}，显示 {{ batteriesFiltered.length }}</span>
      </div>
      <div v-if="Array.isArray(batteriesFiltered) && batteriesFiltered.length > 0">
        <div class="list">
          <div v-for="(b, i) in batteriesFiltered" :key="i" class="item">
            <div class="row head">
              <div class="name ell">{{ b.name || b.model || b.interface_path || '—' }}</div>
              <div class="right mono">{{ fmtPct(b.battery_percent) }}</div>
            </div>
            <div class="row cols">
              <div class="mini">在线：{{ fmtBool(b.present) }}</div>
              <div class="mini">连接：{{ b.connection || '—' }}</div>
              <div class="mini">来源：{{ b.source || '—' }}</div>
              <div class="mini">服务：{{ Array.isArray(b.services) ? b.services.length : (b.has_battery_service === true ? 1 : 0) }}</div>
              <div class="mini">地址：<span class="ell">{{ b.address || b.ble_address || '—' }}</span></div>
            </div>
            <details class="more">
              <summary>更多</summary>
              <div class="grid">
                <div class="kv"><span>Reason</span><span>{{ b.level_valid_reason || '—' }}</span></div>
                <div class="kv"><span>Sampling</span><span>{{ isNum(b.sampling_ms) ? b.sampling_ms : '—' }}</span></div>
                <div class="kv"><span>Retries</span><span>{{ isNum(b.retry_count) ? b.retry_count : '—' }}</span></div>
                <div class="kv"><span>FirstSeen</span><span>{{ fmtTs(b.first_seen_ts) }}</span></div>
                <div class="kv"><span>LastSeen</span><span>{{ fmtTs(b.last_seen_ts) }}</span></div>
                <div class="kv"><span>LastUpdate</span><span>{{ fmtTs(b.last_update_ts) }}</span></div>
                <div class="kv"><span>Path</span><span class="wrap">{{ b.interface_path || '—' }}</span></div>
                <div class="kv"><span>Open</span><span>{{ fmtBool(b.open_ok) }}</span></div>
                <div class="kv"><span>Stage</span><span>{{ isNum(b.gatt_err_stage) ? b.gatt_err_stage : '—' }}</span></div>
                <div class="kv"><span>HR_S</span><span>{{ isNum(b.gatt_hr_services) ? fmtHex(b.gatt_hr_services) : '—' }}</span></div>
                <div class="kv"><span>HR_C</span><span>{{ isNum(b.gatt_hr_chars) ? fmtHex(b.gatt_hr_chars) : '—' }}</span></div>
                <div class="kv"><span>HR_V</span><span>{{ isNum(b.gatt_hr_value) ? fmtHex(b.gatt_hr_value) : '—' }}</span></div>
              </div>
            </details>
          </div>
        </div>
        <details class="json-box" style="margin-top:6px;">
          <summary>peripherals JSON</summary>
          <pre class="json"><code>{{ peripheralsJson }}</code></pre>
        </details>
      </div>
      <div v-else>—</div>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { useMetricsStore } from '../stores/metrics';

const metrics = useMetricsStore();
const peripherals = computed<any>(() => (metrics.latest as any)?.peripherals ?? null);
const batteries = computed<any[]>(() => Array.isArray(peripherals.value?.batteries) ? peripherals.value.batteries : []);
const showOnlineOnly = ref(true);
const batteriesFiltered = computed<any[]>(() => showOnlineOnly.value ? batteries.value.filter(b => b?.present === true) : batteries.value);
const peripheralsJson = computed<string>(() => { try { return JSON.stringify(peripherals.value, null, 2); } catch { return '{}'; } });

const isNum = (v: any) => typeof v === 'number' && isFinite(v);
const fmtPct = (v: any) => isNum(v) ? `${(v as number).toFixed(1)}%` : '—';
const fmtBool = (v: any) => (v === true ? '✓' : v === false ? '✗' : '—');
const fmtTs = (v: any) => {
  if (!isNum(v) || v <= 0) return '—';
  try { return new Date(v).toLocaleString(); } catch { return String(v); }
};
const fmtHex = (v: any) => {
  if (!isNum(v)) return '—';
  const n = v as number; const h = (n >>> 0).toString(16).toUpperCase();
  return `0x${h}`;
};
</script>

<style scoped>
.list { display: grid; gap: 8px; }
.item { border: 1px solid #eee; border-radius: 6px; padding: 8px; background: #fff; }
.row { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.row.head { font-weight: 600; }
.row.cols { gap: 12px; flex-wrap: wrap; margin-top: 4px; }
.mini { font-size: 12px; color: #444; }
.name { font-weight: 600; }
.ell { max-width: 40vw; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.mono { font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace; font-size: 12px; color: #666; }
.more { margin-top: 6px; }
.grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 6px 12px; margin-top: 6px; }
.kv { display: grid; grid-template-columns: 1fr auto; gap: 6px; font-size: 12px; border-bottom: 1px dashed #f0f0f0; padding-bottom: 4px; }
.kv .wrap { white-space: normal; word-break: break-all; }
.json-box pre { max-height: 240px; overflow: auto; background: #0b1020; color: #cde2ff; padding: 8px; border-radius: 6px; }
</style>
