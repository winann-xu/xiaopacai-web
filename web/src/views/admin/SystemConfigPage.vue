<script setup lang="ts">
// 小趴菜 Web 3.0 — 管理端：系统设置
import { reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { adminSystemApi } from '@/api'

const config = reactive({
  webPort: 5000,
  p2pPort: 9527,
  bindAddress: '127.0.0.1',
  httpsEnabled: false,
  backupDir: './backups',
  dataRetentionDays: 90,
  maxLoginAttempts: 5,
  sessionTimeoutMinutes: 60,
})

const saving = ref(false)

async function saveConfig() {
  saving.value = true
  try {
    await adminSystemApi.save(config)
    ElMessage.success('系统配置已保存')
  } catch { ElMessage.error('保存失败') }
  finally { saving.value = false }
}
</script>

<template>
  <div class="admin-page">
    <div class="page-header">
      <h2 class="page-title">系统设置</h2>
      <el-button type="primary" :loading="saving" @click="saveConfig">保存配置</el-button>
    </div>

    <!-- [TASK-PRELAUNCH-P1] 诚实提示：当前版本这些配置仅落库，运行时尚未消费 -->
    <el-alert type="warning" :closable="false" show-icon style="margin-bottom: 16px"
      title="以下配置当前版本仅保存到数据库并写入审计日志，端口/绑定/HTTPS/登录锁定/数据清理等运行时生效能力开发中，暂以服务启动参数为准。" />

    <div class="config-grid">
      <el-card shadow="hover"><template #header>网络配置</template>
        <el-form label-position="left" label-width="160px">
          <el-form-item label="Web 服务端口">
            <el-input-number v-model="config.webPort" :min="1024" :max="65535" />
            <span class="hint">默认 5000</span>
          </el-form-item>
          <el-form-item label="P2P 监听端口">
            <el-input-number v-model="config.p2pPort" :min="1024" :max="65535" />
            <span class="hint">默认 9527</span>
          </el-form-item>
          <el-form-item label="绑定地址">
            <el-select v-model="config.bindAddress">
              <el-option label="仅本机 (127.0.0.1)" value="127.0.0.1" />
              <el-option label="全部接口 (0.0.0.0)" value="0.0.0.0" />
            </el-select>
          </el-form-item>
          <el-form-item label="启用 HTTPS">
            <el-switch v-model="config.httpsEnabled" />
            <span class="hint">需配置证书后启用</span>
          </el-form-item>
        </el-form>
      </el-card>

      <el-card shadow="hover"><template #header>数据策略</template>
        <el-form label-position="left" label-width="160px">
          <el-form-item label="备份目录">
            <el-input v-model="config.backupDir" placeholder="./backups" />
          </el-form-item>
          <el-form-item label="数据保留天数">
            <el-input-number v-model="config.dataRetentionDays" :min="7" :max="365" :step="1" />
            <span class="hint">超过此天数的记录自动清理</span>
          </el-form-item>
        </el-form>
      </el-card>

      <el-card shadow="hover"><template #header>安全配置</template>
        <el-form label-position="left" label-width="160px">
          <el-form-item label="最大登录尝试">
            <el-input-number v-model="config.maxLoginAttempts" :min="1" :max="10" />
            <span class="hint">超过后临时锁定账户</span>
          </el-form-item>
          <el-form-item label="会话超时">
            <el-input-number v-model="config.sessionTimeoutMinutes" :min="10" :max="1440" :step="10" />
            <span class="hint">分钟</span>
          </el-form-item>
        </el-form>
      </el-card>
    </div>
  </div>
</template>

<style scoped>
.admin-page { max-width: 1000px; }
.page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; }
.page-title { font-size: 22px; font-weight: 600; margin: 0; }
.config-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(440px, 1fr)); gap: 16px; }
.hint { font-size: 12px; color: var(--el-text-color-secondary); margin-left: 8px; }
</style>
