<script setup lang="ts">
// 小趴菜 Web 3.0 — 登录页
import { ref, reactive } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { ElMessage } from 'element-plus'
import { UserFilled, Lock, Key } from '@element-plus/icons-vue'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const loginForm = reactive({
  username: '',
  password: '',
})
const loading = ref(false)
const loginError = ref('')

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
    await auth.login(loginForm.username, loginForm.password)
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

// 快速填入演示账号
function fillDemo(role: 'admin' | 'parent') {
  if (role === 'admin') {
    loginForm.username = 'admin'
    loginForm.password = 'admin123'
  } else {
    loginForm.username = 'parent'
    loginForm.password = 'parent123'
  }
}
</script>

<template>
  <div class="login-page">
    <div class="login-card">
      <!-- 品牌区 -->
      <div class="login-brand">
        <span class="brand-icon">🛡️</span>
        <h1 class="brand-title">小趴菜 Web 3.0</h1>
        <p class="brand-subtitle">儿童守护 · 家长控制面板</p>
      </div>

      <!-- 登录表单 -->
      <el-form
        ref="formRef"
        :model="loginForm"
        :rules="rules"
        label-position="top"
        size="large"
        class="login-form"
        @submit.prevent="handleLogin"
      >
        <el-form-item label="用户名" prop="username">
          <el-input
            v-model="loginForm.username"
            placeholder="请输入用户名"
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

      <!-- 演示账号 -->
      <div class="demo-accounts">
        <p class="demo-hint">演示账号：</p>
        <div class="demo-buttons">
          <el-button size="small" text type="primary" @click="fillDemo('parent')">
            <el-icon><UserFilled /></el-icon> 家长 (parent / parent123)
          </el-button>
          <el-button size="small" text type="warning" @click="fillDemo('admin')">
            <el-icon><Key /></el-icon> 管理员 (admin / admin123)
          </el-button>
        </div>
      </div>

      <div class="login-footer">
        <span>自托管 · 本地部署 · 数据不上云</span>
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
  font-size: 48px;
  display: block;
  margin-bottom: 12px;
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

.login-footer {
  text-align: center;
  margin-top: 24px;
  font-size: 12px;
  color: var(--el-text-color-placeholder);
}
</style>
