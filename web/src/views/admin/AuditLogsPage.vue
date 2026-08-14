<script setup lang="ts">
// 小趴菜 Web 3.0 — 管理端：审计日志
import { ref, reactive, onMounted } from 'vue'
import { Search, Download } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { adminAuditApi } from '@/api'

interface AuditEntry {
  id: number; username: string; action: string; resource: string; detail: string; ipAddress: string; timestamp: string
}

// [TASK-PRELAUNCH-P4] 移除 Mock：审计日志走真实 API（/admin/audit-logs），失败显示错误态 + 重试
const logs = ref<AuditEntry[]>([])
const loading = ref(false)
const error = ref<string | null>(null)
const total = ref(0)
const page = ref(1)
const pageSize = 20

const searchForm = reactive({ username: '', action: '', dateRange: '' })

async function loadLogs() {
  loading.value = true
  error.value = null
  try {
    const params: Record<string, any> = { page: page.value, pageSize }
    if (searchForm.username) params.username = searchForm.username
    if (searchForm.action) params.action = searchForm.action
    if (searchForm.dateRange && Array.isArray(searchForm.dateRange)) {
      params.from = searchForm.dateRange[0]
      params.to = searchForm.dateRange[1]
    }
    const res = await adminAuditApi.list(params)
    logs.value = res.data.items ?? res.data
    total.value = res.data.total ?? logs.value.length
  } catch (e: any) {
    error.value = e.response?.data?.message || e.response?.data?.error || '获取审计日志失败'
  } finally {
    loading.value = false
  }
}
onMounted(loadLogs)

function handleSearch() { page.value = 1; loadLogs() }
function handlePageChange(p: number) { page.value = p; loadLogs() }

async function exportLogs(format: 'json' | 'csv') {
  try {
    const params: Record<string, any> = {}
    if (searchForm.username) params.username = searchForm.username
    if (searchForm.action) params.action = searchForm.action
    const res = await adminAuditApi.exportData(format, params)
    // 服务端返回 blob（含 Content-Disposition 文件名）
    const blob = new Blob([res.data])
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `audit-export.${format === 'json' ? 'json' : 'csv'}`
    a.click()
    URL.revokeObjectURL(url)
    ElMessage.success('导出完成')
  } catch {
    ElMessage.error('导出失败')
  }
}

function actionTagColor(action: string): string {
  if (action.includes('失败')) return 'danger'
  if (action.includes('登录')) return 'success'
  if (action.includes('删除')||action.includes('解绑')||action.includes('清除')) return 'warning'
  return 'primary'
}
</script>

<template>
  <div class="admin-page">
    <div class="page-header">
      <h2 class="page-title">审计日志</h2>
      <el-dropdown @command="exportLogs">
        <el-button size="small" :icon="Download">导出</el-button>
        <template #dropdown>
          <el-dropdown-menu>
            <el-dropdown-item command="json">JSON</el-dropdown-item>
            <el-dropdown-item command="csv">CSV</el-dropdown-item>
          </el-dropdown-menu>
        </template>
      </el-dropdown>
    </div>

    <el-card shadow="hover" style="margin-bottom:16px">
      <el-form :inline="true" size="small">
        <el-form-item label="用户"><el-input v-model="searchForm.username" placeholder="用户名" clearable style="width:150px" /></el-form-item>
        <el-form-item label="操作"><el-input v-model="searchForm.action" placeholder="操作类型" clearable style="width:150px" /></el-form-item>
        <el-form-item label="时间范围">
          <el-date-picker v-model="searchForm.dateRange" type="datetimerange" size="small"
            start-placeholder="开始时间" end-placeholder="结束时间" value-format="YYYY-MM-DDTHH:mm:ss" style="width: 340px" />
        </el-form-item>
        <el-form-item><el-button :icon="Search" type="primary" @click="handleSearch">查询</el-button></el-form-item>
      </el-form>
    </el-card>

    <!-- [TASK-PRELAUNCH-P4] 错误态 + 重试（移除 Mock 数据） -->
    <el-alert v-if="error" type="error" :closable="false" style="margin-bottom: 12px">
      <template #title>
        {{ error }}
        <el-button size="small" type="primary" text @click="loadLogs">重试</el-button>
      </template>
    </el-alert>

    <el-table :data="logs" v-loading="loading" stripe size="small" max-height="560">
      <el-table-column prop="id" label="#" width="60" />
      <el-table-column prop="username" label="用户" width="100" />
      <el-table-column label="操作" width="100">
        <template #default="{ row }"><el-tag :type="actionTagColor(row.action)" size="small">{{ row.action }}</el-tag></template>
      </el-table-column>
      <el-table-column prop="resource" label="资源" width="80" />
      <el-table-column prop="detail" label="详情" min-width="240" show-overflow-tooltip />
      <el-table-column prop="ipAddress" label="IP" width="140" />
      <el-table-column label="时间" width="180">
        <template #default="{ row }">{{ new Date(row.timestamp).toLocaleString('zh-CN') }}</template>
      </el-table-column>
    </el-table>
    <el-pagination layout="total, prev, pager, next" :total="total" :page-size="pageSize"
      :current-page="page" @current-change="handlePageChange" style="margin-top:16px;justify-content:flex-end" />
  </div>
</template>

<style scoped>
.admin-page { max-width: 1400px; }
.page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; }
.page-title { font-size: 22px; font-weight: 600; margin: 0; }
</style>
