<script setup lang="ts">
// 小趴菜 Web 3.0 — 登录页
// [TASK-ACCOUNT-V1] 账号邮箱化：密码登录（仅邮箱）/ 验证码登录 / 扫码登录（保留）；
// 注册与找回密码均为「邮箱 → 验证码 → 完成」两步流程（reset-ticket 恢复码链路已退役）
import { ref, reactive, onBeforeUnmount } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { authApi, ticketApi } from '@/api'
import { ElMessage } from 'element-plus'
import {
  UserFilled, Lock, Message,
  Loading, CircleCheck, RefreshRight, ArrowLeft,
} from '@element-plus/icons-vue'
import { toDataURL as qrToDataURL } from 'qrcode'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

// ==================== 密码登录（仅邮箱） ====================
const loginForm = reactive({
  username: '', // [TASK-ACCOUNT-V1] 值即邮箱（服务端仅接受邮箱）
  password: '',
})
const loading = ref(false)
const loginError = ref('')

const rules = {
  username: [{ required: true, message: '请输入邮箱', trigger: 'blur' }],
  password: [{ required: true, message: '请输入密码', trigger: 'blur' }],
}

const formRef = ref()

async function handleLogin() {
  const valid = await formRef.value?.validate().catch(() => false)
  if (!valid) return

  loading.value = true
  loginError.value = ''
  try {
    // [SEC-P1] 强制改密：管理员引导账号/管理员重置口令后首次登录必须先改密（红线 R4.2）
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
    const msg = e.response?.data?.error || e.response?.data?.message || '登录失败，请检查邮箱和密码'
    loginError.value = msg
    ElMessage.error(msg)
  } finally {
    loading.value = false
  }
}

// ==================== 注册（两步：表单 → 邮箱验证码） ====================
const registerMode = ref(false)
const registerStep = ref(0) // 0=填写表单 1=输入验证码
const registerForm = reactive({
  email: '',
  displayName: '',
  password: '',
  confirmPassword: '',
  code: '',
})
const registerLoading = ref(false)
const registerError = ref('')

async function handleRegister() {
  if (!registerForm.email.includes('@')) { ElMessage.warning('请输入有效邮箱'); return }
  if (registerForm.password.length < 8) { ElMessage.warning('密码至少 8 位'); return }
  if (!/\d/.test(registerForm.password) || !/[a-zA-Z]/.test(registerForm.password)) {
    ElMessage.warning('密码需同时包含字母与数字'); return
  }
  if (registerForm.password !== registerForm.confirmPassword) { ElMessage.warning('两次密码不一致'); return }
  if (registerStep.value === 0) {
    // 步骤 1：发送邮箱验证码
    registerLoading.value = true
    registerError.value = ''
    try {
      await authApi.emailCode(registerForm.email, 'register')
      registerStep.value = 1
      startSectionCountdown('register')
      ElMessage.success('验证码已发送，请查收邮件')
    } catch (e: any) {
      const msg = e.response?.data?.error || '验证码发送失败，请稍后重试'
      registerError.value = msg
      ElMessage.error(msg)
    } finally {
      registerLoading.value = false
    }
    return
  }
  // 步骤 2：验证码 + 注册
  if (!/^\d{6}$/.test(registerForm.code)) { ElMessage.warning('请输入 6 位验证码'); return }
  registerLoading.value = true
  registerError.value = ''
  try {
    const res = await authApi.register(
      registerForm.email, registerForm.code, registerForm.password,
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

function toggleRegister() {
  registerMode.value = !registerMode.value
  registerStep.value = 0
  registerForm.code = ''
  registerError.value = ''
}

// ==================== 验证码登录 ====================
const codeLoginForm = reactive({ email: '', code: '' })
const codeLoginLoading = ref(false)
const codeLoginError = ref('')

async function sendLoginCode() {
  if (!codeLoginForm.email.includes('@')) { ElMessage.warning('请输入有效邮箱'); return }
  codeLoginLoading.value = true
  try {
    // 防枚举：服务端对未注册邮箱不发信，统一应答成功文案
    await authApi.emailCode(codeLoginForm.email, 'login')
    startSectionCountdown('codelogin')
    ElMessage.success('验证码已发送（若邮箱已注册）')
  } catch (e: any) {
    codeLoginError.value = e.response?.data?.error || '验证码发送失败'
    ElMessage.error(codeLoginError.value)
  } finally {
    codeLoginLoading.value = false
  }
}

async function handleCodeLogin() {
  if (!codeLoginForm.email.includes('@')) { ElMessage.warning('请输入有效邮箱'); return }
  if (!/^\d{6}$/.test(codeLoginForm.code)) { ElMessage.warning('请输入 6 位验证码'); return }
  codeLoginLoading.value = true
  codeLoginError.value = ''
  try {
    const res = await authApi.codeLogin(codeLoginForm.email, codeLoginForm.code)
    await auth.loginWithAuthResponse(res.data)
    localStorage.setItem('user_role', auth.user?.role || 'parent')
    ElMessage.success('登录成功')
    const redirect = (route.query.redirect as string) || '/dashboard'
    router.push(redirect)
  } catch (e: any) {
    const msg = e.response?.data?.error || '验证码登录失败'
    codeLoginError.value = msg
    ElMessage.error(msg)
  } finally {
    codeLoginLoading.value = false
  }
}

// ==================== 找回密码（两步：邮箱 → 验证码+新密码） ====================
const resetStep = ref(0) // 0=未进入 1=输入邮箱 2=验证码+新密码
const resetForm = reactive({ email: '', code: '', newPassword: '', confirmPassword: '' })
const resetLoading = ref(false)
const resetError = ref('')

function openResetFlow() {
  resetStep.value = 1
  resetError.value = ''
}

function cancelReset() {
  resetStep.value = 0
  resetError.value = ''
  activeTab.value = 'password'
}

async function startReset() {
  if (!resetForm.email.includes('@')) { ElMessage.warning('请输入有效邮箱'); return }
  resetLoading.value = true
  resetError.value = ''
  try {
    // 防枚举：服务端对未注册邮箱不发信，统一应答成功文案
    await authApi.emailCode(resetForm.email, 'reset_password')
    resetStep.value = 2
    startSectionCountdown('reset')
    ElMessage.success('验证码已发送（若邮箱已注册）')
  } catch (e: any) {
    const msg = e.response?.data?.error || '验证码发送失败，请稍后重试'
    resetError.value = msg
    ElMessage.error(msg)
  } finally {
    resetLoading.value = false
  }
}

async function submitReset() {
  if (!/^\d{6}$/.test(resetForm.code)) { ElMessage.warning('请输入 6 位验证码'); return }
  if (resetForm.newPassword.length < 8) { ElMessage.warning('新密码至少 8 位'); return }
  if (resetForm.newPassword !== resetForm.confirmPassword) { ElMessage.warning('两次输入的密码不一致'); return }
  resetLoading.value = true
  resetError.value = ''
  try {
    await authApi.passwordReset(resetForm.email, resetForm.code, resetForm.newPassword)
    ElMessage.success('密码已重置，请使用新密码登录')
    cancelReset()
  } catch (e: any) {
    const msg = e.response?.data?.error || '重置失败，请重试'
    resetError.value = msg
    ElMessage.error(msg)
  } finally {
    resetLoading.value = false
  }
}

// ==================== 发码倒计时（60 秒可重发） ====================
type SectionKey = 'register' | 'codelogin' | 'reset'
const sectionCountdowns = reactive<Record<SectionKey, number>>({ register: 0, codelogin: 0, reset: 0 })
const sectionTimers: Partial<Record<SectionKey, number>> = {}

function startSectionCountdown(section: SectionKey) {
  clearSectionTimer(section)
  sectionCountdowns[section] = 60
  sectionTimers[section] = window.setInterval(() => {
    sectionCountdowns[section] -= 1
    if (sectionCountdowns[section] <= 0) clearSectionTimer(section)
  }, 1000)
}

function clearSectionTimer(section: SectionKey) {
  if (sectionTimers[section]) { clearInterval(sectionTimers[section]); delete sectionTimers[section] }
}

// ==================== 登录方式 Tab ====================
const activeTab = ref<'password' | 'code' | 'qr'>('password')

// ==================== 扫码登录（需求 10，保留） ====================
// 二维码内容约定（与 Android 端 QrCodeGenerator 保持一致，JSON 格式）：
// {"type":"login_ticket","ticketUrl":"{origin}/auth/login-ticket/{ticket}","expiresAt":<epoch秒>,"action":"scan_to_login"}
// Android 家长端扫码后从 ticketUrl 中提取 ticket，
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
function buildTicketQrContent(ticket: string, expiresAt: string): string {
  const ticketUrl = `${window.location.origin}/auth/login-ticket/${ticket}`
  const expiresAtEpoch = Math.floor(new Date(expiresAt).getTime() / 1000)
  return JSON.stringify({
    type: 'login_ticket',
    ticketUrl,
    expiresAt: String(expiresAtEpoch),
    action: 'scan_to_login',
  })
}

// 启动倒计时，归零后置为过期
function startQrCountdown(seconds: number) {
  if (qrCountdownTimer) clearInterval(qrCountdownTimer)
  qrCountdown.value = seconds
  qrCountdownTimer = window.setInterval(() => {
    qrCountdown.value -= 1
    if (qrCountdown.value <= 0) {
      clearQrTimers()
      qrStatus.value = 'expired'
    }
  }, 1000)
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
      buildTicketQrContent(data.ticket, data.expiresAt),
      { width: 200, margin: 1 },
    )
    qrStatus.value = 'pending'
    startQrCountdown(data.expiresInSeconds ?? 90)
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
      startQrCountdown(qrCountdown.value)
      qrPollTimer = window.setInterval(pollQrLogin, 2000)
    }
  } else {
    clearQrTimers()
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
  clearSectionTimer('register')
  clearSectionTimer('codelogin')
  clearSectionTimer('reset')
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

      <!-- 密码登录 / 验证码登录 / 扫码登录 Tabs -->
      <el-tabs
        v-if="resetStep === 0"
        v-model="activeTab"
        class="login-tabs"
        @tab-change="onTabChange"
      >
        <!-- ===== 密码登录 ===== -->
        <el-tab-pane label="密码登录" name="password">
          <!-- 登录表单 -->
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
            <el-form-item label="邮箱" prop="username">
              <el-input
                v-model="loginForm.username"
                placeholder="请输入注册邮箱"
                :prefix-icon="Message"
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

          <!-- 注册（两步：表单 → 邮箱验证码） -->
          <el-form
            v-else
            :model="registerForm"
            label-position="top"
            size="large"
            class="login-form"
            @submit.prevent="handleRegister"
          >
            <template v-if="registerStep === 0">
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
                  placeholder="至少 8 位，含字母与数字"
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
            </template>

            <template v-else>
              <p class="flow-hint">验证码已发送至 <b>{{ registerForm.email }}</b>，5 分钟内有效</p>
              <el-form-item label="邮箱验证码" required>
                <el-input
                  v-model="registerForm.code"
                  placeholder="6 位验证码"
                  maxlength="6"
                  style="letter-spacing: 4px"
                />
              </el-form-item>
            </template>

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
                {{ registerLoading ? '处理中...' : registerStep === 0 ? '获取验证码' : '注册并登录' }}
              </el-button>
            </el-form-item>

            <div v-if="registerStep === 1" class="resend-row">
              <el-button
                text
                type="primary"
                :disabled="sectionCountdowns.register > 0"
                @click="authApi.emailCode(registerForm.email, 'register').then(() => { startSectionCountdown('register'); ElMessage.success('验证码已重新发送') }).catch((e: any) => ElMessage.error(e.response?.data?.error || '发送失败'))"
              >
                {{ sectionCountdowns.register > 0 ? `${sectionCountdowns.register} 秒后可重发` : '重新发送验证码' }}
              </el-button>
              <el-button text @click="registerStep = 0">返回修改</el-button>
            </div>
          </el-form>

          <!-- 忘记密码 / 注册入口 -->
          <div class="login-extra">
            <el-link type="primary" class="no-underline-link" @click="openResetFlow">忘记密码？</el-link>
            <el-link
              type="primary"
              class="no-underline-link"
              style="margin-left: 16px"
              @click="toggleRegister"
            >
              {{ registerMode ? '已有账号？返回登录' : '没有账号？立即注册' }}
            </el-link>
          </div>
        </el-tab-pane>

        <!-- ===== 验证码登录 ===== -->
        <el-tab-pane label="验证码登录" name="code">
          <el-form
            :model="codeLoginForm"
            label-position="top"
            size="large"
            class="login-form"
            @submit.prevent="handleCodeLogin"
          >
            <el-form-item label="邮箱" required>
              <el-input
                v-model="codeLoginForm.email"
                placeholder="请输入注册邮箱"
                autocomplete="email"
              />
            </el-form-item>

            <el-form-item label="验证码" required>
              <div class="code-row">
                <el-input
                  v-model="codeLoginForm.code"
                  placeholder="6 位验证码"
                  maxlength="6"
                  style="letter-spacing: 4px"
                />
                <el-button
                  :disabled="sectionCountdowns.codelogin > 0"
                  :loading="codeLoginLoading && !codeLoginForm.code"
                  @click="sendLoginCode"
                >
                  {{ sectionCountdowns.codelogin > 0 ? `${sectionCountdowns.codelogin} 秒` : '获取验证码' }}
                </el-button>
              </div>
            </el-form-item>

            <el-alert
              v-if="codeLoginError"
              :title="codeLoginError"
              type="error"
              show-icon
              :closable="true"
              @close="codeLoginError = ''"
              style="margin-bottom: 12px"
            />

            <el-form-item>
              <el-button
                type="primary"
                :loading="codeLoginLoading"
                style="width: 100%"
                @click="handleCodeLogin"
              >
                {{ codeLoginLoading ? '登录中...' : '登 录' }}
              </el-button>
            </el-form-item>
          </el-form>
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

      <!-- ===== 找回密码流程（两步：邮箱 → 验证码+新密码） ===== -->
      <div v-else class="reset-flow">
        <!-- 步骤 1：输入邮箱 -->
        <template v-if="resetStep === 1">
          <h3 class="reset-title">找回密码</h3>
          <p class="reset-hint">输入注册邮箱，我们将发送重置验证码</p>
          <el-input v-model="resetForm.email" placeholder="注册邮箱" size="large" />
          <el-alert
            v-if="resetError"
            :title="resetError"
            type="error"
            show-icon
            :closable="true"
            @close="resetError = ''"
            style="margin-top: 12px"
          />
          <el-button
            type="primary"
            size="large"
            style="width: 100%; margin-top: 16px"
            :loading="resetLoading"
            @click="startReset"
          >
            获取验证码
          </el-button>
        </template>

        <!-- 步骤 2：验证码 + 新密码 -->
        <template v-else-if="resetStep === 2">
          <h3 class="reset-title">设置新密码</h3>
          <p class="reset-hint">验证码已发送至 <b>{{ resetForm.email }}</b>，5 分钟内有效</p>
          <el-input
            v-model="resetForm.code"
            placeholder="6 位验证码"
            maxlength="6"
            size="large"
            style="letter-spacing: 4px"
          />
          <el-input
            v-model="resetForm.newPassword"
            type="password"
            show-password
            placeholder="新密码（至少 8 位，含字母与数字）"
            size="large"
            style="margin-top: 12px"
          />
          <el-input
            v-model="resetForm.confirmPassword"
            type="password"
            show-password
            placeholder="确认新密码"
            size="large"
            style="margin-top: 12px"
          />
          <el-alert
            v-if="resetError"
            :title="resetError"
            type="error"
            show-icon
            :closable="true"
            @close="resetError = ''"
            style="margin-top: 12px"
          />
          <el-button
            type="primary"
            size="large"
            style="width: 100%; margin-top: 16px"
            :loading="resetLoading"
            @click="submitReset"
          >
            提交新密码
          </el-button>
          <div class="resend-row">
            <el-button
              text
              type="primary"
              :disabled="sectionCountdowns.reset > 0"
              @click="startReset"
            >
              {{ sectionCountdowns.reset > 0 ? `${sectionCountdowns.reset} 秒后可重发` : '重新发送验证码' }}
            </el-button>
          </div>
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

/* 网页管理入口（IP + 域名双地址） */
.web-console-links {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  flex-wrap: wrap;
  margin-top: 18px;
  padding-top: 14px;
  border-top: 1px solid var(--el-border-color-lighter);
}

.web-console-label {
  font-size: 13px;
  color: var(--el-text-color-secondary);
}

.web-console-sep {
  color: var(--el-text-color-secondary);
}

/* [TASK-PRELAUNCH-P1-FIX] 替代 el-link 废弃的 underline 属性 */
.no-underline-link :deep(.el-link__inner) {
  text-decoration: none;
}

/* 验证码输入行（输入框 + 获取按钮） */
.code-row {
  display: flex;
  gap: 8px;
  width: 100%;
}

.code-row .el-input {
  flex: 1;
}

.code-row .el-button {
  flex-shrink: 0;
}

/* 两步流程提示与重发行 */
.flow-hint {
  font-size: 13px;
  color: var(--el-text-color-secondary);
  margin: 0 0 12px;
}

.resend-row {
  display: flex;
  justify-content: center;
  gap: 12px;
  margin-top: -8px;
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
