<script setup lang="ts">
// 小趴菜 Web 3.0 — 设备管理
import { ref, onMounted, onUnmounted, computed } from 'vue'
import { useDeviceStore } from '@/stores/devices'
import type { Device } from '@/stores/devices'
import { pairingApi, policyApi, authApi, guardEventsApi } from '@/api'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus, Search, Monitor, Refresh, CircleCheckFilled, CircleCloseFilled } from '@element-plus/icons-vue'
import { toDataURL as qrToDataURL } from 'qrcode'

// [TASK-HARDENING-V1.1.1] Bug1-D/1-B：守护健康度快照与失守历史
interface HealthSnapshot {
  score?: number
  readyCount?: number
  totalCount?: number
  status?: string
  guardDown?: boolean
  manufacturer?: string
  model?: string
  timestamp?: number
  items?: Record<string, any>
  [k: string]: any
}
interface GuardHistoryItem {
  eventType: string
  startedAt?: number | null
  durationSeconds?: number | null
  reason?: string | null
  restoredReason?: string | null
  [k: string]: any
}
// 健康度 6 项检查项中文名（无障碍/设备管理员 + OPPO 保活四项；未知键原样显示）
const ITEM_LABELS: Record<string, string> = {
  accessibility_service: '无障碍服务',
  accessibility: '无障碍服务',
  device_owner: '设备管理员',
  device_admin: '设备管理员',
  self_start: '自启动管理',
  background_activity: '后台活动/冻结',
  battery_whitelist: '电池优化白名单',
  battery_optimization: '电池优化白名单',
  recent_task_lock: '最近任务锁定',
}
const REASON_LABELS: Record<string, string> = {
  process_killed: '进程被杀',
  swipe_killed: '上滑关闭',
  accessibility_disabled: '无障碍被关闭',
  device_owner_removed: '设备管理员被移除',
  auto_recovered: '自动恢复',
  swipe_recovery: '上滑恢复',
  accessibility_reenabled: '无障碍已开启',
  device_owner_reenabled: '设备管理员已恢复',
  manual_recovery: '手动恢复',
}

const deviceStore = useDeviceStore()
const searchText = ref('')
const showDetailDialog = ref(false)
const detailDevice = ref<Device | null>(null)
const showBindQr = ref(false)
const bindLoading = ref(false)
const bindPairCode = ref('')
const bindQrDataUrl = ref('')
const bindExpiresAt = ref('')
// [TASK-PRELAUNCH-P4] 实时刷新：30s 轮询（设备状态与额度 ≤30s 同步，需求 9 第 2 条）
let refreshTimer: ReturnType<typeof setInterval> | null = null
const resettingId = ref<number | null>(null)

// [TASK-HARDENING-V1.1.1] 守护健康：卡片徽章（挂载/手动刷新拉取，≤10 台）+ 详情弹窗完整数据
const healthBadges = ref<Record<string, HealthSnapshot>>({})
const detailHealth = ref<HealthSnapshot | null>(null)
const detailHealthLoading = ref(false)
const detailEvents = ref<GuardHistoryItem[]>([])
const detailEventsLoading = ref(false)

const filteredDevices = computed(() => {
  if (!searchText.value) return deviceStore.devices
  const q = searchText.value.toLowerCase()
  return deviceStore.devices.filter(d =>
    d.name.toLowerCase().includes(q) || d.deviceId.toLowerCase().includes(q) || d.ipAddress.includes(q))
})

onMounted(() => {
  deviceStore.fetchDevices().then(() => refreshHealthBadges())
  refreshTimer = setInterval(() => { deviceStore.fetchDevices() }, 30_000)
})
onUnmounted(() => { if (refreshTimer) clearInterval(refreshTimer) })

// 生成儿童端扫码绑定二维码（服务端配对码，归属当前家长账号）
async function openBindQr() {
  bindLoading.value = true
  showBindQr.value = true
  bindPairCode.value = ''
  bindQrDataUrl.value = ''
  try {
    const res = await pairingApi.bindingQr()
    bindPairCode.value = res.data.pairCode
    bindExpiresAt.value = res.data.expiresAt
    bindQrDataUrl.value = await qrToDataURL(res.data.qrContent, { width: 280, margin: 1 })
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '生成二维码失败')
  } finally {
    bindLoading.value = false
  }
}

function showDetail(device: Device) {
  detailDevice.value = device
  showDetailDialog.value = true
  loadGuardData(device.deviceId)
}

// 手动刷新：设备列表 + 卡片健康徽章（30s 轮询不重复拉健康，避免请求风暴）
function handleRefresh() {
  deviceStore.fetchDevices().then(() => refreshHealthBadges())
}

// [TASK-HARDENING-V1.1.1] 卡片健康度徽章：挂载与手动刷新时并行拉取（≤10 台，静默失败）
async function refreshHealthBadges() {
  const targets = deviceStore.devices.slice(0, 10)
  await Promise.all(targets.map(async (d) => {
    try {
      const res = await guardEventsApi.latestHealth(d.deviceId)
      const health = res.data?.health
      if (health) healthBadges.value[d.deviceId] = health
      else delete healthBadges.value[d.deviceId]
    } catch {
      delete healthBadges.value[d.deviceId] // 静默：详情弹窗内仍可查看
    }
  }))
}

// 详情弹窗：健康度快照 + 失守历史（服务端已做账号隔离，前端无需角色判断）
async function loadGuardData(deviceId: string) {
  detailHealthLoading.value = true
  detailEventsLoading.value = true
  detailHealth.value = null
  detailEvents.value = []
  try {
    const [healthRes, eventsRes] = await Promise.all([
      guardEventsApi.latestHealth(deviceId),
      guardEventsApi.list(deviceId, 50),
    ])
    detailHealth.value = healthRes.data?.health ?? null
    // 失守历史：过滤健康快照，仅展示失守/恢复事件，取最近 10 条（服务端按接收时间倒序）
    detailEvents.value = (eventsRes.data?.events ?? [])
      .filter((e: GuardHistoryItem) => e.eventType !== 'health_snapshot')
      .slice(0, 10)
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '获取守护健康数据失败')
  } finally {
    detailHealthLoading.value = false
    detailEventsLoading.value = false
  }
}

async function handleUnpair(device: Device) {
  try {
    // [TASK-PRELAUNCH-FIX-SCAN] 解绑即释放归属：明确告知可用任意账号重新扫码绑定
    await ElMessageBox.confirm(
      `确定要解绑设备「${device.name}」吗？解绑后将清空设备归属，可用任意账号重新扫码绑定。`,
      '确认解绑', { type: 'warning' })
  } catch { return }

  // [TASK-ACCOUNT-V1] A5 解绑前置：登录密码二次验证 → 一次性 Action Token → 携带解绑
  try {
    const { value: password } = await ElMessageBox.prompt(
      '解绑是敏感操作，请输入登录密码确认身份（验证通过后 5 分钟内有效）',
      '安全验证',
      {
        inputType: 'password',
        inputPlaceholder: '登录密码',
        confirmButtonText: '验证并解绑',
        inputValidator: (v: string) => (v ? true : '请输入登录密码'),
      })
    const res = await authApi.verifyPassword(password)
    await deviceStore.unpairDevice(device.id, res.data.actionToken)
    ElMessage.success('解绑成功')
  } catch (e: any) {
    if (e === 'cancel' || e === 'close') return // 用户取消
    ElMessage.error(e.response?.data?.error || '解绑失败，请重试')
  }
}

// [TASK-PRELAUNCH-P4] 重置当日限额：成功后本地即时归零 + 立即刷新（需求 7 验收）
async function handleResetLimit(device: Device) {
  try {
    await ElMessageBox.confirm(
      `确定重置「${device.name}」的当日限额吗？儿童端将重新开始计时（重置前用量仍保留在报告中）。`,
      '重置当日限额', { type: 'warning' })
  } catch { return }
  resettingId.value = device.id
  try {
    const res = await policyApi.resetLimit(device.id)
    deviceStore.applyResetLocally(device.id, res.data.todayRemainingMinutes ?? device.todayLimitMinutes)
    await deviceStore.fetchDevices()
    ElMessage.success(res.data.message || '当日限额已重置')
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '重置失败')
  } finally {
    resettingId.value = null
  }
}

function statusTagType(s: string) { return s === 'online' ? 'success' : s === 'reconnecting' ? 'warning' : 'info' }
function statusText(s: string) { return s === 'online' ? '在线' : s === 'reconnecting' ? '重连中' : '离线' }
function fmtTime(iso?: string | null) { return iso ? new Date(iso).toLocaleString('zh-CN') : '—' }

// ===== 守护健康展示辅助 =====
function healthTagType(status?: string) { return status === 'good' ? 'success' : status === 'attention' ? 'warning' : status === 'danger' ? 'danger' : 'info' }
function healthStatusText(status?: string) { return status === 'good' ? '良好' : status === 'attention' ? '需关注' : status === 'danger' ? '危险' : (status || '—') }
function badgeTagType(status?: string) { return status === 'good' ? 'success' : status === 'danger' ? 'danger' : 'warning' }
function eventTagType(t: string) { return t === 'guard_down' ? 'danger' : t === 'guard_restored' ? 'success' : 'info' }
function eventTagText(t: string) { return t === 'guard_down' ? '守护失效' : t === 'guard_restored' ? '守护恢复' : '健康快照' }
// 客户端 epoch 秒 → 本地时间（null/0 → —）
function fmtEpoch(sec?: number | null) { return sec ? new Date(sec * 1000).toLocaleString('zh-CN') : '—' }
function fmtDuration(sec?: number | null) {
  if (sec == null) return '—'
  const s = Math.max(0, sec)
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const r = s % 60
  if (h > 0) return `${h} 小时 ${m} 分`
  if (m > 0) return `${m} 分 ${r} 秒`
  return `${r} 秒`
}
// 检查项值：布尔直接判定；对象兼容 { ok } / { healthy } / { status }
function isItemOk(v: any): boolean {
  if (typeof v === 'boolean') return v
  if (v && typeof v === 'object') {
    if (typeof v.ok === 'boolean') return v.ok
    if (typeof v.healthy === 'boolean') return v.healthy
    return v.status === 'ok' || v.status === 'good'
  }
  return false
}
function itemLabel(key: string) { return ITEM_LABELS[key] || key }
function reasonLabel(r?: string | null) { return r ? (REASON_LABELS[r] || r) : '—' }
</script>

<template>
  <div class="devices-page">
    <div class="page-header">
      <h2 class="page-title">设备管理</h2>
      <div class="page-actions">
        <el-input v-model="searchText" placeholder="搜索设备" :prefix-icon="Search" clearable style="width: 220px" />
        <el-button :icon="Refresh" @click="handleRefresh" :loading="deviceStore.loading">刷新</el-button>
        <el-button type="primary" :icon="Plus" @click="openBindQr">添加设备</el-button>
      </div>
    </div>

    <!-- [TASK-PRELAUNCH-P4] 最后刷新时间 + 30s 自动轮询说明（需求 9 第 2 条） -->
    <p v-if="deviceStore.lastRefreshAt" class="last-refresh">
      最后刷新 {{ fmtTime(deviceStore.lastRefreshAt) }} · 每 30 秒自动更新
    </p>

    <!-- [TASK-PRELAUNCH-P4] 错误态 + 重试（移除 Mock 兜底，绝不渲染假设备） -->
    <el-alert v-if="deviceStore.error" type="error" :closable="false" class="load-error">
      <template #title>
        {{ deviceStore.error }}
        <el-button size="small" type="primary" text @click="deviceStore.fetchDevices()">重试</el-button>
      </template>
    </el-alert>

    <!-- [TASK-ACCOUNT-V1] A6：绑定设备 >10 台预警（不阻断，提醒清理闲置设备） -->
    <el-alert
      v-if="deviceStore.deviceCount > 10"
      type="warning"
      :closable="false"
      class="load-error"
      title="设备数量预警"
      show-icon
    >
      当前账号绑定 {{ deviceStore.deviceCount }} 台设备，数量较多。设备数不设上限，但建议及时解绑闲置设备，降低账号风险。
    </el-alert>

    <div v-loading="deviceStore.loading" class="device-grid">
      <el-empty v-if="!deviceStore.devices.length && !deviceStore.loading" description="暂无设备" />
      <el-card v-for="device in filteredDevices" :key="device.id" shadow="hover" class="device-card"
        :class="{ 'is-offline': device.status === 'offline' }">
        <div class="card-body" @click="showDetail(device)">
          <div class="card-icon">
            <el-icon :size="40"><Monitor /></el-icon>
            <el-tag :type="statusTagType(device.status)" size="small" class="status-tag">{{ statusText(device.status) }}</el-tag>
          </div>
          <div class="card-info">
            <h3 class="device-name">{{ device.name }}</h3>
            <p class="device-meta">{{ device.deviceId }} · {{ device.osVersion }}</p>
            <p class="device-ip">IP: {{ device.ipAddress }}</p>
            <el-progress :percentage="device.todayLimitMinutes ? Math.round(device.todayUsageMinutes / device.todayLimitMinutes * 100) : 0"
              :stroke-width="10" :status="device.todayUsageMinutes >= device.todayLimitMinutes ? 'exception' : undefined"
              style="margin-top: 8px" />
            <!-- [TASK-PRELAUNCH-P4] 调整后口径 + 原始累计区分标注（需求 7 验收“数字关系可解释”） -->
            <p class="device-usage">
              今日已用：{{ device.todayUsageMinutes }} / {{ device.todayLimitMinutes }} 分钟
              <el-tag v-if="device.lastResetOffsetMinutes" size="small" type="warning" effect="plain">已重置</el-tag>
            </p>
            <p v-if="(device.rawTodayUsageMinutes ?? 0) !== device.todayUsageMinutes" class="device-raw">
              原始累计 {{ device.rawTodayUsageMinutes }} 分钟（含重置前，报告同口径）
            </p>
            <!-- [TASK-HARDENING-V1.1.1] 守护健康徽章：失守立即醒目提示，正常显示健康分 -->
            <p v-if="healthBadges[device.deviceId]" class="device-guard">
              <el-tag v-if="healthBadges[device.deviceId].guardDown" type="danger" size="small" effect="dark">守护失效</el-tag>
              <el-tag v-else :type="badgeTagType(healthBadges[device.deviceId].status)" size="small" effect="plain">
                守护健康 {{ healthBadges[device.deviceId].score ?? '—' }}
              </el-tag>
            </p>
          </div>
        </div>
        <div class="card-actions">
          <el-button size="small" text type="primary" @click.stop="showDetail(device)">详情</el-button>
          <el-button size="small" text type="warning" :loading="resettingId === device.id"
            @click.stop="handleResetLimit(device)">重置限额</el-button>
          <el-button size="small" text type="danger" @click.stop="handleUnpair(device)">解绑</el-button>
        </div>
      </el-card>
    </div>

    <!-- 儿童端扫码绑定 -->
    <el-dialog v-model="showBindQr" title="扫码绑定儿童端" width="420px">
      <div v-loading="bindLoading" class="bind-qr-body">
        <p class="bind-hint">用儿童端「连接家长端 → 扫码」扫描下方二维码，即可把儿童端绑定到你的账号。</p>
        <div v-if="bindQrDataUrl" class="bind-qr-img">
          <img :src="bindQrDataUrl" alt="绑定二维码" />
        </div>
        <div v-if="bindPairCode" class="bind-code">
          <span>配对码</span>
          <strong>{{ bindPairCode }}</strong>
          <em>5 分钟内有效</em>
        </div>
      </div>
      <template #footer>
        <el-button @click="showBindQr = false">关闭</el-button>
      </template>
    </el-dialog>

    <!-- 详情弹窗 -->
    <el-dialog v-model="showDetailDialog" title="设备详情" width="660px">
      <template v-if="detailDevice">
        <el-descriptions :column="2" border>
          <el-descriptions-item label="设备名称">{{ detailDevice.name }}</el-descriptions-item>
          <el-descriptions-item label="设备 ID">{{ detailDevice.deviceId }}</el-descriptions-item>
          <el-descriptions-item label="系统版本">{{ detailDevice.osVersion }}</el-descriptions-item>
          <el-descriptions-item label="状态">
            <el-tag :type="statusTagType(detailDevice.status)" size="small">{{ statusText(detailDevice.status) }}</el-tag>
          </el-descriptions-item>
          <el-descriptions-item label="IP 地址">{{ detailDevice.ipAddress }}</el-descriptions-item>
          <el-descriptions-item label="最后在线">{{ fmtTime(detailDevice.lastSeen) }}</el-descriptions-item>
          <el-descriptions-item label="配对时间">{{ fmtTime(detailDevice.pairedAt) }}</el-descriptions-item>
          <!-- [TASK-PRELAUNCH-FIX-SCAN] 绑定账号显示（null=无归属），便于排查跨账号扫码 -->
          <el-descriptions-item label="绑定账号">{{ detailDevice.ownerAccount || '—' }}</el-descriptions-item>
          <!-- [TASK-PRELAUNCH-P4] 需求 7 第 5 条：最近上报 + 采集延迟说明 -->
          <el-descriptions-item label="最近上报">
            {{ fmtTime(detailDevice.lastReportAt) }}
            <div class="report-delay-hint">儿童端每 ≤5 分钟采集上报一次，数据可能略有延迟</div>
          </el-descriptions-item>
          <el-descriptions-item label="今日已用">{{ detailDevice.todayUsageMinutes }} / {{ detailDevice.todayLimitMinutes }} 分钟</el-descriptions-item>
          <el-descriptions-item label="今日剩余">{{ detailDevice.todayRemainingMinutes ?? 0 }} 分钟</el-descriptions-item>
          <el-descriptions-item label="原始累计">{{ detailDevice.rawTodayUsageMinutes ?? detailDevice.todayUsageMinutes }} 分钟
            <div class="report-delay-hint">含重置前用量（使用报告同口径）</div>
          </el-descriptions-item>
          <el-descriptions-item label="重置偏移" v-if="detailDevice.lastResetOffsetMinutes">
            {{ detailDevice.lastResetOffsetMinutes }} 分钟（今日已重置）
          </el-descriptions-item>
          <el-descriptions-item label="证书指纹" :span="2">
            <code style="font-size:11px;word-break:break-all">{{ detailDevice.certFingerprint }}</code>
          </el-descriptions-item>
        </el-descriptions>

        <!-- [TASK-HARDENING-V1.1.1] Bug1-D/1-B：守护健康 + 失守历史（服务端已账号隔离） -->
        <div class="guard-section">
          <h4 class="guard-title">守护健康</h4>
          <div v-loading="detailHealthLoading" class="guard-body">
            <template v-if="detailHealth">
              <div class="guard-score-row">
                <span class="guard-score" :class="`is-${detailHealth.status || 'unknown'}`">{{ detailHealth.score ?? '—' }}</span>
                <el-tag :type="healthTagType(detailHealth.status)" size="small" disable-transitions>{{ healthStatusText(detailHealth.status) }}</el-tag>
                <el-tag v-if="detailHealth.guardDown" type="danger" size="small" effect="dark">守护失效</el-tag>
              </div>
              <div v-if="detailHealth.items && Object.keys(detailHealth.items).length" class="guard-items">
                <div v-for="(val, key) in detailHealth.items" :key="key" class="guard-item">
                  <el-icon :size="14" :color="isItemOk(val) ? '#67c23a' : '#f56c6c'">
                    <CircleCheckFilled v-if="isItemOk(val)" />
                    <CircleCloseFilled v-else />
                  </el-icon>
                  <span class="guard-item-name">{{ itemLabel(String(key)) }}</span>
                </div>
              </div>
              <p class="guard-meta">
                已就绪 {{ detailHealth.readyCount ?? 0 }} / {{ detailHealth.totalCount ?? 0 }} 项
                <template v-if="detailHealth.timestamp"> · 快照时间 {{ fmtEpoch(detailHealth.timestamp) }}</template>
                <template v-if="detailHealth.manufacturer"> · {{ detailHealth.manufacturer }} {{ detailHealth.model || '' }}</template>
              </p>
            </template>
            <el-empty v-else-if="!detailHealthLoading" description="暂无健康度数据（儿童端上报后显示）" :image-size="60" />
          </div>

          <h4 class="guard-title">失守历史（最近 10 条）</h4>
          <div v-loading="detailEventsLoading">
            <el-table v-if="detailEvents.length" :data="detailEvents" size="small" stripe style="width: 100%">
              <el-table-column label="时间" width="150">
                <template #default="{ row }">{{ fmtEpoch(row.startedAt) }}</template>
              </el-table-column>
              <el-table-column label="事件" width="100">
                <template #default="{ row }">
                  <el-tag :type="eventTagType(row.eventType)" size="small" disable-transitions>{{ eventTagText(row.eventType) }}</el-tag>
                </template>
              </el-table-column>
              <el-table-column label="原因" width="130" show-overflow-tooltip>
                <template #default="{ row }">{{ reasonLabel(row.reason) }}</template>
              </el-table-column>
              <el-table-column label="时长" width="100">
                <template #default="{ row }">{{ fmtDuration(row.durationSeconds) }}</template>
              </el-table-column>
              <el-table-column label="恢复方式" min-width="120" show-overflow-tooltip>
                <template #default="{ row }">{{ reasonLabel(row.restoredReason) }}</template>
              </el-table-column>
            </el-table>
            <el-empty v-else-if="!detailEventsLoading" description="暂无失守记录" :image-size="60" />
          </div>
        </div>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.devices-page { max-width: 1400px; }
.page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; flex-wrap: wrap; gap: 12px; }
.page-title { font-size: 22px; font-weight: 600; margin: 0; }
.page-actions { display: flex; gap: 8px; align-items: center; }
.device-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(340px, 1fr)); gap: 16px; }
.device-card { cursor: pointer; transition: transform 0.2s; }
.device-card:hover { transform: translateY(-2px); }
.device-card.is-offline { opacity: 0.7; }
.card-body { display: flex; gap: 16px; }
.card-icon { position: relative; flex-shrink: 0; color: var(--el-color-primary); }
.status-tag { position: absolute; bottom: -4px; left: 50%; transform: translateX(-50%); white-space: nowrap; }
.card-info { flex: 1; min-width: 0; }
.device-name { font-size: 16px; font-weight: 600; margin: 0 0 4px; }
.device-meta, .device-ip { font-size: 12px; color: var(--el-text-color-secondary); margin: 0 0 2px; }
.device-usage { font-size: 12px; color: var(--el-text-color-secondary); margin: 4px 0 0; }
.device-raw { font-size: 11px; color: var(--el-text-color-placeholder); margin: 2px 0 0; }
.device-guard { margin: 6px 0 0; }

/* [TASK-HARDENING-V1.1.1] 守护健康区块 */
.guard-section { margin-top: 16px; border-top: 1px solid var(--el-border-color-lighter); padding-top: 12px; }
.guard-title { font-size: 14px; font-weight: 600; margin: 0 0 8px; }
.guard-body { min-height: 40px; }
.guard-score-row { display: flex; align-items: center; gap: 8px; margin-bottom: 8px; }
.guard-score { font-size: 26px; font-weight: 700; line-height: 1; color: var(--el-color-success); }
.guard-score.is-attention { color: var(--el-color-warning); }
.guard-score.is-danger { color: var(--el-color-danger); }
.guard-items { display: grid; grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); gap: 6px 12px; margin-bottom: 8px; }
.guard-item { display: flex; align-items: center; gap: 6px; font-size: 12px; color: var(--el-text-color-regular); }
.guard-item-name { word-break: break-all; }
.guard-meta { font-size: 12px; color: var(--el-text-color-placeholder); margin: 0 0 8px; }
.last-refresh { font-size: 12px; color: var(--el-text-color-placeholder); margin: 0 0 12px; }
.load-error { margin-bottom: 12px; }
.report-delay-hint { font-size: 11px; color: var(--el-text-color-placeholder); }
.card-actions { display: flex; justify-content: flex-end; gap: 4px; margin-top: 12px; padding-top: 12px; border-top: 1px solid var(--el-border-color-lighter); }

/* 扫码绑定二维码（基线） */
.bind-qr-body { display: flex; flex-direction: column; align-items: center; gap: 12px; }
.bind-hint { font-size: 13px; color: var(--el-text-color-secondary); text-align: center; margin: 0; }
.bind-qr-img { padding: 12px; border: 1px dashed var(--el-border-color); border-radius: 8px; }
.bind-qr-img img { display: block; }
.bind-code { display: flex; align-items: baseline; gap: 8px; font-size: 13px; color: var(--el-text-color-secondary); }
.bind-code strong { font-size: 22px; letter-spacing: 4px; color: var(--el-color-primary); }
.bind-code em { font-size: 12px; font-style: normal; color: var(--el-text-color-placeholder); }

/* [TASK-PRELAUNCH-P1] 移动端：页头堆叠、搜索全宽、单列卡片、按钮触控区 */
@media (max-width: 768px) {
  .page-header { flex-direction: column; align-items: stretch; }
  .page-actions { flex-direction: column; }
  .page-actions .el-input { width: 100% !important; }
  .page-actions .el-button { min-height: 44px; }
  .device-grid { grid-template-columns: 1fr; }
  .card-actions .el-button { min-height: 44px; }
}
</style>
