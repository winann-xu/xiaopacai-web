<script setup lang="ts">
// 小趴菜 Web 3.0 — 管理端：数据管理
import { ref, reactive } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { adminDataApi } from '@/api'
import { UploadFilled, Refresh, Delete } from '@element-plus/icons-vue'

const storageStatus = reactive({
  dbSizeMB: 12.4,
  backupCount: 5,
  lastBackupAt: new Date(Date.now() - 86400000).toISOString(),
  keyRotationAt: new Date(Date.now() - 7*86400000).toISOString(),
  healthOk: true,
})

const backupLoading = ref(false)
const rotatingKeys = ref(false)

async function handleBackup() {
  backupLoading.value = true
  try {
    const res = await adminDataApi.backup()
    const blob = new Blob([JSON.stringify(res.data)], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a'); a.href = url; a.download = `xiaopacai-admin-backup-${new Date().toISOString().slice(0,10)}.json`; a.click()
    URL.revokeObjectURL(url); ElMessage.success('备份完成')
  } catch { ElMessage.error('备份失败') }
  finally { backupLoading.value = false }
}

function handleRestore() {
  const input = document.createElement('input'); input.type = 'file'; input.accept = '.json'
  input.onchange = async (e: any) => {
    const file = e.target.files?.[0]; if (!file) return
    try { await ElMessageBox.confirm('恢复将覆盖所有数据，是否继续？', '确认恢复', { type: 'warning' }); await adminDataApi.restore(file); ElMessage.success('数据恢复成功') } catch { /* */ }
  }; input.click()
}

async function handleClearAll() {
  try {
    await ElMessageBox.confirm('此操作将清除所有数据（含账号/设备/策略/记录），不可恢复！', '⚠️ 最终确认', { type: 'error', confirmButtonText: '确认清除所有数据' })
    await adminDataApi.clear(); ElMessage.success('所有数据已清除')
  } catch { /* */ }
}

async function handleRotateKeys() {
  try {
    await ElMessageBox.confirm('密钥轮换后旧备份可能无法恢复，确认继续？', '确认轮换', { type: 'warning' })
    rotatingKeys.value = true
    await adminDataApi.rotateKeys()
    storageStatus.keyRotationAt = new Date().toISOString()
    ElMessage.success('密钥已轮换')
  } catch { /* */ }
  finally { rotatingKeys.value = false }
}
</script>

<template>
  <div class="admin-page">
    <div class="page-header">
      <h2 class="page-title">数据管理</h2>
    </div>

    <!-- 存储健康 -->
    <el-row :gutter="16">
      <el-col :xs="24" :sm="12" :md="6">
        <el-card shadow="hover" class="stat-card">
          <div class="stat-label">数据库大小</div>
          <div class="stat-value">{{ storageStatus.dbSizeMB }} <small>MB</small></div>
        </el-card>
      </el-col>
      <el-col :xs="24" :sm="12" :md="6">
        <el-card shadow="hover" class="stat-card">
          <div class="stat-label">备份数量</div>
          <div class="stat-value">{{ storageStatus.backupCount }} <small>份</small></div>
        </el-card>
      </el-col>
      <el-col :xs="24" :sm="12" :md="6">
        <el-card shadow="hover" class="stat-card">
          <div class="stat-label">上次备份</div>
          <div class="stat-value-small">{{ new Date(storageStatus.lastBackupAt).toLocaleString('zh-CN') }}</div>
        </el-card>
      </el-col>
      <el-col :xs="24" :sm="12" :md="6">
        <el-card shadow="hover" class="stat-card">
          <div class="stat-label">健康状态</div>
          <div class="stat-value">
            <el-tag :type="storageStatus.healthOk?'success':'danger'">{{ storageStatus.healthOk ? '正常' : '异常' }}</el-tag>
          </div>
        </el-card>
      </el-col>
    </el-row>

    <!-- 操作 -->
    <el-row :gutter="16" style="margin-top:16px">
      <el-col :xs="24" :md="8">
        <el-card shadow="hover"><template #header>加密备份</template>
          <p class="card-desc">导出加密的完整数据库备份文件</p>
          <el-button :icon="Refresh" :loading="backupLoading" @click="handleBackup" style="width:100%">立即备份</el-button>
        </el-card>
      </el-col>
      <el-col :xs="24" :md="8">
        <el-card shadow="hover"><template #header>数据恢复</template>
          <p class="card-desc">从备份文件恢复全部数据（覆盖当前）</p>
          <el-button :icon="UploadFilled" @click="handleRestore" style="width:100%">选择备份文件</el-button>
        </el-card>
      </el-col>
      <el-col :xs="24" :md="8">
        <el-card shadow="hover"><template #header>密钥管理</template>
          <p class="card-desc">上次轮换：{{ new Date(storageStatus.keyRotationAt).toLocaleString('zh-CN') }}</p>
          <el-button :icon="Refresh" :loading="rotatingKeys" @click="handleRotateKeys" style="width:100%" type="warning">轮换加密密钥</el-button>
        </el-card>
      </el-col>
    </el-row>

    <el-card shadow="hover" style="margin-top:16px;border-color:var(--el-color-danger)">
      <template #header><span style="color:var(--el-color-danger)">⚠️ 危险操作</span></template>
      <p class="card-desc">清除所有数据（含账号/设备/策略/使用记录），此操作不可恢复！</p>
      <el-button type="danger" :icon="Delete" @click="handleClearAll">清除所有数据</el-button>
    </el-card>
  </div>
</template>

<style scoped>
.admin-page { max-width: 1000px; }
.page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; }
.page-title { font-size: 22px; font-weight: 600; margin: 0; }
.stat-card { text-align: center; cursor: default; }
.stat-label { font-size: 13px; color: var(--el-text-color-secondary); margin-bottom: 4px; }
.stat-value { font-size: 24px; font-weight: 700; color: var(--el-text-color-primary); }
.stat-value small { font-size: 13px; font-weight: 400; }
.stat-value-small { font-size: 13px; color: var(--el-text-color-primary); }
.card-desc { font-size: 13px; color: var(--el-text-color-secondary); margin: 0 0 12px; line-height: 1.5; }
</style>
