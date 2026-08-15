<script setup lang="ts">
// 小趴菜 Web 3.0 — 管理端：邮件设置（[TASK-ACCOUNT-V1-MAILCONFIG]）
// 通道：DirectMail（阿里云邮件推送 API，主推）/ SMTP（自建或第三方）
// 配置优先级：数据库（本页）→ 环境变量 MAIL_*；Secret 用 XIAOPACAI_MASTER_KEY AES-256-GCM 加密入库
import { ref, reactive, computed, onMounted } from 'vue'
import { mailConfigApi } from '@/api'
import { ElMessage } from 'element-plus'

interface MailConfigResponse {
  channel: string
  accessKeyId?: string
  accessKeySecretMasked: boolean
  fromAddress?: string
  fromName?: string
  smtpHost?: string
  smtpPort?: number
  smtpUser?: string
  smtpPasswordMasked: boolean
  smtpUseSsl?: boolean
  masterKeyConfigured: boolean
  lastTestOk?: boolean | null
  lastTestDetail?: string | null
  lastTestAt?: string | null
}

const loading = ref(false)
const saving = ref(false)
const testing = ref(false)
const masterKeyConfigured = ref(false)
const lastTestOk = ref<boolean | null>(null)
const lastTestDetail = ref<string | null>(null)
const lastTestAt = ref<string | null>(null)

const form = reactive({
  channel: 'api', // api=DirectMail | smtp=SMTP
  accessKeyId: '',
  accessKeySecret: '', // 留空=保持不变；有值时校验主密钥后加密保存
  fromAddress: '',
  fromName: '',
  smtpHost: '',
  smtpPort: 587,
  smtpUser: '',
  smtpPassword: '', // 留空=保持不变
  smtpUseSsl: true,
})

const secretDisabled = computed(() => !masterKeyConfigured.value)

// 已保存的 Secret 脱敏回显
const accessKeySecretSet = ref(false)
const smtpPasswordSet = ref(false)

async function load() {
  loading.value = true
  try {
    const res = await mailConfigApi.get()
    const d = res.data as MailConfigResponse
    form.channel = d.channel || 'api'
    form.accessKeyId = d.accessKeyId || ''
    form.fromAddress = d.fromAddress || ''
    form.fromName = d.fromName || ''
    form.smtpHost = d.smtpHost || ''
    form.smtpPort = d.smtpPort ?? 587
    form.smtpUser = d.smtpUser || ''
    form.smtpUseSsl = d.smtpUseSsl ?? true
    masterKeyConfigured.value = d.masterKeyConfigured
    accessKeySecretSet.value = d.accessKeySecretMasked
    smtpPasswordSet.value = d.smtpPasswordMasked
    lastTestOk.value = d.lastTestOk ?? null
    lastTestDetail.value = d.lastTestDetail ?? null
    lastTestAt.value = d.lastTestAt ?? null
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '加载邮件配置失败')
  } finally {
    loading.value = false
  }
}

onMounted(load)

function buildPayload() {
  const payload: Record<string, unknown> = {
    channel: form.channel,
    fromAddress: form.fromAddress,
    fromName: form.fromName,
  }
  if (form.channel === 'api') {
    payload.accessKeyId = form.accessKeyId
    if (form.accessKeySecret) payload.accessKeySecret = form.accessKeySecret
  } else {
    payload.smtpHost = form.smtpHost
    payload.smtpPort = form.smtpPort
    payload.smtpUser = form.smtpUser
    payload.smtpUseSsl = form.smtpUseSsl
    if (form.smtpPassword) payload.smtpPassword = form.smtpPassword
  }
  return payload
}

async function handleSave() {
  if (!form.channel) { ElMessage.warning('请选择邮件通道'); return }
  if (form.channel === 'api' && !form.accessKeyId) { ElMessage.warning('请填写 AccessKeyId'); return }
  if (form.channel === 'smtp') {
    if (!form.smtpHost) { ElMessage.warning('请填写 SMTP 服务器地址'); return }
    if (!form.smtpUser) { ElMessage.warning('请填写 SMTP 用户名（通常是邮箱地址）'); return }
  }
  if (!form.fromAddress) { ElMessage.warning('请填写发件人地址'); return }
  // Secret 输入但主密钥未配置 → 服务端会拒绝，这里提前友好提示
  if ((form.accessKeySecret || form.smtpPassword) && secretDisabled.value) {
    ElMessage.error('服务端未配置 XIAOPACAI_MASTER_KEY 主密钥，无法保存 Secret。请先在服务器环境变量配置后重启服务。')
    return
  }
  saving.value = true
  try {
    await mailConfigApi.save(buildPayload())
    ElMessage.success('已保存，新配置即时生效')
    form.accessKeySecret = ''
    form.smtpPassword = ''
    await load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '保存失败')
  } finally {
    saving.value = false
  }
}

const testTo = ref('')
async function handleTest() {
  if (!testTo.value.includes('@')) { ElMessage.warning('请输入有效的收件邮箱'); return }
  testing.value = true
  try {
    await mailConfigApi.test(testTo.value)
    ElMessage.success(`测试邮件已发送至 ${testTo.value}`)
    await load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '发送失败，请检查配置与通道状态')
  } finally {
    testing.value = false
  }
}

function fmtTime(iso: string | null | undefined) {
  return iso ? new Date(iso).toLocaleString('zh-CN') : '—'
}
</script>

<template>
  <div class="admin-page mail-config-page">
    <div class="page-header">
      <h2 class="page-title">邮件设置</h2>
    </div>

    <el-alert
      type="info"
      :closable="false"
      show-icon
      class="config-alert"
      title="邮件用于：注册验证码、验证码登录、找回密码验证码。未配置时相关功能返回 503，不影响密码登录。"
    />

    <el-card v-loading="loading" shadow="never">
      <el-form :model="form" label-width="150px" label-position="right">
        <!-- 通道选择 -->
        <el-form-item label="邮件通道">
          <el-radio-group v-model="form.channel">
            <el-radio value="api">阿里云 DirectMail（推荐）</el-radio>
            <el-radio value="smtp">SMTP</el-radio>
          </el-radio-group>
          <div class="form-tip">
            DirectMail 需在阿里云开通「邮件推送」并配置发信域名；SMTP 适用于自建或第三方邮箱（如 QQ 邮箱、企业邮箱）。
          </div>
        </el-form-item>

        <!-- DirectMail 字段 -->
        <template v-if="form.channel === 'api'">
          <el-form-item label="AccessKeyId">
            <el-input v-model="form.accessKeyId" placeholder="阿里云 AccessKeyId（需拥有 DirectMail 权限）" />
          </el-form-item>
          <el-form-item label="AccessKeySecret">
            <el-input
              v-model="form.accessKeySecret"
              type="password"
              show-password
              :disabled="secretDisabled"
              :placeholder="accessKeySecretSet
                ? '已设置（留空保持不变）'
                : secretDisabled ? '未配置主密钥，无法保存' : '请输入 AccessKeySecret'"
            />
            <div class="form-tip">
              <template v-if="accessKeySecretSet">当前已保存 Secret（加密存储，不回显明文），留空保存表示保持不变。</template>
              <template v-else-if="secretDisabled">服务端未配置 XIAOPACAI_MASTER_KEY，Secret 无法加密入库，请先配置环境变量。</template>
              <template v-else>Secret 将使用 XIAOPACAI_MASTER_KEY 加密后存储，绝不回显明文。</template>
            </div>
          </el-form-item>
        </template>

        <!-- SMTP 字段 -->
        <template v-else>
          <el-form-item label="SMTP 服务器">
            <el-input v-model="form.smtpHost" placeholder="如 smtp.qq.com / smtp.exmail.qq.com" />
          </el-form-item>
          <el-form-item label="SMTP 端口">
            <el-input-number v-model="form.smtpPort" :min="1" :max="65535" />
          </el-form-item>
          <el-form-item label="SMTP 用户名">
            <el-input v-model="form.smtpUser" placeholder="通常是发件邮箱地址" />
          </el-form-item>
          <el-form-item label="SMTP 密码">
            <el-input
              v-model="form.smtpPassword"
              type="password"
              show-password
              :disabled="secretDisabled"
              :placeholder="smtpPasswordSet
                ? '已设置（留空保持不变）'
                : secretDisabled ? '未配置主密钥，无法保存' : '请输入 SMTP 密码/授权码'"
            />
            <div class="form-tip">
              <template v-if="smtpPasswordSet">当前已保存密码（加密存储，不回显明文），留空保存表示保持不变。</template>
              <template v-else-if="secretDisabled">服务端未配置 XIAOPACAI_MASTER_KEY，密码无法加密入库，请先配置环境变量。</template>
              <template v-else>密码将使用 XIAOPACAI_MASTER_KEY 加密后存储，绝不回显明文。</template>
            </div>
          </el-form-item>
          <el-form-item label="使用 SSL">
            <el-switch v-model="form.smtpUseSsl" />
            <div class="form-tip">多数服务商要求开启（QQ 邮箱 / 企业邮箱等均需 SSL）。</div>
          </el-form-item>
        </template>

        <!-- 公共发件人字段 -->
        <el-form-item label="发件人地址">
          <el-input v-model="form.fromAddress" placeholder="如 noreply@example.com（需为通道已验证发信地址）" />
        </el-form-item>
        <el-form-item label="发件人名称">
          <el-input v-model="form.fromName" placeholder="如「小趴菜」" />
        </el-form-item>

        <el-form-item>
          <el-button type="primary" :loading="saving" @click="handleSave">保存配置</el-button>
        </el-form-item>
      </el-form>
    </el-card>

    <!-- 测试发送 -->
    <el-card shadow="never" style="margin-top: 16px">
      <template #header><b>发送测试邮件</b></template>
      <div class="test-row">
        <el-input v-model="testTo" placeholder="收件邮箱（如 your@example.com）" style="max-width: 360px" />
        <el-button type="primary" :loading="testing" @click="handleTest">发送测试</el-button>
      </div>
      <el-alert
        v-if="lastTestOk !== null"
        :type="lastTestOk ? 'success' : 'error'"
        :closable="false"
        show-icon
        style="margin-top: 12px"
        :title="lastTestOk ? '最近一次测试发送成功' : '最近一次测试发送失败'"
      >
        <div v-if="lastTestDetail" class="test-detail">{{ lastTestDetail }}</div>
        <div class="test-time">时间：{{ fmtTime(lastTestAt) }}</div>
      </el-alert>
      <p v-else class="form-tip">保存配置后，可在此发送测试邮件验证通道是否可用。</p>
    </el-card>
  </div>
</template>

<style scoped>
.mail-config-page { max-width: 860px; }
.config-alert { margin-bottom: 16px; }
.form-tip { font-size: 12px; color: var(--el-text-color-placeholder); line-height: 1.5; margin-top: 4px; }
.test-row { display: flex; gap: 8px; }
.test-detail { font-size: 12px; color: inherit; margin-top: 4px; word-break: break-all; }
.test-time { font-size: 12px; opacity: 0.85; margin-top: 2px; }
</style>
