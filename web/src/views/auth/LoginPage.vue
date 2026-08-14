<script setup lang="ts">
// 小趴菜 Web 3.0 — 登录页（密码登录 / 扫码登录 / 忘记密码，OPT12 需求 10/12）
import { ref, reactive, onBeforeUnmount } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { authApi, ticketApi } from '@/api'
import { ElMessage } from 'element-plus'
import {
  UserFilled, Lock,
  Loading, CircleCheck, RefreshRight, ArrowLeft,
} from '@element-plus/icons-vue'
import { toDataURL as qrToDataURL } from 'qrcode'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

// ==================== 密码登录 ====================
const loginForm = reactive({
  username: '',
  password: '',
})
const loading = ref(false)
const loginError = ref('')

// ==================== 注册（邮箱账号） ====================
const registerMode = ref(false)
const registerForm = reactive({
  email: '',
  displayName: '',
  password: '',
  confirmPassword: '',
})
const registerLoading = ref(false)
const registerError = ref('')

async function handleRegister() {
  if (!registerForm.email.includes('@')) { ElMessage.warning('请输入有效邮箱'); return }
  if (registerForm.password.length < 8) { ElMessage.warning('密码至少 8 位'); return }
  if (registerForm.password !== registerForm.confirmPassword) { ElMessage.warning('两次密码不一致'); return }
  registerLoading.value = true
  registerError.value = ''
  try {
    const res = await authApi.register(
      registerForm.email,
      registerForm.password,
      registerForm.displayName || undefined,
    )
    await auth.loginWithAuthResponse(res.data)
    localStorage.setItem('user_role', auth.user?.role || 'parent')
    ElMessage.success('注册成功，已自动登录')
    const redirect = (route.query.redirect as string) || '/dashboard'
    router.push(redirect)
  } catch (e: any) {
    const msg = e.response?.data?.error || '注册失败，请稍后重试'
    registerError.value = msg
    ElMessage.error(msg)
  } finally {
    registerLoading.value = false
  }
}

// 登录表单校验规则
const rules = {
  username: [{ required: true, message: '请输入用户名', trigger: 'blur' }],
  password: [{ required: true, message: '请输入密码', trigger: 'blur' }],
}

const formRef = ref()

async function handleLogin() {
  const valid = await formRef.value?.validate().catch(() => false)
  if (!valid) return

  loading.value = true
  loginError.value = ''
  try {
    // [SEC-P1] 强制改密：种子账号/管理员重置口令后首次登录必须先改密（红线 R4.2）
    const mustChange = await auth.login(loginForm.username, loginForm.password)
    if (mustChange) {
      ElMessage.warning('首次登录请先修改密码')
      router.push({ path: '/settings', query: { mustChange: '1' } })
      return
    }
    // 保存角色到 localStorage（路由守卫用）
    localStorage.setItem('user_role', auth.user?.role || 'parent')
    ElMessage.success('登录成功')
    const redirect = (route.query.redirect as string) || '/dashboard'
    router.push(redirect)
  } catch (e: any) {
    const msg = e.response?.data?.message || '登录失败，请检查用户名和密码'
    loginError.value = msg
    ElMessage.error(msg)
  } finally {
    loading.value = false
  }
}

// ==================== 登录方式 Tab ====================
const activeTab = ref<'password' | 'qr'>('password')

// ==================== 扫码登录（需求 10） ====================
// 二维码内容约定（与 Android 端 QrCodeGenerator 保持一致，JSON 格式）：
// {"type":"login_ticket","ticketUrl":"{origin}/auth/login-ticket/{ticket}","expiresAt":<epoch秒>,"action":"scan_to_login"}
// Android 家长端（P3）扫码后从 ticketUrl 中提取 ticket，
// 调用 POST /api/auth/login-ticket/{ticket}/confirm（需家长端登录态）确认。
const qrStatus = ref<'idle' | 'loading' | 'pending' | 'confirmed' | 'expired'>('idle')
const qrDataUrl = ref('')
const qrTicket = ref('')
const qrCountdown = ref(0)
const qrError = ref('')
let qrPollTimer: number | undefined
let qrCountdownTimer: number | undefined
let qrPolling = false

// 清理扫码登录相关定时器
function clearQrTimers() {
  if (qrPollTimer) { clearInterval(qrPollTimer); qrPollTimer = undefined }
  if (qrCountdownTimer) { clearInterval(qrCountdownTimer); qrCountdownTimer = undefined }
}

// 生成 Ticket 二维码内容（JSON 格式，与 Android 端 QrCodeGenerator 约定一致）
function buildTicketQrContent(kind: 'login' | 'reset', ticket: string, expiresAt: string, username?: string): string {
  const ticketUrl = `${window.location.origin}/auth/${kind}-ticket/${ticket}`
  const expiresAtEpoch = Math.floor(new Date(expiresAt).getTime() / 1000)
  const payload: Record<string, string> = {
    type: kind === 'login' ? 'login_ticket' : 'reset_ticket',
    ticketUrl,
    expiresAt: String(expiresAtEpoch),
    action: kind === 'login' ? 'scan_to_login' : 'confirm_reset',
  }
  if (username) payload.username = username
  return JSON.stringify(payload)
}

// 启动倒计时，归零后置为过期
function startCountdown(target: 'qr' | 'reset', seconds: number) {
  const setValue = (v: number) => {
    if (target === 'qr') qrCountdown.value = v
    else resetCountdown.value = v
  }
  const timer = target === 'qr' ? qrCountdownTimer : resetCountdownTimer
  if (timer) clearInterval(timer)
  setValue(seconds)
  const newTimer = window.setInterval(() => {
    const cur = target === 'qr' ? qrCountdown.value : resetCountdown.value
    setValue(cur - 1)
    if (cur - 1 <= 0) {
      if (target === 'qr') {
        clearQrTimers()
        qrStatus.value = 'expired'
      } else {
        clearResetTimers()
        resetStatus.value = 'expired'
      }
    }
  }, 1000)
  if (target === 'qr') qrCountdownTimer = newTimer
  else resetCountdownTimer = newTimer
}

// 轮询扫码登录状态（每 2 秒），确认后自动登录
async function pollQrLogin() {
  if (qrPolling || !qrTicket.value) return
  qrPolling = true
  try {
    const res = await ticketApi.pollLogin(qrTicket.value)
    const data = res.data
    if (data.status === 'confirmed') {
      clearQrTimers()
      qrStatus.value = 'confirmed'
      if (data.auth?.accessToken) {
        // [SEC-K5] 服务端已写入 httpOnly Cookie 会话；Body 中 token 仅作兼容，本地不持久化
        await auth.loginWithAuthResponse(data.auth)
        localStorage.setItem('user_role', auth.user?.role || 'parent')
        ElMessage.success('扫码登录成功')
        const redirect = (route.query.redirect as string) || '/dashboard'
        router.push(redirect)
      } else {
        // 异常兜底：凭证缺失（如账号被停用），引导刷新重试
        qrError.value = '登录凭证异常，请刷新二维码重试'
        qrStatus.value = 'expired'
      }
    } else if (data.status === 'expired') {
      clearQrTimers()
      qrStatus.value = 'expired'
    } else {
      // 仍为 pending：同步剩余秒数
      qrCountdown.value = data.expiresInSeconds ?? qrCountdown.value
    }
  } catch {
    // 网络抖动忽略，等待下一轮轮询
  } finally {
    qrPolling = false
  }
}

// 生成扫码登录 Ticket + 二维码
async function generateLoginQr() {
  clearQrTimers()
  qrStatus.value = 'loading'
  qrError.value = ''
  try {
    const res = await ticketApi.createLogin()
    const data = res.data
    qrTicket.value = data.ticket
    qrDataUrl.value = await qrToDataURL(
      buildTicketQrContent('login', data.ticket, data.expiresAt),
      { width: 200, margin: 1 },
    )
    qrStatus.value = 'pending'
    startCountdown('qr', data.expiresInSeconds ?? 90)
    qrPollTimer = window.setInterval(pollQrLogin, 2000)
  } catch {
    qrStatus.value = 'expired'
    qrError.value = '二维码生成失败，请检查网络后重试'
  }
}

// Tab 切换：进入扫码 Tab 时自动生成二维码；离开时停止轮询，切回时恢复
function onTabChange(name: string | number) {
  if (name === 'qr') {
    if (qrStatus.value === 'idle' || qrStatus.value === 'expired') {
      generateLoginQr()
    } else if (qrStatus.value === 'pending' && !qrPollTimer) {
      // 从其他 Tab 切回：恢复倒计时与 2 秒轮询
      startCountdown('qr', qrCountdown.value)
      qrPollTimer = window.setInterval(pollQrLogin, 2000)
    }
  } else {
    clearQrTimers()
  }
}

// ==================== 忘记密码（需求 12） ====================
// 流程：输入账号 → 生成重置 Ticket（10 分钟）→ 展示二维码等家长 APP 扫码确认 →
// 确认后设置新密码 → 提交（成功后吊销全部 refresh token）
const resetStep = ref(0) // 0=未进入 1=输入账号 2=扫码确认 3=设置新密码
const resetUsername = ref('')
const resetStatus = ref<'pending' | 'confirmed' | 'expired'>('pending')
const resetDataUrl = ref('')
const resetTicket = ref('')
const resetCountdown = ref(0)
const resetForm = reactive({ newPassword: '', confirmPassword: '' })
const resetSubmitting = ref(false)
let resetPollTimer: number | undefined
let resetCountdownTimer: number | undefined

// 清理重置流程相关定时器
function clearResetTimers() {
  if (resetPollTimer) { clearInterval(resetPollTimer); resetPollTimer = undefined }
  if (resetCountdownTimer) { clearInterval(resetCountdownTimer); resetCountdownTimer = undefined }
}

// 进入忘记密码流程（登录页"忘记密码"链接）
function openResetFlow() {
  clearQrTimers()
  resetStep.value = 1
  resetUsername.value = ''
  resetForm.newPassword = ''
  resetForm.confirmPassword = ''
}

// 退出忘记密码流程，回到密码登录
function cancelReset() {
  clearResetTimers()
  resetStep.value = 0
  activeTab.value = 'password'
}

// 步骤 1 → 2：校验账号并生成重置 Ticket + 二维码
async function startReset() {
  const username = resetUsername.value.trim()
  if (!username) { ElMessage.warning('请输入家长账号'); return }
  clearResetTimers() // 防止重复点击产生多个轮询定时器
  resetStatus.value = 'pending'
  try {
    const res = await ticketApi.createReset(username)
    const data = res.data
    resetTicket.value = data.ticket
    resetDataUrl.value = await qrToDataURL(
      buildTicketQrContent('reset', data.ticket, data.expiresAt, username),
      { width: 200, margin: 1 },
    )
    resetStep.value = 2
    startCountdown('reset', data.expiresInSeconds ?? 600)
    resetPollTimer = window.setInterval(pollReset, 2000)
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '生成重置二维码失败，请重试')
  }
}

// 轮询重置 Ticket 状态（每 2 秒），确认后进入设置新密码步骤
async function pollReset() {
  if (!resetTicket.value) return
  try {
    const res = await ticketApi.pollReset(resetTicket.value)
    const data = res.data
    if (data.status === 'confirmed') {
      clearResetTimers()
      resetStatus.value = 'confirmed'
      resetStep.value = 3
    } else if (data.status === 'expired') {
      clearResetTimers()
      resetStatus.value = 'expired'
    } else {
      resetCountdown.value = data.expiresInSeconds ?? resetCountdown.value
    }
  } catch {
    // 网络抖动忽略，等待下一轮轮询
  }
}

// 步骤 3：提交新密码
async function submitReset() {
  if (resetForm.newPassword.length < 8) { ElMessage.warning('新密码至少 8 位'); return }
  if (resetForm.newPassword !== resetForm.confirmPassword) { ElMessage.warning('两次输入的密码不一致'); return }
  resetSubmitting.value = true
  try {
    await ticketApi.resetPassword(resetTicket.value, resetForm.newPassword)
    ElMessage.success('密码已重置，请使用新密码登录')
    cancelReset()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '重置失败，请重试')
  } finally {
    resetSubmitting.value = false
  }
}

// 倒计时显示（mm:ss 或 n 秒）
function formatCountdown(seconds: number): string {
  const s = Math.max(0, seconds)
  if (s < 60) return `${s} 秒`
  const m = Math.floor(s / 60)
  const r = s % 60
  return `${m}:${String(r).padStart(2, '0')}`
}

// 组件卸载时清理所有定时器
onBeforeUnmount(() => {
  clearQrTimers()
  clearResetTimers()
})
</script>

<template>
  <div class="login-page">
    <div class="login-card">
      <!-- 品牌区 -->
      <div class="login-brand">
        <img src="/logo.png" alt="小趴菜" class="brand-icon" />
        <h1 class="brand-title">小趴菜 Web 3.0</h1>
        <p class="brand-subtitle">儿童守护 · 家长控制面板</p>
      </div>

      <!-- 密码登录 / 扫码登录 Tabs -->
      <el-tabs
        v-if="resetStep === 0"
        v-model="activeTab"
        class="login-tabs"
        @tab-change="onTabChange"
      >
        <!-- ===== 密码登录 ===== -->
        <el-tab-pane label="密码登录" name="password">
          <el-form
            v-if="!registerMode"
            ref="formRef"
            :model="loginForm"
            :rules="rules"
            label-position="top"
            size="large"
            class="login-form"
            @submit.prevent="handleLogin"
          >
            <el-form-item label="用户名 / 邮箱" prop="username">
              <el-input
                v-model="loginForm.username"
                placeholder="请输入用户名或邮箱"
                :prefix-icon="UserFilled"
                autocomplete="username"
              />
            </el-form-item>

            <el-form-item label="密码" prop="password">
              <el-input
                v-model="loginForm.password"
                type="password"
                placeholder="请输入密码"
                :prefix-icon="Lock"
                show-password
                autocomplete="current-password"
              />
            </el-form-item>

            <el-alert
              v-if="loginError"
              :title="loginError"
              type="error"
              show-icon
              :closable="true"
              @close="loginError = ''"
              style="margin-bottom: 12px"
            />

            <el-form-item>
              <el-button
                type="primary"
                :loading="loading"
                style="width: 100%"
                @click="handleLogin"
              >
                {{ loading ? '登录中...' : '登 录' }}
              </el-button>
            </el-form-item>
          </el-form>

          <!-- 注册（邮箱账号） -->
          <el-form
            v-else
            :model="registerForm"
            label-position="top"
            size="large"
            class="login-form"
            @submit.prevent="handleRegister"
          >
            <el-form-item label="邮箱" required>
              <el-input
                v-model="registerForm.email"
                placeholder="请输入邮箱（作为唯一登录账号）"
                autocomplete="email"
              />
            </el-form-item>

            <el-form-item label="昵称（可选）">
              <el-input
                v-model="registerForm.displayName"
                placeholder="家长称呼"
                :prefix-icon="UserFilled"
              />
            </el-form-item>

            <el-form-item label="密码" required>
              <el-input
                v-model="registerForm.password"
                type="password"
                placeholder="至少 8 位"
                :prefix-icon="Lock"
                show-password
                autocomplete="new-password"
              />
            </el-form-item>

            <el-form-item label="确认密码" required>
              <el-input
                v-model="registerForm.confirmPassword"
                type="password"
                placeholder="再次输入密码"
                :prefix-icon="Lock"
                show-password
                autocomplete="new-password"
              />
            </el-form-item>

            <el-alert
              v-if="registerError"
              :title="registerError"
              type="error"
              show-icon
              :closable="true"
              @close="registerError = ''"
              style="margin-bottom: 12px"
            />

            <el-form-item>
              <el-button
                type="primary"
                :loading="registerLoading"
                style="width: 100%"
                @click="handleRegister"
              >
                {{ registerLoading ? '注册中...' : '注册并登录' }}
              </el-button>
            </el-form-item>
          </el-form>

          <!-- 忘记密码入口（需求 12） -->
          <div class="login-extra">
            <!-- [TASK-PRELAUNCH-P1-FIX] 移除已废弃的 el-link underline 属性，用 CSS 控制下划线 -->
            <el-link type="primary" class="no-underline-link" @click="openResetFlow">忘记密码？</el-link>
            <el-link
              type="primary"
              class="no-underline-link"
              style="margin-left: 16px"
              @click="registerMode = !registerMode; registerError = ''"
            >
              {{ registerMode ? '已有账号？返回登录' : '没有账号？立即注册' }}
            </el-link>
          </div>

        </el-tab-pane>

        <!-- ===== 扫码登录（需求 10） ===== -->
        <el-tab-pane label="扫码登录" name="qr">
          <div class="qr-area">
            <!-- 等待扫码确认 -->
            <template v-if="qrStatus === 'pending'">
              <img :src="qrDataUrl" alt="扫码登录二维码" class="qr-img" />
              <p class="qr-hint">请使用已登录的小趴菜家长端 APP 扫码确认</p>
              <p class="qr-countdown">
                二维码有效期剩余 <b>{{ formatCountdown(qrCountdown) }}</b>
              </p>
              <el-button size="small" text :icon="RefreshRight" @click="generateLoginQr">刷新二维码</el-button>
            </template>

            <!-- 生成中 -->
            <div v-else-if="qrStatus === 'loading'" class="qr-placeholder">
              <el-icon class="is-loading" :size="36"><Loading /></el-icon>
              <p>正在生成二维码...</p>
            </div>

            <!-- 过期 / 生成失败 -->
            <div v-else-if="qrStatus === 'idle' || qrStatus === 'expired'" class="qr-placeholder">
              <p v-if="qrError" class="qr-error">{{ qrError }}</p>
              <p v-else>二维码已过期，请刷新后重试</p>
              <el-button type="primary" :icon="RefreshRight" @click="generateLoginQr">刷新二维码</el-button>
            </div>

            <!-- 已确认，跳转中 -->
            <div v-else class="qr-placeholder">
              <el-icon :size="36" style="color: var(--el-color-success)"><CircleCheck /></el-icon>
              <p>登录成功，正在跳转...</p>
            </div>
          </div>
        </el-tab-pane>
      </el-tabs>

      <!-- ===== 忘记密码流程（需求 12） ===== -->
      <div v-else class="reset-flow">
        <!-- 步骤 1：输入账号 -->
        <template v-if="resetStep === 1">
          <h3 class="reset-title">找回密码</h3>
          <p class="reset-hint">输入需要重置密码的家长账号，用已登录的小趴菜 APP 扫码确认身份</p>
          <el-input v-model="resetUsername" placeholder="家长账号" size="large" />
          <el-button type="primary" size="large" style="width: 100%; margin-top: 16px" @click="startReset">
            下一步
          </el-button>
        </template>

        <!-- 步骤 2：扫码确认身份 -->
        <template v-else-if="resetStep === 2">
          <h3 class="reset-title">扫码确认身份</h3>
          <p class="reset-hint">请使用已登录的小趴菜家长端 APP 扫描二维码</p>
          <img :src="resetDataUrl" alt="重置密码二维码" class="qr-img" />
          <p class="qr-countdown">
            二维码有效期剩余 <b>{{ formatCountdown(resetCountdown) }}</b>
          </p>
          <div v-if="resetStatus === 'expired'" class="reset-expired">
            <p>二维码已过期</p>
            <el-button type="primary" size="small" @click="startReset">重新生成</el-button>
          </div>
        </template>

        <!-- 步骤 3：设置新密码 -->
        <template v-else-if="resetStep === 3">
          <h3 class="reset-title">设置新密码</h3>
          <p class="reset-hint">身份已确认，请设置新的登录密码（至少 8 位）</p>
          <el-input
            v-model="resetForm.newPassword"
            type="password"
            show-password
            placeholder="新密码（至少 8 位）"
            size="large"
          />
          <el-input
            v-model="resetForm.confirmPassword"
            type="password"
            show-password
            placeholder="确认新密码"
            size="large"
            style="margin-top: 12px"
          />
          <el-button
            type="primary"
            size="large"
            style="width: 100%; margin-top: 16px"
            :loading="resetSubmitting"
            @click="submitReset"
          >
            提交新密码
          </el-button>
        </template>

        <!-- 返回登录 -->
        <el-button text :icon="ArrowLeft" style="margin-top: 12px" @click="cancelReset">返回登录</el-button>
      </div>

      <div class="login-footer">
        <span>
          自托管 · 本地部署 · 数据不上云 ·
          <router-link to="/download" style="color: var(--el-color-primary)">下载客户端</router-link>
        </span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.login-page {
  min-height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  background: linear-gradient(135deg, var(--el-color-primary-light-9) 0%, var(--el-bg-color-page) 50%);
}

.login-card {
  width: 420px;
  max-width: 90vw;
  padding: 40px 36px;
  background: var(--el-bg-color);
  border-radius: 12px;
  box-shadow: 0 8px 40px rgba(0, 0, 0, 0.08);
}

.login-brand {
  text-align: center;
  margin-bottom: 32px;
}

.brand-icon {
  width: 64px;
  height: 64px;
  display: block;
  margin: 0 auto 12px;
}

.brand-title {
  font-size: 24px;
  font-weight: 700;
  color: var(--el-text-color-primary);
  margin: 0 0 6px;
}

.brand-subtitle {
  font-size: 14px;
  color: var(--el-text-color-secondary);
  margin: 0;
}

.login-form {
  margin-top: 8px;
}

.login-tabs :deep(.el-tabs__header) {
  margin-bottom: 16px;
}

/* 忘记密码链接 */
.login-extra {
  text-align: center;
  margin: -4px 0 8px;
}

/* [TASK-PRELAUNCH-P1-FIX] 替代 el-link 废弃的 underline 属性 */
.no-underline-link :deep(.el-link__inner) {
  text-decoration: none;
}

.demo-accounts {
  text-align: center;
  margin-top: 8px;
}

.demo-hint {
  font-size: 12px;
  color: var(--el-text-color-placeholder);
  margin: 0 0 4px;
}

.demo-buttons {
  display: flex;
  justify-content: center;
  gap: 8px;
  flex-wrap: wrap;
}

/* ===== 扫码登录区域 ===== */
.qr-area {
  text-align: center;
  padding: 8px 0 4px;
}

.qr-img {
  width: 200px;
  height: 200px;
  border-radius: 8px;
  border: 1px solid var(--el-border-color-light);
  padding: 8px;
  background: #fff;
  display: inline-block;
}

.qr-hint {
  font-size: 13px;
  color: var(--el-text-color-secondary);
  margin: 12px 0 4px;
}

.qr-countdown {
  font-size: 13px;
  color: var(--el-text-color-regular);
  margin: 4px 0 8px;
}

.qr-placeholder {
  min-height: 240px;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 12px;
  color: var(--el-text-color-secondary);
  font-size: 14px;
}

.qr-error {
  color: var(--el-color-danger);
  font-size: 13px;
  margin: 0;
}

/* ===== 忘记密码流程 ===== */
.reset-flow {
  padding: 4px 0 4px;
}

.reset-title {
  font-size: 17px;
  font-weight: 600;
  color: var(--el-text-color-primary);
  margin: 0 0 6px;
  text-align: center;
}

.reset-hint {
  font-size: 13px;
  color: var(--el-text-color-secondary);
  margin: 0 0 16px;
  text-align: center;
}

.reset-flow .qr-img {
  display: block;
  margin: 0 auto;
}

.reset-expired {
  text-align: center;
  color: var(--el-color-danger);
  font-size: 13px;
  margin-top: 8px;
}

.reset-flow .el-button.is-text {
  width: 100%;
}

.login-footer {
  text-align: center;
  margin-top: 24px;
  font-size: 12px;
  color: var(--el-text-color-placeholder);
}
</style>
