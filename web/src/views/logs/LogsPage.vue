<script setup lang="ts">
// 小趴菜 Web 3.0 — 运行日志（TASK-MILESTONE-V3 需求 14）
// 家长端 App 本机日志自动上传（每 6 小时 + 手动）；
// 普通家长仅见本账号日志，admin 可见全部并可按账号/级别/时间筛选（服务端过滤）；
// 内容两端脱敏（客户端写入打码 + 服务端入库二次打码），保留最近 7 天。
import { ref, reactive, computed, onMounted } from 'vue'
import { Refresh } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import dayjs from 'dayjs'
import { logsApi, adminAccountApi } from '@/api'
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
const isAdmin = computed(() => auth.isAdmin)

interface LogItem {
  id: number
  accountId: number
  accountEmail?: string
  level: string
  tag: string
  message: string
  client: string | null
  createdAt: string
  receivedAt: string
}

interface AccountOption {
  id: number
  label: string
}

const items = ref<LogItem[]>([])
const total = ref(0)
const loading = ref(false)
const accounts = ref<AccountOption[]>([])

const searchForm = reactive({
  level: '',
  accountId: null as number | null,
  dateRange: [] as string[],
})
const page = ref(1)
const pageSize = 50

async function fetchLogs() {
  loading.value = true
  try {
    const params: any = { limit: pageSize, offset: (page.value - 1) * pageSize }
    if (searchForm.level) params.level = searchForm.level
    if (isAdmin.value && searchForm.accountId) params.accountId = searchForm.accountId
    if (searchForm.dateRange?.length === 2) {
      params.from = searchForm.dateRange[0]
      params.to = searchForm.dateRange[1]
    }
    const res = await logsApi.list(params)
    items.value = res.data.items || []
    total.value = res.data.total || 0
  } catch {
    ElMessage.error('加载日志失败')
  } finally {
    loading.value = false
  }
}

async function loadAccounts() {
  if (!isAdmin.value) return
  try {
    const res = await adminAccountApi.list()
    accounts.value = (res.data || []).map((a: any) => ({
      id: a.id,
      label: a.email ? `${a.email}（${a.username}）` : a.username,
    }))
  } catch {
    // 账号列表加载失败不阻断日志查询（筛选器为空即可）
  }
}

onMounted(() => {
  fetchLogs()
  loadAccounts()
})

function search() {
  page.value = 1
  fetchLogs()
}

const levelType = (level: string) =>
  ({ debug: 'info', info: 'primary', warn: 'warning', error: 'danger' } as Record<string, string>)[level] || 'info'

const levelLabel = (level: string) =>
  ({ debug: '调试', info: '信息', warn: '警告', error: '错误' } as Record<string, string>)[level] || level

function formatTime(v: string) {
  return dayjs(v).isValid() ? dayjs(v).format('YYYY-MM-DD HH:mm:ss') : v
}
</script>

<template>
  <div class="logs-page">
    <!-- 筛选栏 -->
    <el-card shadow="never" class="filter-card">
      <div class="filter-row">
        <el-select
          v-if="isAdmin"
          v-model="searchForm.accountId"
          placeholder="全部账号"
          clearable
          filterable
          style="width: 220px"
          @change="search"
        >
          <el-option v-for="a in accounts" :key="a.id" :label="a.label" :value="a.id" />
        </el-select>
        <el-select v-model="searchForm.level" placeholder="全部级别" clearable style="width: 130px" @change="search">
          <el-option label="调试" value="debug" />
          <el-option label="信息" value="info" />
          <el-option label="警告" value="warn" />
          <el-option label="错误" value="error" />
        </el-select>
        <el-date-picker
          v-model="searchForm.dateRange"
          type="datetimerange"
          range-separator="至"
          start-placeholder="开始时间"
          end-placeholder="结束时间"
          value-format="YYYY-MM-DDTHH:mm:ss"
          style="width: 360px"
          @change="search"
        />
        <el-button :icon="Refresh" @click="search">刷新</el-button>
        <span class="retention-tip">服务端保留最近 7 天 · 内容已脱敏（无密码/验证码/令牌明文）</span>
      </div>
    </el-card>

    <!-- 日志表 -->
    <el-card shadow="never" class="table-card">
      <el-table v-loading="loading" :data="items" stripe size="small" style="width: 100%">
        <el-table-column label="时间" width="165">
          <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column v-if="isAdmin" label="账号" width="200" show-overflow-tooltip>
          <template #default="{ row }">{{ row.accountEmail || `#${row.accountId}` }}</template>
        </el-table-column>
        <el-table-column label="客户端" width="130" show-overflow-tooltip>
          <template #default="{ row }">{{ row.client || '-' }}</template>
        </el-table-column>
        <el-table-column label="级别" width="80">
          <template #default="{ row }">
            <el-tag :type="levelType(row.level)" size="small" disable-transitions>{{ levelLabel(row.level) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="模块" width="140" show-overflow-tooltip>
          <template #default="{ row }">{{ row.tag }}</template>
        </el-table-column>
        <el-table-column label="内容" min-width="320" show-overflow-tooltip>
          <template #default="{ row }">
            <span class="log-message">{{ row.message }}</span>
          </template>
        </el-table-column>
      </el-table>

      <div class="pager-row">
        <el-pagination
          v-model:current-page="page"
          :page-size="pageSize"
          :total="total"
          layout="total, prev, pager, next"
          @current-change="fetchLogs"
        />
      </div>
    </el-card>
  </div>
</template>

<style scoped>
.logs-page {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.filter-row {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

.retention-tip {
  font-size: 12px;
  color: var(--el-text-color-secondary);
  margin-left: auto;
}

.log-message {
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, monospace;
  font-size: 12px;
}

.pager-row {
  display: flex;
  justify-content: flex-end;
  margin-top: 12px;
}
</style>
