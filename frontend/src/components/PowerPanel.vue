<template>
  <div class="card">
    <h3>Power</h3>

    <section>
      <h4>Battery</h4>
      <table class="tbl">
        <tbody>
          <tr v-for="r in batteryRows" :key="r.k">
            <td class="k">{{ r.k }}</td>
            <td class="v">{{ r.v }}</td>
          </tr>
        </tbody>
      </table>
    </section>

    <section>
      <h4>Adapter</h4>
      <div v-if="adapter">
        <table class="tbl">
          <tbody>
            <tr v-for="r in adapterRows" :key="r.k">
              <td class="k">{{ r.k }}</td>
              <td class="v">{{ r.v }}</td>
            </tr>
          </tbody>
        </table>
        <table class="tbl" style="margin-top:6px;">
          <tbody>
            <tr>
              <td class="k">Power Source</td>
              <td class="v">{{ fmtPowerSource(adapter) }}</td>
            </tr>
          </tbody>
        </table>
        <details style="margin-top:6px;">
          <summary>adapter JSON</summary>
          <pre class="json">{{ adapterJson }}</pre>
        </details>
      </div>
      <div v-else>—</div>
    </section>

    <section>
      <h4>USB-PD</h4>
      <div v-if="pd">
        <table class="tbl">
          <tbody>
            <tr>
              <td class="k">Protocol</td>
              <td class="v">{{ pd.protocol || '—' }}</td>
            </tr>
            <tr>
              <td class="k">Negotiated Voltage</td>
              <td class="v">{{ typeof pd.negotiated_profile?.voltage_v === 'number' ? pd.negotiated_profile.voltage_v.toFixed(2) + ' V' : '—' }}</td>
            </tr>
            <tr>
              <td class="k">Negotiated Current</td>
              <td class="v">{{ typeof pd.negotiated_profile?.current_a === 'number' ? pd.negotiated_profile.current_a.toFixed(2) + ' A' : '—' }}</td>
            </tr>
            <tr>
              <td class="k">Negotiated Power</td>
              <td class="v">{{ typeof pd.negotiated_profile?.power_w === 'number' ? pd.negotiated_profile.power_w.toFixed(2) + ' W' : '—' }}</td>
            </tr>
          </tbody>
        </table>
        <div v-if="Array.isArray(pd.caps) && pd.caps.length > 0" style="margin-top:6px;">
          <table class="tbl">
            <tbody>
              <tr>
                <td class="k">PDO Count</td>
                <td class="v">{{ pd.caps.length }}</td>
              </tr>
              <tr v-for="(c, i) in pd.caps" :key="i">
                <td class="k">PDO #{{ i + 1 }}</td>
                <td class="v">
                  {{ typeof c.voltage_v === 'number' ? c.voltage_v.toFixed(2) + ' V' : '—' }}
                  · {{ typeof c.current_a === 'number' ? c.current_a.toFixed(2) + ' A' : '—' }}
                  · {{ typeof c.power_w === 'number' ? c.power_w.toFixed(2) + ' W' : '—' }}
                  <span v-if="c.pps === true"> · PPS</span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
      <div v-else>—</div>
    </section>

    <section>
      <h4>USB (Debug)</h4>
      <div v-if="usb">
        <table class="tbl">
          <thead>
            <tr><th class="k">Name</th><th class="v">Class</th></tr>
          </thead>
          <tbody>
            <tr v-for="(d, i) in usb.devices" :key="i">
              <td class="k">{{ d.name }}</td>
              <td class="v">{{ d.pnp_class || d.description || '—' }}</td>
            </tr>
          </tbody>
        </table>
        <details style="margin-top:6px;">
          <summary>usb JSON</summary>
          <pre class="json">{{ usbJson }}</pre>
        </details>
      </div>
      <div v-else>—</div>
    </section>

    <section>
      <h4>UPS</h4>
      <div v-if="ups">
        <table class="tbl">
          <tbody>
            <tr v-for="r in upsRows" :key="r.k">
              <td class="k">{{ r.k }}</td>
              <td class="v">{{ r.v }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <div v-else>—</div>
    </section>

    <section>
      <h4>USB (Debug)</h4>
      <div v-if="usb && (usb.devices?.length || 0) > 0">
        <table class="tbl">
          <tbody>
            <tr>
              <td class="k">Count</td>
              <td class="v">{{ usb.devices.length }}</td>
            </tr>
            <tr v-for="(d, idx) in usb.devices" :key="idx">
              <td class="k">{{ d.name || d.description || 'Unknown' }}</td>
              <td class="v">{{ d.pnp_class || d.status || '—' }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <div v-else>—</div>
    </section>

    <details class="json-box" v-if="power">
      <summary>power JSON</summary>
      <pre><code>{{ powerJson }}</code></pre>
    </details>
    <details class="json-box" v-if="adapter">
      <summary>adapter JSON</summary>
      <pre><code>{{ adapterJson }}</code></pre>
    </details>
    <details class="json-box" v-if="ups">
      <summary>ups JSON</summary>
      <pre><code>{{ upsJson }}</code></pre>
    </details>
    <details class="json-box" v-if="usb">
      <summary>usb JSON</summary>
      <pre><code>{{ usbJson }}</code></pre>
    </details>
  </div>
  
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useMetricsStore } from '../stores/metrics';

const metrics = useMetricsStore();
const power = computed<any>(() => (metrics.latest as any)?.power ?? null);
const bat = computed<any>(() => power.value?.battery ?? null);
const adapter = computed<any>(() => power.value?.adapter ?? null);
const ups = computed<any>(() => power.value?.ups ?? null);
const pd = computed<any>(() => adapter.value?.pd ?? null);
const usb = computed<any>(() => power.value?.usb ?? null);
const powerJson = computed(() => { try { return JSON.stringify(power.value, null, 2); } catch { return '{}'; } });
const adapterJson = computed(() => { try { return JSON.stringify(adapter.value, null, 2); } catch { return 'null'; } });
const upsJson = computed(() => { try { return JSON.stringify(ups.value, null, 2); } catch { return 'null'; } });
const usbJson = computed(() => { try { return JSON.stringify(usb.value, null, 2); } catch { return 'null'; } });

// 启发式判断电源来源（AC/Battery/—）
const fmtPowerSource = (a: any) => {
  if (!a) return '—';
  const mode = a.charge_mode as string | null | undefined;
  const present = a.present === true;
  const isPd = a.is_pd_fast_charge === true;
  const hasW = typeof a.negotiated_watts === 'number' && isFinite(a.negotiated_watts);
  if (mode === 'charging' || mode === 'maintenance' || mode === 'full') return 'AC';
  if (present || isPd || hasW) return 'AC';
  return 'Battery';
};

const fmtPct = (v: any) => typeof v === 'number' && isFinite(v) ? `${v.toFixed(1)}%` : '—';
const fmtMin = (v: any) => typeof v === 'number' && isFinite(v) ? `${Math.max(0, Math.round(v))} min` : '—';
const fmtSec = (v: any) => typeof v === 'number' && isFinite(v) ? `${Math.max(0, Math.round(v))} s` : '—';
const fmtMv = (v: any) => typeof v === 'number' && isFinite(v) ? `${v.toFixed(0)} mV` : '—';
const fmtMa = (v: any) => typeof v === 'number' && isFinite(v) ? `${v.toFixed(0)} mA` : '—';
const fmtW = (v: any) => typeof v === 'number' && isFinite(v) ? `${v.toFixed(2)} W` : '—';
const fmtMah = (v: any) => typeof v === 'number' && isFinite(v) ? `${v.toFixed(0)} mAh` : '—';
const fmtInt = (v: any) => typeof v === 'number' && isFinite(v) ? `${Math.max(0, Math.round(v))}` : '—';
const fmtBool = (v: any) => (v === true ? 'Yes' : v === false ? 'No' : '—');
const fmtC = (v: any) => typeof v === 'number' && isFinite(v) ? `${v.toFixed(1)} °C` : '—';

type Row = { k: string; v: string };
const batteryRows = computed<Row[]>(() => {
  const b = bat.value ?? {};
  return [
    { k: 'State', v: b.state ?? '—' },
    { k: 'Percentage', v: fmtPct(b.percentage) },
    { k: 'AC Line', v: fmtBool(b.ac_line_online) },
    { k: 'Time Remaining', v: fmtMin(b.time_remaining_min) },
    { k: 'Time To Full', v: fmtMin(b.time_to_full_min) },
    { k: 'Time On Battery', v: fmtSec(b.time_on_battery_sec) },
    { k: 'Temperature', v: fmtC(b.temperature_c) },
    { k: 'Voltage', v: fmtMv(b.voltage_mv) },
    { k: 'Current', v: fmtMa(b.current_ma) },
    { k: 'Power', v: fmtW(b.power_w) },
    { k: 'Full Capacity', v: fmtMah(b.full_charge_capacity_mah) },
    { k: 'Design Capacity', v: fmtMah(b.design_capacity_mah) },
    { k: 'Cycle Count', v: fmtInt(b.cycle_count) },
    { k: 'Condition', v: b.condition ?? '—' },
    { k: 'Manufacturer', v: b.manufacturer ?? '—' },
    { k: 'Serial', v: b.serial_number ?? '—' },
    { k: 'Mfg Date', v: b.manufacture_date ?? '—' },
  ];
});

const adapterRows = computed<Row[]>(() => {
  const a = adapter.value ?? {};
  return [
    { k: 'Present', v: fmtBool(a.present) },
    { k: 'Charge Mode', v: a.charge_mode ?? '—' },
    { k: 'PD Fast Charge', v: fmtBool(a.is_pd_fast_charge) },
    { k: 'Rated Watts', v: fmtW(a.rated_watts) },
    { k: 'Negotiated Watts', v: fmtW(a.negotiated_watts) },
    { k: 'Voltage', v: fmtMv(a.voltage_mv) },
    { k: 'Current', v: fmtMa(a.current_ma) },
  ];
});

const upsRows = computed<Row[]>(() => {
  const u = ups.value ?? {};
  return [
    { k: 'Present', v: fmtBool(u.present) },
    { k: 'Percentage', v: fmtPct(u.percentage) },
    { k: 'Runtime', v: fmtMin(u.runtime_min) },
    { k: 'Power Source', v: u.power_source ?? '—' },
    { k: 'Input Voltage', v: typeof u.input_voltage_v === 'number' ? `${u.input_voltage_v.toFixed(1)} V` : '—' },
    { k: 'Input Frequency', v: typeof u.input_frequency_hz === 'number' ? `${u.input_frequency_hz.toFixed(1)} Hz` : '—' },
    { k: 'Load', v: typeof u.load_percent === 'number' ? `${u.load_percent.toFixed(0)}%` : '—' },
  ];
});
</script>

<style scoped>
.tbl { width: 100%; border-collapse: collapse; font-size: 12px; }
.tbl td { border-bottom: 1px dashed #eee; padding: 6px 4px; }
.tbl .k { color: #667; width: 50%; }
.tbl .v { text-align: right; font-weight: 600; }
.card h4 { margin: 8px 0 4px; }
.json-box { margin-top: 8px; }
.json-box pre { max-height: 240px; overflow: auto; background: #0b1020; color: #cde2ff; padding: 8px; border-radius: 6px; }
</style>
