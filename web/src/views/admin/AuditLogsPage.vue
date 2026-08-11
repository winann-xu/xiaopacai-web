<script setup lang="ts">
// 小趴菜 Web 3.0 — 管理端：审计日志
import { ref, reactive, computed } from 'vue'
import { Search, Download } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import dayjs from 'dayjs'

interface AuditEntry {
  id: number; username: string; action: string; resource: string; detail: string; ipAddress: string; timestamp: string
}

// Mock 审计日志
const logs = ref<AuditEntry[]>([
  { id:1, username:'admin', action:'登录', resource:'系统', detail:'管理员登录成功', ipAddress:'127.0.0.1', timestamp: new Date().toISOString() },
  { id:2, username:'parent', action:'修改策略', resource:'策略', detail:'修改设备「小明的手机」每日限额: 120→180min', ipAddress:'192.168.1.1', timestamp: new Date(Date.now()-3600000).toISOString() },
  { id:3, username:'admin', action:'创建账号', resource:'账号', detail:'创建家长账号: parent2', ipAddress:'127.0.0.1', timestamp: new Date(Date.now()-7200000).toISOString() },
  { id:4, username:'parent', action:'发布公告', resource:'公告', detail:'发布公告「今日使用时长已调整」', ipAddress:'192.168.1.1', timestamp: new Date(Date.now()-10800000).toISOString() },
  { id:5, username:'admin', action:'数据备份', resource:'数据', detail:'手动备份数据库', ipAddress:'127.0.0.1', timestamp: new Date(Date.now()-86400000).toISOString() },
  { id:6, username:'parent', action:'登录失败', resource:'系统', detail:'密码错误（尝试 3/5）', ipAddress:'192.168.1.100', timestamp: new Date(Date.now()-90000000).toISOString() },
  { id:7, username:'admin', action:'解绑设备', resource:'设备', detail:'取消授权设备「测试设备」', ipAddress:'127.0.0.1', timestamp: new Date(Date.now()-172800000).toISOString() },
])

const searchForm = reactive({ username: '', action: '', dateRange: '' })
const loading = ref(false)

const filteredLogs = computed(() => {
  let result = logs.value
  if (searchForm.username) result = result.filter(l => l.username.includes(searchForm.username))
  if (searchForm.action) result = result.filter(l => l.action.includes(searchForm.action))
  return result
})

function exportLogs(format: 'json' | 'csv') {
  let content = '', filename = `audit-${dayjs().format('YYYYMMDD')}`, mime = ''
  if (format === 'json') { content = JSON.stringify(filteredLogs.value, null, 2); filename += '.json'; mime = 'application/json' }
  else { content = 'ID,User,Action,Resource,Detail,IP,Time\n' + filteredLogs.value.map(l => `${l.id},"${l.username}","${l.action}","${l.resource}","${l.detail}","${l.ipAddress}","${l.timestamp}"`).join('\n'); filename += '.csv'; mime = 'text/csv' }
  const blob = new Blob([content], { type: mime }); const url = URL.createObjectURL(blob)
  const a = document.createElement('a'); a.href = url; a.download = filename; a.click(); URL.revokeObjectURL(url)
  ElMessage.success('导出完成')
}

function actionTagColor(action: string): string {
  if (action.includes('失败')) return 'danger'
  if (action.includes('登录')) return 'success'
  if (action.includes('删除')||action.includes('解绑')) return 'warning'
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
        <el-form-item><el-button :icon="Search" type="primary">查询</el-button></el-form-item>
      </el-form>
    </el-card>

    <el-table :data="filteredLogs" v-loading="loading" stripe size="small" max-height="560">
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
    <el-pagination layout="total, prev, pager, next" :total="filteredLogs.length" :page-size="20" style="margin-top:16px;justify-content:flex-end" />
  </div>
</template>

<style scoped>
.admin-page { max-width: 1400px; }
.page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; }
.page-title { font-size: 22px; font-weight: 600; margin: 0; }
</style>
