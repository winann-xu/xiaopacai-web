<script setup lang="ts">
// 小趴菜 Web 3.0 — 管理端：云端中继会话（OPT12 需求 3）
// 儿童端 / 家长端通过 Web 3.0 中继节点（9527）跨网络连接，此处查看在线中继设备。
import { ref, reactive, onMounted } from 'vue'
import { Search, Refresh } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { relayApi } from '@/api'

// 中继会话条目（字段与后端 RelayController 返回一致）
interface RelaySession {
  id: number
  deviceId: string
  role: 'child' | 'parent'
  userId: number | null
  ipAddress: string | null
  status: 'connected' | 'disconnected'
  connectedAt: string
  disconnectedAt: string | null
}

const sessions = ref<RelaySession[]>([])
const loading = ref(false)
const total = ref(0)
const onlineCount = ref(0)

// 筛选条件：连接状态 + 角色
const searchForm = reactive({
  status: '',
  role: '',
})

// 刷新列表
async function fetchSessions() {
  loading.value = true
  try {
    const params: any = { limit: 100 }
    if (searchForm.status) params.status = searchForm.status
    if (searchForm.role) params.role = searchForm.role
    const res = await relayApi.sessions(params)
    sessions.value = res.data.items || []
    total.value = res.data.total || 0
    onlineCount.value = res.data.onlineCount || 0
  } catch {
    ElMessage.error('加载中继会话失败')
  } finally {
    loading.value = false
  }
}

function handleSearch() {
  fetchSessions()
}

// 状态 → 标签类型 / 文案
function statusTagType(s: string) {
  return s === 'connected' ? 'success' : 'info'
}
function statusText(s: string) {
  return s === 'connected' ? '在线' : '离线'
}

// 角色 → 标签类型 / 文案
function roleTagType(role: string) {
  return role === 'child' ? 'warning' : 'primary'
}
function roleText(role: string) {
  return role === 'child' ? '儿童端' : '家长端'
}

onMounted(() => { fetchSessions() })
</script>

<template>
  <div class="admin-page">
    <div class="page-header">
      <h2 class="page-title">云端中继会话</h2>
      <div class="page-actions">
        <el-button :icon="Refresh" size="small" circle @click="fetchSessions" />
      </div>
    </div>

    <!-- 概览统计 -->
    <el-row :gutter="16" style="margin-bottom: 16px">
      <el-col :span="8">
        <el-card shadow="hover" class="stat-card">
          <p class="stat-label">在线中继设备</p>
          <p class="stat-value" style="color: var(--el-color-success)">{{ onlineCount }}</p>
        </el-card>
      </el-col>
      <el-col :span="8">
        <el-card shadow="hover" class="stat-card">
          <p class="stat-label">会话总数</p>
          <p class="stat-value">{{ total }}</p>
        </el-card>
      </el-col>
      <el-col :span="8">
        <el-card shadow="hover" class="stat-card">
          <p class="stat-label">在线率</p>
          <p class="stat-value">{{ total ? Math.round(onlineCount / total * 100) : 0 }}%</p>
        </el-card>
      </el-col>
    </el-row>

    <!-- 筛选区 -->
    <el-card shadow="hover" style="margin-bottom: 16px">
      <el-form :inline="true" size="small" @submit.prevent="handleSearch">
        <el-form-item label="连接状态">
          <el-select v-model="searchForm.status" placeholder="全部" clearable style="width: 140px">
            <el-option label="在线" value="connected" />
            <el-option label="离线" value="disconnected" />
          </el-select>
        </el-form-item>
        <el-form-item label="角色">
          <el-select v-model="searchForm.role" placeholder="全部" clearable style="width: 140px">
            <el-option label="儿童端" value="child" />
            <el-option label="家长端" value="parent" />
          </el-select>
        </el-form-item>
        <el-form-item>
          <el-button type="primary" :icon="Search" native-type="submit">查询</el-button>
        </el-form-item>
      </el-form>
    </el-card>

    <!-- 会话表格 -->
    <el-table :data="sessions" v-loading="loading" stripe size="small" max-height="560">
      <el-table-column prop="id" label="#" width="60" />
      <el-table-column prop="deviceId" label="设备 ID" min-width="180" show-overflow-tooltip />
      <el-table-column label="角色" width="100">
        <template #default="{ row }">
          <el-tag :type="roleTagType(row.role)" size="small">{{ roleText(row.role) }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="userId" label="用户 ID" width="100">
        <template #default="{ row }">{{ row.userId ?? '—' }}</template>
      </el-table-column>
      <el-table-column prop="ipAddress" label="IP 地址" width="140">
        <template #default="{ row }">{{ row.ipAddress || '—' }}</template>
      </el-table-column>
      <el-table-column label="状态" width="90">
        <template #default="{ row }">
          <el-tag :type="statusTagType(row.status)" size="small">{{ statusText(row.status) }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="连接时间" width="180">
        <template #default="{ row }">{{ new Date(row.connectedAt).toLocaleString('zh-CN') }}</template>
      </el-table-column>
      <el-table-column label="断开时间" width="180">
        <template #default="{ row }">{{ row.disconnectedAt ? new Date(row.disconnectedAt).toLocaleString('zh-CN') : '—' }}</template>
      </el-table-column>
    </el-table>

    <el-pagination
      layout="total"
      :total="total"
      style="margin-top: 16px; justify-content: flex-end"
    />
  </div>
</template>

<style scoped>
.admin-page { max-width: 1400px; }
.page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; }
.page-title { font-size: 22px; font-weight: 600; margin: 0; }
.stat-card { text-align: center; }
.stat-label { font-size: 13px; color: var(--el-text-color-secondary); margin: 0 0 8px; }
.stat-value { font-size: 28px; font-weight: 700; margin: 0; color: var(--el-text-color-primary); }
</style>
