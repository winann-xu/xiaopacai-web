<script setup lang="ts">
// 小趴菜 Web 3.0 — 管理端：故障诊断（OPT12 需求 5）
// 儿童端定期上报诊断信息（版本/权限状态/服务状态/崩溃/P2P 历史），此处提供列表筛选、详情查看与导出。
import { ref, reactive, onMounted } from 'vue'
import { Search, Download, Refresh } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import dayjs from 'dayjs'
import { adminDiagnosticsApi } from '@/api'

// 诊断记录条目（字段与后端 DiagnosticsController 返回一致）
interface DiagnosticRecord {
  id: number
  deviceId: string
  appVersion: string | null
  androidVersion: string | null
  deviceModel: string | null
  manufacturer: string | null
  permissionStatus: string | null  // JSON 对象：无障碍/用量/设备管理器/通知/电池优化
  serviceStatus: string | null     // JSON 对象：守护服务/无障碍服务
  recentCrashes: string | null     // JSON 数组：最近 5 条崩溃堆栈
  p2pHistory: string | null        // JSON 对象：成功/失败/重连次数
  dbSizeBytes: number | null
  networkType: string | null       // wifi | cellular | none
  reportedAt: string
}

const records = ref<DiagnosticRecord[]>([])
const loading = ref(false)
const total = ref(0)

// 筛选条件：设备 ID + 上报时间范围
const searchForm = reactive({
  deviceId: '',
  dateRange: [] as string[],
})

// 详情弹窗
const detailVisible = ref(false)
const detailRecord = ref<DiagnosticRecord | null>(null)

// 刷新数据
async function fetchRecords() {
  loading.value = true
  try {
    const params: any = { limit: 100 }
    if (searchForm.deviceId.trim()) params.deviceId = searchForm.deviceId.trim()
    if (searchForm.dateRange?.length === 2) {
      params.from = searchForm.dateRange[0]
      params.to = searchForm.dateRange[1]
    }
    const res = await adminDiagnosticsApi.list(params)
    records.value = res.data.items || []
    total.value = res.data.total || 0
  } catch {
    ElMessage.error('加载诊断数据失败')
  } finally {
    loading.value = false
  }
}

function handleSearch() {
  fetchRecords()
}

// 导出诊断数据（JSON 文件下载，携带当前筛选条件）
async function handleExport() {
  try {
    const params: any = {}
    if (searchForm.deviceId.trim()) params.deviceId = searchForm.deviceId.trim()
    if (searchForm.dateRange?.length === 2) {
      params.from = searchForm.dateRange[0]
      params.to = searchForm.dateRange[1]
    }
    const res = await adminDiagnosticsApi.exportData(params)
    const blob = new Blob([res.data], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `diagnostics_${dayjs().format('YYYYMMDD_HHmmss')}.json`
    a.click()
    URL.revokeObjectURL(url)
    ElMessage.success('诊断数据导出完成')
  } catch {
    ElMessage.error('导出失败')
  }
}

// 点击行查看详情
function showDetail(row: DiagnosticRecord) {
  detailRecord.value = row
  detailVisible.value = true
}

// ===== 详情展示辅助：安全解析后端返回的 JSON 字符串 =====
function parseJson<T>(json: string | null): T | null {
  if (!json) return null
  try {
    return JSON.parse(json) as T
  } catch {
    return null
  }
}

// 权限状态（键 → 中文名）
const PERMISSION_LABELS: Record<string, string> = {
  accessibility: '无障碍',
  usageAccess: '用量访问',
  deviceAdmin: '设备管理器',
  notification: '通知',
  batteryOptimization: '电池优化',
}

// 服务状态（键 → 中文名）
const SERVICE_LABELS: Record<string, string> = {
  guardian: '守护服务',
  accessibility: '无障碍服务',
}

// 网络类型 → 中文
function networkText(network: string | null): string {
  if (network === 'wifi') return 'WiFi'
  if (network === 'cellular') return '蜂窝网络'
  if (network === 'none') return '无网络'
  return network || '—'
}

// 数据库大小格式化
function formatBytes(bytes: number | null): string {
  if (bytes == null) return '—'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(2)} MB`
}

// 布尔值 → 状态文字（详情展示用）
function boolText(v: any): string {
  return v === true ? '已开启' : v === false ? '未开启' : '—'
}

onMounted(() => { fetchRecords() })
</script>

<template>
  <div class="admin-page">
    <div class="page-header">
      <h2 class="page-title">故障诊断</h2>
      <div class="page-actions">
        <el-button :icon="Download" size="small" @click="handleExport">导出</el-button>
        <el-button :icon="Refresh" size="small" circle @click="fetchRecords" />
      </div>
    </div>

    <!-- 筛选区 -->
    <el-card shadow="hover" style="margin-bottom: 16px">
      <el-form :inline="true" size="small" @submit.prevent="handleSearch">
        <el-form-item label="设备 ID">
          <el-input v-model="searchForm.deviceId" placeholder="设备唯一标识" clearable style="width: 200px" />
        </el-form-item>
        <el-form-item label="上报时间">
          <el-date-picker
            v-model="searchForm.dateRange"
            type="datetimerange"
            range-separator="至"
            start-placeholder="开始时间"
            end-placeholder="结束时间"
            value-format="YYYY-MM-DDTHH:mm:ss"
            style="width: 360px"
          />
        </el-form-item>
        <el-form-item>
          <el-button type="primary" :icon="Search" native-type="submit">查询</el-button>
        </el-form-item>
      </el-form>
    </el-card>

    <!-- 诊断记录表格 -->
    <el-table
      :data="records"
      v-loading="loading"
      stripe
      size="small"
      max-height="560"
      @row-click="showDetail"
      style="cursor: pointer"
    >
      <el-table-column prop="id" label="#" width="60" />
      <el-table-column prop="deviceId" label="设备 ID" min-width="160" show-overflow-tooltip />
      <el-table-column prop="appVersion" label="APP 版本" width="100" />
      <el-table-column prop="androidVersion" label="Android 版本" width="120" />
      <el-table-column prop="deviceModel" label="设备型号" min-width="140" show-overflow-tooltip />
      <el-table-column label="网络" width="100">
        <template #default="{ row }">
          <el-tag :type="row.networkType === 'none' ? 'info' : 'success'" size="small">
            {{ networkText(row.networkType) }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column label="上报时间" width="180">
        <template #default="{ row }">{{ new Date(row.reportedAt).toLocaleString('zh-CN') }}</template>
      </el-table-column>
    </el-table>

    <el-pagination
      layout="total"
      :total="total"
      style="margin-top: 16px; justify-content: flex-end"
    />

    <!-- 诊断详情弹窗 -->
    <el-dialog v-model="detailVisible" title="诊断详情" width="680px">
      <template v-if="detailRecord">
        <el-descriptions :column="2" border size="small" style="margin-bottom: 16px">
          <el-descriptions-item label="设备 ID" :span="2">{{ detailRecord.deviceId }}</el-descriptions-item>
          <el-descriptions-item label="APP 版本">{{ detailRecord.appVersion || '—' }}</el-descriptions-item>
          <el-descriptions-item label="Android 版本">{{ detailRecord.androidVersion || '—' }}</el-descriptions-item>
          <el-descriptions-item label="设备型号">{{ detailRecord.deviceModel || '—' }}</el-descriptions-item>
          <el-descriptions-item label="厂商">{{ detailRecord.manufacturer || '—' }}</el-descriptions-item>
          <el-descriptions-item label="网络状态">{{ networkText(detailRecord.networkType) }}</el-descriptions-item>
          <el-descriptions-item label="数据库大小">{{ formatBytes(detailRecord.dbSizeBytes) }}</el-descriptions-item>
          <el-descriptions-item label="上报时间" :span="2">{{ new Date(detailRecord.reportedAt).toLocaleString('zh-CN') }}</el-descriptions-item>
        </el-descriptions>

        <!-- 权限状态 -->
        <div class="detail-section">
          <h4 class="detail-title">权限状态</h4>
          <el-descriptions :column="3" border size="small">
            <el-descriptions-item
              v-for="(label, key) in PERMISSION_LABELS"
              :key="key"
              :label="label"
            >
              {{ boolText(parseJson<any>(detailRecord.permissionStatus)?.[key]) }}
            </el-descriptions-item>
          </el-descriptions>
        </div>

        <!-- 服务状态 -->
        <div class="detail-section">
          <h4 class="detail-title">服务运行状态</h4>
          <el-descriptions :column="2" border size="small">
            <el-descriptions-item
              v-for="(label, key) in SERVICE_LABELS"
              :key="key"
              :label="label"
            >
              {{ boolText(parseJson<any>(detailRecord.serviceStatus)?.[key]) }}
            </el-descriptions-item>
          </el-descriptions>
        </div>

        <!-- 最近崩溃 -->
        <div class="detail-section">
          <h4 class="detail-title">最近崩溃（{{ (parseJson<any[]>(detailRecord.recentCrashes) || []).length }} 条）</h4>
          <el-empty
            v-if="!(parseJson<any[]>(detailRecord.recentCrashes) || []).length"
            description="无崩溃记录"
            :image-size="60"
          />
          <el-collapse v-else>
            <el-collapse-item
              v-for="(crash, i) in parseJson<any[]>(detailRecord.recentCrashes)"
              :key="i"
              :title="`崩溃 #${i + 1}${crash?.time ? ' · ' + new Date(crash.time).toLocaleString('zh-CN') : ''}`"
            >
              <pre class="crash-stack">{{ crash?.stackTrace || JSON.stringify(crash, null, 2) }}</pre>
            </el-collapse-item>
          </el-collapse>
        </div>

        <!-- P2P 连接历史 -->
        <div class="detail-section">
          <h4 class="detail-title">P2P 连接历史</h4>
          <el-descriptions :column="3" border size="small">
            <el-descriptions-item label="连接成功">
              {{ parseJson<any>(detailRecord.p2pHistory)?.successCount ?? '—' }}
            </el-descriptions-item>
            <el-descriptions-item label="连接失败">
              {{ parseJson<any>(detailRecord.p2pHistory)?.failCount ?? '—' }}
            </el-descriptions-item>
            <el-descriptions-item label="重连次数">
              {{ parseJson<any>(detailRecord.p2pHistory)?.reconnectCount ?? '—' }}
            </el-descriptions-item>
          </el-descriptions>
        </div>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.admin-page { max-width: 1400px; }
.page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; }
.page-title { font-size: 22px; font-weight: 600; margin: 0; }
.detail-section { margin-top: 16px; }
.detail-title { font-size: 14px; font-weight: 600; margin: 0 0 8px; color: var(--el-text-color-regular); }
.crash-stack {
  margin: 0;
  font-size: 12px;
  line-height: 1.6;
  white-space: pre-wrap;
  word-break: break-all;
  background: var(--el-fill-color-light);
  padding: 10px;
  border-radius: 6px;
  max-height: 220px;
  overflow: auto;
}
</style>
