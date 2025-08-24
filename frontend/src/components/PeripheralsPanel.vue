<template>
  <div class="card">
    <h3>Peripherals</h3>

    <section>
      <h4>BLE/Peripheral Batteries</h4>
      <div v-if="Array.isArray(batteries) && batteries.length > 0">
        <div class="tbl-wrap">
        <table class="tbl">
          <thead>
            <tr>
              <th class="k">Name</th>
              <th class="v">Present</th>
              <th class="v">Battery</th>
              <th class="v">Conn</th>
              <th class="v">Source</th>
              <th class="v">Reason</th>
              <th class="v">HasBattSvc</th>
              <th class="v">Services</th>
              <th class="v">Sampling(ms)</th>
              <th class="v">Retries</th>
              <th class="v">Address</th>
              <th class="v">FirstSeen</th>
              <th class="v">LastSeen</th>
              <th class="v">LastUpdate</th>
              <th class="v">Path</th>
              <th class="v">Open</th>
              <th class="v">Stage</th>
              <th class="v">HR_S</th>
              <th class="v">HR_C</th>
              <th class="v">HR_V</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(b, i) in batteries" :key="i">
              <td class="k">{{ b.name || b.model || b.interface_path || '—' }}</td>
              <td class="v">{{ fmtBool(b.present) }}</td>
              <td class="v">{{ fmtPct(b.battery_percent) }}</td>
              <td class="v">{{ b.connection || '—' }}</td>
              <td class="v">{{ b.source || '—' }}</td>
              <td class="v">{{ b.level_valid_reason || '—' }}</td>
              <td class="v">{{ fmtBool(b.has_battery_service) }}</td>
              <td class="v">{{ Array.isArray(b.services) ? b.services.length : (b.has_battery_service === true ? 1 : 0) }}</td>
              <td class="v">{{ isNum(b.sampling_ms) ? b.sampling_ms : '—' }}</td>
              <td class="v">{{ isNum(b.retry_count) ? b.retry_count : '—' }}</td>
              <td class="v">{{ b.address || b.ble_address || '—' }}</td>
              <td class="v">{{ fmtTs(b.first_seen_ts) }}</td>
              <td class="v">{{ fmtTs(b.last_seen_ts) }}</td>
              <td class="v">{{ fmtTs(b.last_update_ts) }}</td>
              <td class="v" style="max-width:360px;">{{ b.interface_path || '—' }}</td>
              <td class="v">{{ fmtBool(b.open_ok) }}</td>
              <td class="v">{{ isNum(b.gatt_err_stage) ? b.gatt_err_stage : '—' }}</td>
              <td class="v">{{ isNum(b.gatt_hr_services) ? fmtHex(b.gatt_hr_services) : '—' }}</td>
              <td class="v">{{ isNum(b.gatt_hr_chars) ? fmtHex(b.gatt_hr_chars) : '—' }}</td>
              <td class="v">{{ isNum(b.gatt_hr_value) ? fmtHex(b.gatt_hr_value) : '—' }}</td>
            </tr>
          </tbody>
        </table>
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
import { computed } from 'vue';
import { useMetricsStore } from '../stores/metrics';

const metrics = useMetricsStore();
const peripherals = computed<any>(() => (metrics.latest as any)?.peripherals ?? null);
const batteries = computed<any[]>(() => Array.isArray(peripherals.value?.batteries) ? peripherals.value.batteries : []);
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
.tbl-wrap { width: 100%; overflow-x: auto; }
.tbl { width: max-content; min-width: 100%; border-collapse: collapse; font-size: 12px; }
.tbl th, .tbl td { border-bottom: 1px dashed #eee; padding: 6px 4px; text-align: left; }
.tbl .k { color: #667; }
.tbl .v { text-align: right; font-weight: 600; }
.json-box pre { max-height: 240px; overflow: auto; background: #0b1020; color: #cde2ff; padding: 8px; border-radius: 6px; }
</style>
