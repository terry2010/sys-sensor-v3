<template>
  <div class="card">
    <h3>Network</h3>
    <div class="sub">实时网络速率（总览与每接口）</div>
    <!-- Wi‑Fi 与连通性简要信息 -->
    <div class="kv" v-if="wifi">
      <span class="k">Wi‑Fi</span>
      <span class="v">{{ wifi.ssid || '-' }} ({{ wifi.bssid || '-' }}) · Ch {{ wifi.channel ?? '-' }} · PHY {{ wifi.phy_mode || '-' }} · Tx {{ wifi.tx_phy_rate_mbps ?? '-' }} Mbps</span>
    </div>
    <div class="kv" v-if="conn">
      <span class="k">Connectivity</span>
      <span class="v">IPv4 {{ conn.public_ipv4 || '-' }}, IPv6 {{ conn.public_ipv6 || '-' }}</span>
    </div>

    <div v-if="totals">
      <div class="kv"><span class="k">rx_bytes_per_sec</span><span class="v">{{ fmtBps(totals.rx_bytes_per_sec) }}</span></div>
      <div class="kv"><span class="k">tx_bytes_per_sec</span><span class="v">{{ fmtBps(totals.tx_bytes_per_sec) }}</span></div>
      <div class="kv"><span class="k">rx_packets_per_sec</span><span class="v">{{ fmtNum(totals.rx_packets_per_sec) }}</span></div>
      <div class="kv"><span class="k">tx_packets_per_sec</span><span class="v">{{ fmtNum(totals.tx_packets_per_sec) }}</span></div>
      <div class="kv"><span class="k">rx_errors_per_sec</span><span class="v">{{ fmtNum(totals.rx_errors_per_sec) }}</span></div>
      <div class="kv"><span class="k">tx_errors_per_sec</span><span class="v">{{ fmtNum(totals.tx_errors_per_sec) }}</span></div>
      <div class="kv"><span class="k">rx_drops_per_sec</span><span class="v">{{ fmtNum(totals.rx_drops_per_sec) }}</span></div>
      <div class="kv"><span class="k">tx_drops_per_sec</span><span class="v">{{ fmtNum(totals.tx_drops_per_sec) }}</span></div>
    </div>

    <details class="json-box">
      <summary>per_interface_io</summary>
      <div class="table">
        <div class="thead">
          <span class="c if">if_id</span>
          <span class="c name">name</span>
          <span class="c rx">rx_bytes/s</span>
          <span class="c tx">tx_bytes/s</span>
          <span class="c rpk">rx_pkts/s</span>
          <span class="c tpk">tx_pkts/s</span>
          <span class="c rer">rx_err/s</span>
          <span class="c ter">tx_err/s</span>
          <span class="c rdp">rx_drop/s</span>
          <span class="c tdp">tx_drop/s</span>
          <span class="c util">util%</span>
        </div>
        <div class="row" v-for="(it, idx) in list" :key="idx">
          <span class="c if">{{ it.if_id }}</span>
          <span class="c name">{{ it.name }}</span>
          <span class="c rx">{{ fmtBps(it.rx_bytes_per_sec) }}</span>
          <span class="c tx">{{ fmtBps(it.tx_bytes_per_sec) }}</span>
          <span class="c rpk">{{ fmtNum(it.rx_packets_per_sec) }}</span>
          <span class="c tpk">{{ fmtNum(it.tx_packets_per_sec) }}</span>
          <span class="c rer">{{ fmtNum(it.rx_errors_per_sec) }}</span>
          <span class="c ter">{{ fmtNum(it.tx_errors_per_sec) }}</span>
          <span class="c rdp">{{ fmtNum(it.rx_drops_per_sec) }}</span>
          <span class="c tdp">{{ fmtNum(it.tx_drops_per_sec) }}</span>
          <span class="c util">{{ fmtPercent(it.utilization_percent) }}</span>
        </div>
      </div>
      <pre><code>{{ netJson }}</code></pre>
    </details>

    <details class="json-box">
      <summary>per_interface_info</summary>
      <div class="table info">
        <div class="thead">
          <span class="c if">if_id</span>
          <span class="c name">name</span>
          <span class="c stat">status</span>
          <span class="c type">type</span>
          <span class="c mac">mac</span>
          <span class="c mtu">mtu</span>
          <span class="c speed">link(Mbps)</span>
        </div>
        <div class="row" v-for="(it, idx) in infoList" :key="idx">
          <span class="c if">{{ it.if_id }}</span>
          <span class="c name">{{ it.name }}</span>
          <span class="c stat">{{ it.status }}</span>
          <span class="c type">{{ it.type }}</span>
          <span class="c mac">{{ it.mac_address ?? '-' }}</span>
          <span class="c mtu">{{ it.mtu ?? '-' }}</span>
          <span class="c speed">{{ it.link_speed_mbps ?? '-' }}</span>
        </div>
      </div>
    </details>

    <details class="json-box">
      <summary>per_ethernet_info</summary>
      <div class="table eth">
        <div class="thead">
          <span class="c if">if_id</span>
          <span class="c name">name</span>
          <span class="c speed">link(Mbps)</span>
          <span class="c duplex">duplex</span>
          <span class="c auto">auto_negotiation</span>
        </div>
        <div class="row" v-for="(it, idx) in ethList" :key="idx">
          <span class="c if">{{ it.if_id }}</span>
          <span class="c name">{{ it.name }}</span>
          <span class="c speed">{{ it.link_speed_mbps ?? '-' }}</span>
          <span class="c duplex">{{ it.duplex ?? '-' }}</span>
          <span class="c auto">{{ String(it.auto_negotiation) }}</span>
        </div>
      </div>
    </details>

    <details class="json-box" v-if="wifi">
      <summary>wifi_info</summary>
      <pre><code>{{ wifiJson }}</code></pre>
    </details>

    <details class="json-box" v-if="conn">
      <summary>connectivity</summary>
      <pre><code>{{ connJson }}</code></pre>
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
const infoList = computed<any[]>(() => Array.isArray(net.value?.per_interface_info) ? net.value.per_interface_info : []);
const ethList = computed<any[]>(() => Array.isArray(net.value?.per_ethernet_info) ? net.value.per_ethernet_info : []);
const wifi = computed<any>(() => net.value?.wifi_info || null);
const conn = computed<any>(() => net.value?.connectivity || null);
const wifiJson = computed<string>(() => { try { return JSON.stringify(wifi.value || {}, null, 2); } catch { return '{}'; } });
const connJson = computed<string>(() => { try { return JSON.stringify(conn.value || {}, null, 2); } catch { return '{}'; } });

const fmtBps = (v: any) => {
  if (v === null || v === undefined) return '-';
  if (typeof v !== 'number' || !isFinite(v)) return '-';
  // 简易单位换算
  if (v >= 1024 * 1024) return (v / (1024 * 1024)).toFixed(2) + ' MB/s';
  if (v >= 1024) return (v / 1024).toFixed(1) + ' KB/s';
  return v.toFixed(0) + ' B/s';
};

const fmtNum = (v: any) => {
  if (v === null || v === undefined) return '-';
  if (typeof v !== 'number' || !isFinite(v)) return '-';
  return Math.round(v).toString();
};

const fmtPercent = (v: any) => {
  if (v === null || v === undefined) return '-';
  if (typeof v !== 'number' || !isFinite(v)) return '-';
  return v.toFixed(1) + '%';
};
</script>

<style scoped>
.sub { color: #666; font-size: 12px; margin-bottom: 6px; }
.kv { display: flex; justify-content: space-between; font-size: 13px; }
.k { color: #555; }
.v { color: #222; font-weight: 600; }
.table { margin-top: 8px; border-top: 1px dashed #eee; }
.thead, .row { display: grid; grid-template-columns: 190px 1fr 120px 120px 90px 90px 80px 80px 90px 90px 70px; gap: 8px; padding: 6px 0; border-bottom: 1px dashed #f0f0f0; font-size: 12px; }
.thead { color: #666; font-weight: 600; }
.c { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.json-box pre { max-height: 240px; overflow: auto; background: #0b1020; color: #cde2ff; padding: 8px; border-radius: 6px; margin-top: 8px; }

/* info 与 eth 表格稍作列宽调整 */
.table.info .thead, .table.info .row { grid-template-columns: 190px 1fr 110px 120px 160px 80px 110px; }
.table.eth .thead, .table.eth .row { grid-template-columns: 190px 1fr 120px 90px 140px; }
</style>

