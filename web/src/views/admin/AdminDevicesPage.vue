<script setup lang="ts">
// 小趴菜 Web 3.0 — 管理端：设备管理
import { onMounted } from 'vue'
import { useDeviceStore } from '@/stores/devices'
import { authApi } from '@/api'
import { ElMessage, ElMessageBox } from 'element-plus'

const deviceStore = useDeviceStore()
onMounted(() => deviceStore.fetchDevices())

async function handleDeauthorize(deviceId: number) {
  try {
    await ElMessageBox.confirm('确定取消该设备授权？设备将无法连接。', '确认', { type: 'warning' })
  } catch { return }

  // [TASK-ACCOUNT-V1] A5 解绑前置：登录密码二次验证 → 一次性 Action Token
  try {
    const { value: password } = await ElMessageBox.prompt(
      '取消授权是敏感操作，请输入登录密码确认身份（验证通过后 5 分钟内有效）',
      '安全验证',
      {
        inputType: 'password',
        inputPlaceholder: '登录密码',
        confirmButtonText: '验证并取消授权',
        inputValidator: (v: string) => (v ? true : '请输入登录密码'),
      })
    const res = await authApi.verifyPassword(password)
    await deviceStore.unpairDevice(deviceId, res.data.actionToken)
    ElMessage.success('已取消授权')
  } catch (e: any) {
    if (e === 'cancel' || e === 'close') return
    ElMessage.error(e.response?.data?.error || '操作失败，请重试')
  }
}
</script>

<template>
  <div class="admin-page">
    <div class="page-header">
      <h2 class="page-title">设备管理（总览）</h2>
      <div class="header-stats">
        <el-tag type="success">在线 {{ deviceStore.onlineCount }}</el-tag>
        <el-tag type="info" style="margin-left:8px">总数 {{ deviceStore.totalCount }}</el-tag>
      </div>
    </div>

    <el-table :data="deviceStore.devices" v-loading="deviceStore.loading" stripe>
      <el-table-column prop="name" label="设备名称" min-width="140" />
      <el-table-column prop="deviceId" label="设备 ID" width="130" />
      <el-table-column prop="osVersion" label="系统" width="110" />
      <el-table-column label="状态" width="90">
        <template #default="{ row }">
          <el-tag :type="row.status==='online'?'success':row.status==='reconnecting'?'warning':'info'" size="small">
            {{ row.status==='online'?'在线':row.status==='reconnecting'?'重连':'离线' }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="ipAddress" label="IP" width="140" />
      <!-- [TASK-PRELAUNCH-P4] 调整后口径 + 已重置标注（与设备页同源） -->
      <el-table-column label="今日使用" width="180">
        <template #default="{ row }">
          {{ row.todayUsageMinutes }} / {{ row.todayLimitMinutes }} min
          <el-tag v-if="row.lastResetOffsetMinutes" size="small" type="warning" effect="plain">已重置</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="配对时间" width="180">
        <template #default="{ row }">{{ new Date(row.pairedAt).toLocaleString('zh-CN') }}</template>
      </el-table-column>
      <el-table-column label="最后在线" width="180">
        <template #default="{ row }">{{ new Date(row.lastSeen).toLocaleString('zh-CN') }}</template>
      </el-table-column>
      <el-table-column label="操作" width="120" fixed="right">
        <template #default="{ row }">
          <el-button size="small" text type="danger" @click="handleDeauthorize(row.id)">取消授权</el-button>
        </template>
      </el-table-column>
    </el-table>
  </div>
</template>

<style scoped>
.admin-page { max-width: 1400px; }
.page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; }
.page-title { font-size: 22px; font-weight: 600; margin: 0; }
</style>
