<script setup lang="ts">
// 小趴菜 Web 3.0 — 设置页面
import { ref, reactive } from 'vue'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { ElMessage, ElMessageBox } from 'element-plus'
import { settingsApi, authApi } from '@/api'
import { UploadFilled } from '@element-plus/icons-vue'

const auth = useAuthStore()
const ui = useUiStore()

const passwordForm = reactive({ oldPassword: '', newPassword: '', confirmPassword: '' })
const changingPassword = ref(false)

async function changePassword() {
  if (!passwordForm.oldPassword) { ElMessage.warning('请输入当前密码'); return }
  if (passwordForm.newPassword.length < 6) { ElMessage.warning('新密码至少6位'); return }
  if (passwordForm.newPassword !== passwordForm.confirmPassword) { ElMessage.warning('两次新密码不一致'); return }
  changingPassword.value = true
  try {
    await authApi.changePassword(passwordForm.oldPassword, passwordForm.newPassword)
    ElMessage.success('密码修改成功，请重新登录')
    passwordForm.oldPassword = ''; passwordForm.newPassword = ''; passwordForm.confirmPassword = ''
  } catch (e: any) { ElMessage.error(e.response?.data?.message || '修改失败') }
  finally { changingPassword.value = false }
}

const notifySettings = reactive({ usageWarn: true, deviceOffline: true, timeoutAlert: true, announcementPush: false })

const backupLoading = ref(false)
async function handleBackup() {
  backupLoading.value = true
  try {
    const res = await settingsApi.backup()
    const blob = new Blob([JSON.stringify(res.data)], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a'); a.href = url; a.download = `xiaopacai-backup-${new Date().toISOString().slice(0,10)}.json`; a.click()
    URL.revokeObjectURL(url); ElMessage.success('备份文件已下载')
  } catch { ElMessage.error('备份失败') } finally { backupLoading.value = false }
}

function handleRestore() {
  const input = document.createElement('input'); input.type = 'file'; input.accept = '.json'
  input.onchange = async (e: any) => {
    const file = e.target.files?.[0]; if (!file) return
    try { await ElMessageBox.confirm('恢复将覆盖当前数据，是否继续？', '确认恢复', { type: 'warning' }); await settingsApi.restore(file); ElMessage.success('数据恢复成功') } catch { /* */ }
  }; input.click()
}

async function handleClearData() {
  try { await ElMessageBox.confirm('此操作将清除所有使用数据（账号和设备配置保留），不可恢复！', '确认清除', { type: 'error', confirmButtonText: '确认清除' }); await settingsApi.clearData(); ElMessage.success('数据已清除') } catch { /* */ }
}

const serverConfig = reactive({ webPort: 5173, p2pPort: 9527, bindAddress: '127.0.0.1' })
</script>

<template>
  <div class="settings-page">
    <h2 class="page-title">设置</h2>
    <div class="settings-grid">
      <el-card shadow="hover"><template #header>账号与安全</template>
        <el-descriptions :column="1" border>
          <el-descriptions-item label="用户名">{{ auth.user?.username || '—' }}</el-descriptions-item>
          <el-descriptions-item label="角色">{{ auth.isAdmin ? '管理员' : '家长' }}</el-descriptions-item>
        </el-descriptions>
        <el-divider />
        <p class="section-label">修改密码</p>
        <el-form label-position="top" size="small">
          <el-form-item label="当前密码"><el-input v-model="passwordForm.oldPassword" type="password" show-password /></el-form-item>
          <el-form-item label="新密码"><el-input v-model="passwordForm.newPassword" type="password" show-password /></el-form-item>
          <el-form-item label="确认新密码"><el-input v-model="passwordForm.confirmPassword" type="password" show-password /></el-form-item>
          <el-button type="primary" :loading="changingPassword" @click="changePassword">修改密码</el-button>
        </el-form>
      </el-card>

      <el-card shadow="hover"><template #header>通知偏好</template>
        <el-form label-position="left" label-width="160px">
          <el-form-item label="用量预警"><el-switch v-model="notifySettings.usageWarn" /></el-form-item>
          <el-form-item label="设备离线提醒"><el-switch v-model="notifySettings.deviceOffline" /></el-form-item>
          <el-form-item label="超时停用通知"><el-switch v-model="notifySettings.timeoutAlert" /></el-form-item>
          <el-form-item label="公告推送"><el-switch v-model="notifySettings.announcementPush" /></el-form-item>
        </el-form>
      </el-card>

      <el-card shadow="hover"><template #header>数据管理</template>
        <div class="data-actions">
          <div class="data-item"><div><p class="data-label">备份数据</p><p class="data-hint">导出所有配置和使用数据</p></div><el-button :loading="backupLoading" @click="handleBackup">备份</el-button></div>
          <div class="data-item"><div><p class="data-label">恢复数据</p><p class="data-hint">从备份文件恢复数据</p></div><el-button :icon="UploadFilled" @click="handleRestore">恢复</el-button></div>
          <el-divider />
          <div class="data-item danger"><div><p class="data-label">清除使用数据</p><p class="data-hint">清除所有使用记录</p></div><el-button type="danger" @click="handleClearData">清除</el-button></div>
        </div>
      </el-card>

      <el-card shadow="hover"><template #header>Web 服务配置</template>
        <el-form label-position="left" label-width="140px">
          <el-form-item label="Web 端口"><el-input-number v-model="serverConfig.webPort" :min="1024" :max="65535" /></el-form-item>
          <el-form-item label="P2P 监听端口"><el-input-number v-model="serverConfig.p2pPort" :min="1024" :max="65535" /></el-form-item>
          <el-form-item label="绑定地址">
            <el-select v-model="serverConfig.bindAddress">
              <el-option label="仅本机 (127.0.0.1)" value="127.0.0.1" />
              <el-option label="局域网 (0.0.0.0)" value="0.0.0.0" />
            </el-select>
          </el-form-item>
          <el-form-item label="深色模式"><el-switch v-model="ui.darkMode" @change="ui.toggleDarkMode()" /></el-form-item>
        </el-form>
      </el-card>
    </div>
  </div>
</template>

<style scoped>
.settings-page { max-width: 960px; }
.page-title { font-size: 22px; font-weight: 600; margin: 0 0 20px; }
.settings-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(440px, 1fr)); gap: 16px; }
.section-label { font-size: 14px; font-weight: 600; margin: 0 0 12px; }
.data-actions { display: flex; flex-direction: column; }
.data-item { display: flex; justify-content: space-between; align-items: center; padding: 8px 0; }
.data-item.danger { color: var(--el-color-danger); }
.data-label { font-size: 14px; font-weight: 500; margin: 0; }
.data-hint { font-size: 12px; color: var(--el-text-color-secondary); margin: 2px 0 0; }
@media (max-width: 768px) { .settings-grid { grid-template-columns: 1fr; } }
</style>
