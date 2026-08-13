<template>
  <div class="download-page">
    <header class="dl-header">
      <div class="dl-brand">
        <span class="dl-logo">🐤</span>
        <span class="dl-title">小趴菜 · 下载中心</span>
      </div>
      <div class="dl-actions">
        <el-button v-if="loggedIn" text @click="go('/dashboard')">返回控制台</el-button>
        <el-button v-else text @click="go('/login')">登录 / 管理后台</el-button>
      </div>
    </header>

    <main class="dl-main">
      <h1 class="dl-heading">下载最新客户端</h1>
      <p class="dl-sub">小趴菜守护 · 家长监控客户端（Android / Windows / iOS 陆续上线）</p>

      <div class="dl-grid">
        <el-card class="dl-card" shadow="hover">
          <div class="dl-icon">📱</div>
          <h3>Android 客户端</h3>
          <p class="dl-desc">儿童守护 / 家长端二合一<br />Android 8.0 及以上</p>
          <p class="dl-meta">APK · 约 40.9 MB</p>
          <el-button type="primary" size="large" class="dl-btn" @click="download('/downloads/XiaopacaiParent-1.0.0-debug.apk')">
            下载 APK
          </el-button>
        </el-card>

        <el-card class="dl-card" shadow="hover">
          <div class="dl-icon">💻</div>
          <h3>Windows 桌面端</h3>
          <p class="dl-desc">家长端桌面客户端<br />Windows 10/11 x64</p>
          <p class="dl-meta">ZIP · 约 77.4 MB</p>
          <el-button type="primary" size="large" class="dl-btn" @click="download('/downloads/XiaopacaiParent-1.0.0-win-x64.zip')">
            下载 Windows
          </el-button>
        </el-card>

        <el-card class="dl-card" shadow="hover">
          <div class="dl-icon">🍎</div>
          <h3>iOS 客户端</h3>
          <p class="dl-desc">iPhone / iPad<br />敬请期待</p>
          <p class="dl-meta">即将上线</p>
          <el-button size="large" class="dl-btn" disabled>敬请期待</el-button>
        </el-card>

        <el-card class="dl-card" shadow="hover">
          <div class="dl-icon">🛠️</div>
          <h3>电脑一键授权脚本</h3>
          <p class="dl-desc">孩子手机快速开通守护权限<br />Windows · 双击运行 · 约30秒</p>
          <p class="dl-meta">BAT · 使用方法见小趴菜家长端首页</p>
          <el-button type="success" size="large" class="dl-btn" @click="download('/downloads/xiaopacai-adb-grant.bat')">
            下载脚本
          </el-button>
        </el-card>
      </div>

      <footer class="dl-footer">
        <span>小趴菜 · 开源免费 · 本地优先 · 数据不上云</span>
      </footer>
    </main>
  </div>
</template>

<script setup lang="ts">
// 小趴菜 Web 3.0 — 下载中心（登录前后均可访问）
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'

const router = useRouter()
const loggedIn = ref(false)

onMounted(() => {
  loggedIn.value = !!localStorage.getItem('access_token')
})

function go(path: string) {
  router.push(path)
}

function download(url: string) {
  // 走浏览器直接下载（静态文件由后端托管）
  window.open(url, '_blank')
}
</script>

<style scoped>
.download-page {
  min-height: 100vh;
  background: var(--el-bg-color-page, #f5f7fa);
}

.dl-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 28px;
  background: #fff;
  box-shadow: 0 1px 4px rgba(0, 0, 0, 0.06);
}

.dl-brand {
  display: flex;
  align-items: center;
  gap: 10px;
}

.dl-logo {
  font-size: 26px;
}

.dl-title {
  font-size: 18px;
  font-weight: 600;
  color: var(--el-text-color-primary);
}

.dl-main {
  max-width: 1080px;
  margin: 0 auto;
  padding: 48px 24px;
  text-align: center;
}

.dl-heading {
  font-size: 28px;
  font-weight: 700;
  color: var(--el-text-color-primary);
  margin: 0 0 8px;
}

.dl-sub {
  color: var(--el-text-color-secondary);
  margin: 0 0 36px;
}

.dl-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
  gap: 20px;
}

.dl-card {
  border-radius: 12px;
}

.dl-icon {
  font-size: 44px;
  margin-bottom: 10px;
}

.dl-card h3 {
  margin: 0 0 8px;
  font-size: 18px;
  color: var(--el-text-color-primary);
}

.dl-desc {
  color: var(--el-text-color-regular);
  font-size: 13px;
  line-height: 1.6;
  margin: 0 0 8px;
}

.dl-meta {
  color: var(--el-text-color-placeholder);
  font-size: 12px;
  margin: 0 0 18px;
}

.dl-btn {
  width: 100%;
}

.dl-footer {
  margin-top: 40px;
  font-size: 12px;
  color: var(--el-text-color-placeholder);
}
</style>
